using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Ibp;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Exposes every attached iBUYPOWER keyboard / mouse on the lighting page -
/// one card per unit by default (a single fixed segment carrying the model's
/// LED table with its stock board positions), or whatever partition the user
/// drew over it - and contributes the per-zone <see cref="DeviceFrame"/>s to
/// the engine so canvas effects, brightness and identify apply uniformly.
/// Mirrors <see cref="KeebLightingDeviceProvider"/> with a list of units
/// instead of one board.
/// </summary>
public sealed class IbpPeripheralLightingDeviceProvider : ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource
{
    /// <summary>Default zone id suffix (also the engine frame id suffix).</summary>
    public const string LedsSuffix = ":leds";
    public const int LedsSegment = 0;

    private readonly IbpPeripheralHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    // Stock UV per model, computed once (the tables are static).
    private static readonly ConcurrentDictionary<IbpPeripheralModel, (float[] U, float[] V)> ModelUv = new();

    public IbpPeripheralLightingDeviceProvider(IbpPeripheralHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    public event Action? DevicesChanged;

    /// <summary>Called by the connection worker when a unit appears or disappears.</summary>
    public void OnConnectionChanged()
    {
        try { DevicesChanged?.Invoke(); } catch { /* subscriber failures shouldn't bubble */ }
    }

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        var attached = _hub.Attached;
        if (attached.Count == 0) return resp;
        var settings = _store.Load();
        foreach (var unit in attached)
        {
            resp.Devices.AddRange(BuildCards(unit, settings));
        }
        return resp;
    }

    /// <summary>Card emission for one unit's current partition; static so tests cover it without a live hub.</summary>
    internal static List<LightingDevice> BuildCards(IbpAttachedPeripheral unit, NexusSettings settings)
    {
        var structure = BuildStructure(unit);
        var zones = ZoneResolution.Resolve(structure, settings);
        var cards = new List<LightingDevice>(zones.Count);
        var slot = 0;
        foreach (var zone in zones)
        {
            cards.Add(BuildCard(unit, structure, zone, slot++, settings));
        }
        return cards;
    }

    private static LightingDevice BuildCard(IbpAttachedPeripheral unit, DeviceStructure structure, ResolvedZone zone, int slot, NexusSettings settings)
    {
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var layouts = settings.Lighting.DeviceLayouts;

        var isOn = true;
        for (var i = 0; i < disabled.Count; i++)
        {
            if (disabled[i] == zone.Id) { isOn = false; break; }
        }
        prefs.TryGetValue(zone.Id, out var pref);
        var (defX, defY, defW, defH) = DefaultLayout(unit.Model.Kind, slot);
        layouts.TryGetValue(zone.Id, out var layout);

        return new LightingDevice
        {
            Id = zone.Id,
            DeviceKey = zone.DeviceKey,
            Name = zone.Name,
            Type = "ledstrip",
            IconType = IconTypeFor(unit.Model.Kind),
            LedsOn = isOn,
            Brightness = pref?.Brightness ?? 100,
            Hue = pref?.Hue ?? 0f,
            Saturation = pref?.Saturation ?? 1f,
            LedCount = zone.LedCount,
            // Contributor cards render through ResolveSeeded, which always
            // selects artifact zone 0; the count must match it.
            EnabledLedCount = ZoneResolution.CountEnabled(structure, zone, zone.Id, zone.LedCount, zoneHint: 0, settings),
            CanvasX = layout?.X ?? defX,
            CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW,
            CanvasH = layout?.H ?? defH,
            CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            ParentDeviceId = unit.DeviceId,
            ZoneIndex = zone.Ordinal,
            ZoneType = "linear",
            ZoneResizable = false,
            DeviceId = unit.DeviceId,
            ZoneCustomizable = true,
        };
    }

    internal static string IconTypeFor(IbpPeripheralKind kind) =>
        kind == IbpPeripheralKind.Keyboard ? "keyboard" : "mouse";

    internal static string ArchetypeFor(IbpPeripheralKind kind) =>
        kind == IbpPeripheralKind.Keyboard ? "keyboard" : "mouse";

