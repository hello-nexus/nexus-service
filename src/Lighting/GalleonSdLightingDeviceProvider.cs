using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Corsair.Galleon;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>Exposes the Corsair Galleon 100 SD keyboard on the lighting page as one partitionable card with its key positions, and contributes its zone frames to the engine. Mirrors <see cref="IbpPeripheralLightingDeviceProvider"/> for a single unit.</summary>
public sealed class GalleonSdLightingDeviceProvider : ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource
{
    public const string LedsSuffix = ":leds";
    public const int LedsSegment = 0;

    private static readonly Lazy<(float[] U, float[] V)> Uv = new(GalleonSdProtocol.ComputeUv);

    private readonly GalleonSdHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public GalleonSdLightingDeviceProvider(GalleonSdHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    public event Action? DevicesChanged;

    public void OnConnectionChanged()
    {
        try { DevicesChanged?.Invoke(); } catch { /* subscriber failures shouldn't bubble */ }
    }

    public static bool IsGalleonId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(GalleonSdProtocol.DeviceIdPrefix, StringComparison.Ordinal);

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        var unit = _hub.Attached;
        if (unit is null) return resp;
        resp.Devices.AddRange(BuildCards(unit, _store.Load()));
        return resp;
    }

    internal static List<LightingDevice> BuildCards(GalleonSdAttached unit, NexusSettings settings)
    {
        var structure = BuildStructure(unit);
        var zones = ZoneResolution.Resolve(structure, settings);
        var cards = new List<LightingDevice>(zones.Count);
        var slot = 0;
        foreach (var zone in zones) cards.Add(BuildCard(unit, structure, zone, slot++, settings));
        return cards;
    }

    private static LightingDevice BuildCard(GalleonSdAttached unit, DeviceStructure structure, ResolvedZone zone, int slot, NexusSettings settings)
    {
        var disabled = settings.Devices.DisabledLightingDevices;
        var isOn = !disabled.Contains(zone.Id);
        settings.Devices.LightingDevicePrefs.TryGetValue(zone.Id, out var pref);
        var (defX, defY, defW, defH) = DefaultLayout(slot);
        settings.Lighting.DeviceLayouts.TryGetValue(zone.Id, out var layout);

        return new LightingDevice
        {
            Id = zone.Id,
            DeviceKey = zone.DeviceKey,
            Name = zone.Name,
            Type = "ledstrip",
            IconType = "keyboard",
            LedsOn = isOn,
            Brightness = pref?.Brightness ?? 100,
            Hue = pref?.Hue ?? 0f,
            Saturation = pref?.Saturation ?? 1f,
            LedCount = zone.LedCount,
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
            ControlHandlerId = GalleonSdProtocol.HandlerId,
            ZoneCustomizable = true,
        };
    }

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        var unit = _hub.Attached;
        return unit is null ? Array.Empty<DeviceStructure>() : new[] { BuildStructure(unit) };
    }

    internal static DeviceStructure BuildStructure(GalleonSdAttached unit)
    {
        var uv = Uv.Value;
        var deviceKey = DeviceKeyComputer.ForFirstParty(GalleonSdProtocol.VendorId, GalleonSdProtocol.ProductId);
        var count = GalleonSdProtocol.LedCount;
        var structure = new DeviceStructure
        {
            DeviceId = unit.DeviceId,
            Name = GalleonSdProtocol.ProductName,
            DeviceKey = deviceKey,
        };
        structure.Segments.Add(new StructureSegment
        {
            Index = LedsSegment,
            Name = "LEDs",
            LedCount = count,
            FrameLedCount = count,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = uv.U,
            DefaultV = uv.V,
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = unit.DeviceId + LedsSuffix,
            Name = GalleonSdProtocol.ProductName,
            RawName = structure.Segments[LedsSegment].Name,
            DeviceKey = deviceKey,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = LedsSegment, Start = 0, Count = count } },
        });
        return structure;
    }

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

    public void SetZoneLedCount(string id, int count) { /* fixed layout */ }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        var unit = _hub.Attached;
        if (unit is null)
        {
            _frameCache.Clear();
            return Array.Empty<DeviceFrame>();
        }
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var idx = startingIndex;
        var structure = BuildStructure(unit);
        var frames = new List<DeviceFrame>();
        foreach (var zone in ZoneResolution.Resolve(structure, settings))
        {
            var frame = BuildOrReuseFrame(zone.Id, zone.FrameLedCount, zone.Ordinal, layouts, ref idx);
            frame.DeviceId = structure.DeviceId;
            frames.Add(frame);
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

    // The frame instance survives refreshes so the writer never sees a zero-filled frame for a tick.
    private DeviceFrame BuildOrReuseFrame(string id, int ledCount, int slot, IReadOnlyDictionary<string, DeviceLayout> layouts, ref int idx)
    {
        var (defX, defY, defW, defH) = DefaultLayout(slot);
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);
        var thisIdx = idx++;
        if (_frameCache.TryGetValue(id, out var existing) && existing.Index == thisIdx && existing.LedCount == ledCount)
        {
            existing.X = layout?.X ?? defX;
            existing.Y = layout?.Y ?? defY;
            existing.W = layout?.W ?? defW;
            existing.H = layout?.H ?? defH;
            existing.Rotation = rot;
            existing.Archetype = "keyboard";
            return existing;
        }
        var frame = new DeviceFrame(thisIdx, id, ledCount,
            layout?.X ?? defX, layout?.Y ?? defY, layout?.W ?? defW, layout?.H ?? defH, rot);
        frame.Archetype = "keyboard";
        _frameCache[id] = frame;
        return frame;
    }

    internal static (float x, float y, float w, float h) DefaultLayout(int slot) =>
        slot % 2 == 0 ? (120f, 150f, 760f, 140f) : (120f, 300f, 760f, 50f);
}