    // ── IDeviceStructureSource ──

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        var attached = _hub.Attached;
        if (attached.Count == 0) return Array.Empty<DeviceStructure>();
        var structures = new DeviceStructure[attached.Count];
        for (var i = 0; i < attached.Count; i++) structures[i] = BuildStructure(attached[i]);
        return structures;
    }

    /// <summary>
    /// Authored structure for one unit: a single fixed segment in wire order
    /// with the model's stock per-LED positions, and one default zone
    /// spanning it. Counts are fixed per model, so a user partition over it
    /// is index-stable for as long as the same model is attached.
    /// </summary>
    internal static DeviceStructure BuildStructure(IbpAttachedPeripheral unit)
    {
        var model = unit.Model;
        var uv = ModelUv.GetOrAdd(model, static m => m.ComputeUv());
        var deviceKey = DeviceKeyComputer.ForFirstParty(IbpPeripheralProtocol.VendorId, model.ProductId);
        var structure = new DeviceStructure
        {
            DeviceId = unit.DeviceId,
            Name = model.Name,
            DeviceKey = deviceKey,
        };
        structure.Segments.Add(new StructureSegment
        {
            Index = LedsSegment,
            Name = "LEDs",
            LedCount = model.LedCount,
            FrameLedCount = model.LedCount,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = uv.U,
            DefaultV = uv.V,
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = unit.DeviceId + LedsSuffix,
            Name = model.Name,
            RawName = structure.Segments[LedsSegment].Name,
            DeviceKey = deviceKey,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = LedsSegment, Start = 0, Count = model.LedCount } },
        });
        return structure;
    }

    // ── Persisted setters (same shared store OpenRGB / NP50 / Keeb use) ──

    public void SetDisabled(IReadOnlyList<string> ids) => _store.Update(s =>
        s.Devices.DisabledLightingDevices = new List<string>(ids));

    public void SetPower(string id, bool on) => _store.Update(s =>
    {
        var current = s.Devices.DisabledLightingDevices;
        if (on)
        {
            if (!current.Contains(id)) return;
            var next = new List<string>(current.Count);
            foreach (var x in current) if (x != id) next.Add(x);
            s.Devices.DisabledLightingDevices = next;
        }
        else
        {
            if (current.Contains(id)) return;
            var next = new List<string>(current.Count + 1);
            next.AddRange(current);
            next.Add(id);
            s.Devices.DisabledLightingDevices = next;
        }
    });

    public void SetBrightness(string id, int brightness) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
        pref.Brightness = Math.Clamp(brightness, 0, 100);
    });

    public void SetHue(string id, float hue) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
        pref.Hue = hue;
    });

    public void SetSaturation(string id, float saturation) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        { pref = new LightingDevicePreference(); s.Devices.LightingDevicePrefs[id] = pref; }
        pref.Saturation = saturation;
    });

    // LED counts are fixed per model; ignore resize requests rather than
    // letting the canvas under-address the wire table.
    public void SetZoneLedCount(string id, int count) { /* fixed layout */ }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // ── ILightingFrameContributor ──

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        var attached = _hub.Attached;
        if (attached.Count == 0)
        {
            _frameCache.Clear();
            return Array.Empty<DeviceFrame>();
        }
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var idx = startingIndex;
        var frames = new List<DeviceFrame>();
        foreach (var unit in attached)
        {
            var structure = BuildStructure(unit);
            var zones = ZoneResolution.Resolve(structure, settings);
            foreach (var zone in zones)
            {
                frames.Add(BuildOrReuseFrame(zone.Id, zone.FrameLedCount, unit.Model.Kind, zone.Ordinal, layouts, ref idx));
            }
        }
        if (_frameCache.Count > frames.Count)
        {
            var live = new HashSet<string>(frames.Count);
            foreach (var f in frames) live.Add(f.Id);
            var stale = new List<string>();
            foreach (var k in _frameCache.Keys) if (!live.Contains(k)) stale.Add(k);
            foreach (var k in stale) _frameCache.Remove(k);
        }
        return frames;
    }

    // Reuse the DeviceFrame instance across the bridge's refresh so the writer
    // never sees a fresh zero-filled frame for one tick and blanks the unit.
    private DeviceFrame BuildOrReuseFrame(
        string id, int ledCount, IbpPeripheralKind kind, int slot,
        IReadOnlyDictionary<string, DeviceLayout> layouts, ref int idx)
    {
        var (defX, defY, defW, defH) = DefaultLayout(kind, slot);
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);
        var thisIdx = idx++;
        var archetype = ArchetypeFor(kind);
        if (_frameCache.TryGetValue(id, out var existing)
            && existing.Index == thisIdx && existing.LedCount == ledCount)
        {
            existing.X = layout?.X ?? defX;
            existing.Y = layout?.Y ?? defY;
            existing.W = layout?.W ?? defW;
            existing.H = layout?.H ?? defH;
            existing.Rotation = rot;
            existing.Archetype = archetype;
            return existing;
        }
        var frame = new DeviceFrame(thisIdx, id, ledCount,
            layout?.X ?? defX, layout?.Y ?? defY, layout?.W ?? defW, layout?.H ?? defH, rot);
        frame.Archetype = archetype;
        _frameCache[id] = frame;
        return frame;
    }

    /// <summary>
    /// Default canvas slots (canvas is 0..1000 x 0..600): keyboards take the
    /// wide band the Keeb uses, mice a narrow box to its right. Slots cycle by
    /// zone ordinal so a custom partition alternates rather than stacking.
    /// The composite provider re-spreads unlaid cards across its grid anyway;
    /// this only decides the first look.
    /// </summary>
    internal static (float x, float y, float w, float h) DefaultLayout(IbpPeripheralKind kind, int slot)
    {
        if (kind == IbpPeripheralKind.Mouse)
            return slot % 2 == 0 ? (900f, 150f, 80f, 140f) : (900f, 300f, 80f, 50f);
        return slot % 2 == 0 ? (120f, 150f, 760f, 140f) : (120f, 300f, 760f, 50f);
    }
}
