using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Exposes the directly-driven HYTE Keeb TKL on the lighting page - by
/// default as two zones (the key matrix and the underglow), or as whatever
/// partition the user drew over those two fixed segments - and contributes
/// the per-zone <see cref="DeviceFrame"/>s to the engine so canvas effects,
/// brightness, and identify apply uniformly. Mirrors
/// <see cref="Np50LightingDeviceProvider"/>; the keeb owns its vendor HID
/// interface, so OpenRGB doesn't drive it.
/// </summary>
public sealed class KeebLightingDeviceProvider : ILightingDeviceProvider, ILightingFrameContributor, Nexus.Service.Lighting.Zones.IDeviceStructureSource
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Nexus.Service.Peripherals.Hyte.Keeb.KeebKeyMap, string?[]> KeyNamesCache = new();

    /// <summary>Zone id suffixes (also the engine frame ids).</summary>
    public const string KeysSuffix = ":keys";
    public const string UnderglowSuffix = ":underglow";

    private readonly KeebHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public KeebLightingDeviceProvider(KeebHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    public event Action? DevicesChanged;

    /// <summary>Called by the connection worker when the keeb appears/disappears.</summary>
    public void OnConnectionChanged()
    {
        try { DevicesChanged?.Invoke(); } catch { /* subscriber failures shouldn't bubble */ }
    }

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected) return resp;
        resp.Devices.AddRange(BuildCards(_hub.DeviceId, _hub.KeyMap, _store.Load()));
        return resp;
    }

    /// <summary>Pure card emission for the current partition; static so tests cover it without a live hub.</summary>
    internal static List<LightingDevice> BuildCards(string hubId, KeebKeyMap keys, NexusSettings settings)
    {
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var layouts = settings.Lighting.DeviceLayouts;

        var structure = KeebZoneSupport.BuildStructure(hubId, keys);
        var zones = Nexus.Service.Lighting.Zones.ZoneResolution.Resolve(structure, settings);
        var cards = new List<LightingDevice>(zones.Count);

        if (zones.Count > 0 && zones[0].IsDefault)
        {
            cards.Add(BuildZone(
                id: hubId + KeysSuffix, name: $"{KeebHub.ProductName} - Keys",
                deviceKey: Nexus.Service.Lighting.Mappings.DeviceKeyComputer.ForFirstParty(
                    Peripherals.Hyte.Keeb.KeebProtocol.VendorId, Peripherals.Hyte.Keeb.KeebProtocol.ProductId, "keys"),
                iconType: "keyboard", firmwareLedCount: keys.LedCount,
                zoneIndex: 0, parentDeviceId: hubId, structure, zones[0], settings));
            cards.Add(BuildZone(
                id: hubId + UnderglowSuffix, name: $"{KeebHub.ProductName} - Underglow",
                deviceKey: Nexus.Service.Lighting.Mappings.DeviceKeyComputer.ForFirstParty(
                    Peripherals.Hyte.Keeb.KeebProtocol.VendorId, Peripherals.Hyte.Keeb.KeebProtocol.ProductId, "underglow"),
                iconType: "strip", firmwareLedCount: KeebLayout.SurroundLedCount,
                zoneIndex: 1, parentDeviceId: hubId, structure, zones[1], settings));
            return cards;
        }

        foreach (var zone in zones)
        {
            var touchesKeys = false;
            foreach (var slice in zone.Slices)
            {
                if (slice.Segment == KeebZoneSupport.KeysSegment)
                { touchesKeys = true; break; }
            }
            var isOn = true;
            for (var i = 0; i < disabled.Count; i++)
            { if (disabled[i] == zone.Id) { isOn = false; break; } }
            prefs.TryGetValue(zone.Id, out var pref);
            var (defX, defY, defW, defH) = DefaultKeebLayout(zone.Ordinal);
            layouts.TryGetValue(zone.Id, out var layout);
            cards.Add(new LightingDevice
            {
                Id = zone.Id,
                Name = zone.Name,
                Type = "ledstrip",
                IconType = touchesKeys ? "keyboard" : "strip",
                LedsOn = isOn,
                Brightness = pref?.Brightness ?? 100,
                Hue = pref?.Hue ?? 0f,
                Saturation = pref?.Saturation ?? 1f,
                LedCount = zone.LedCount,
                // Contributor cards render through ResolveSeeded, which
                // always selects artifact zone 0; the count must match it.
                EnabledLedCount = Nexus.Service.Lighting.Zones.ZoneResolution.CountEnabled(
                    structure, zone, zone.Id, zone.LedCount, zoneHint: 0, settings),
                CanvasX = layout?.X ?? defX,
                CanvasY = layout?.Y ?? defY,
                CanvasW = layout?.W ?? defW,
                CanvasH = layout?.H ?? defH,
                CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
                ParentDeviceId = hubId,
                ZoneIndex = zone.Ordinal,
                ZoneType = "linear",
                ZoneResizable = false,
                DeviceId = hubId,
                ZoneCustomizable = true,
            });
        }
        return cards;
    }

    // ── IDeviceStructureSource ──

    public IReadOnlyList<Nexus.Service.Lighting.Zones.DeviceStructure> GetStructures()
    {
        if (!_hub.IsConnected || string.IsNullOrEmpty(_hub.DeviceId))
            return Array.Empty<Nexus.Service.Lighting.Zones.DeviceStructure>();
        return new[] { KeebZoneSupport.BuildStructure(_hub.DeviceId, _hub.KeyMap) };
    }

    private static LightingDevice BuildZone(
        string id, string name, string deviceKey, string iconType, int firmwareLedCount,
        int zoneIndex, string parentDeviceId,
        Nexus.Service.Lighting.Zones.DeviceStructure structure,
        Nexus.Service.Lighting.Zones.ResolvedZone zone,
        NexusSettings settings)
    {
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var layouts = settings.Lighting.DeviceLayouts;
        var zoneLedCounts = settings.Devices.ZoneLedCounts;
        var isOn = true;
        for (var i = 0; i < disabled.Count; i++)
        { if (disabled[i] == id) { isOn = false; break; } }
        var brightness = 100;
        var hue = 0f;
        var saturation = 1f;
        if (prefs.TryGetValue(id, out var pref))
        {
            brightness = pref.Brightness; hue = pref.Hue; saturation = pref.Saturation;
        }
        var effectiveLedCount = firmwareLedCount;
        if (zoneLedCounts.TryGetValue(id, out var persisted))
            effectiveLedCount = Math.Clamp(persisted, 0, firmwareLedCount);

        var (defX, defY, defW, defH) = DefaultKeebLayout(zoneIndex);
        layouts.TryGetValue(id, out var layout);
        return new LightingDevice
        {
            Id = id,
            DeviceKey = deviceKey,
            Name = name,
            Type = "ledstrip",
            IconType = iconType,
            LedsOn = isOn,
            Brightness = brightness,
            Hue = hue,
            Saturation = saturation,
            LedCount = effectiveLedCount,
            // Contributor cards render through ResolveSeeded, which always
            // selects artifact zone 0; the count must match it.
            EnabledLedCount = Nexus.Service.Lighting.Zones.ZoneResolution.CountEnabled(
                structure, zone, id, effectiveLedCount, zoneHint: 0, settings),
            CanvasX = layout?.X ?? defX,
            CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW,
            CanvasH = layout?.H ?? defH,
            CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            ParentDeviceId = parentDeviceId,
            ZoneIndex = zoneIndex,
            ZoneType = "linear",
            ZoneResizable = false,
            DeviceId = parentDeviceId,
            ZoneCustomizable = true,
        };
    }

    // ── Persisted setters (same shared store OpenRGB / NP50 use) ──

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

    // Keeb zone LED counts are firmware-fixed; ignore shrink requests rather
    // than letting the canvas under-address the matrix.
    public void SetZoneLedCount(string id, int count) { /* fixed layout */ }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // ── ILightingFrameContributor ──

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected) return Array.Empty<DeviceFrame>();
        var hubId = _hub.DeviceId;
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var structure = KeebZoneSupport.BuildStructure(hubId, _hub.KeyMap);
        var zones = Nexus.Service.Lighting.Zones.ZoneResolution.Resolve(structure, settings);
        var idx = startingIndex;
        var frames = new List<DeviceFrame>(zones.Count);
        foreach (var zone in zones)
        {
            var frame = BuildOrReuseFrame(zone.Id, zone.FrameLedCount, zone.Ordinal, TouchesKeys(zone), layouts, ref idx);
            frame.LedKeys = ZoneKeyNames(zone, _hub.KeyMap, frame.LedKeys);
            frame.DeviceId = hubId;
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

    internal static bool TouchesKeys(Nexus.Service.Lighting.Zones.ResolvedZone zone)
    {
        foreach (var slice in zone.Slices)
        {
            if (slice.Segment == KeebZoneSupport.KeysSegment) return true;
        }
        return false;
    }

    /// <summary>Key name per LED of <paramref name="zone"/>, null on underglow LEDs; reuses <paramref name="current"/> when unchanged so key reactions keep their state across refreshes.</summary>
    internal static string?[]? ZoneKeyNames(Nexus.Service.Lighting.Zones.ResolvedZone zone, Nexus.Service.Peripherals.Hyte.Keeb.KeebKeyMap keys, string?[]? current)
    {
        if (!TouchesKeys(zone)) return null;
        var all = KeyNamesCache.GetOrAdd(keys, Nexus.Service.Peripherals.Hyte.Keeb.KeebKeyNames.For);
        var names = new string?[zone.FrameLedCount];
        var pos = 0;
        foreach (var slice in zone.Slices)
        {
            for (var i = 0; i < slice.Count && pos + i < names.Length; i++)
            {
                var led = slice.Start + i;
                if (slice.Segment == KeebZoneSupport.KeysSegment && led >= 0 && led < all.Length) names[pos + i] = all[led];
            }
            pos += slice.Count;
        }
        return current is not null && current.AsSpan().SequenceEqual(names) ? current : names;
    }

    private DeviceFrame BuildOrReuseFrame(
        string id, int ledCount, int slot, bool touchesKeys,
        IReadOnlyDictionary<string, Persistence.DeviceLayout> layouts, ref int idx)
    {
        var (defX, defY, defW, defH) = DefaultKeebLayout(slot);
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);
        var thisIdx = idx++;
        if (_frameCache.TryGetValue(id, out var existing)
            && existing.Index == thisIdx && existing.LedCount == ledCount)
        {
            existing.X = layout?.X ?? defX;
            existing.Y = layout?.Y ?? defY;
            existing.W = layout?.W ?? defW;
            existing.H = layout?.H ?? defH;
            existing.Rotation = rot;
            existing.Archetype = touchesKeys ? "keyboard" : null;
            return existing;
        }
        var frame = new DeviceFrame(thisIdx, id, ledCount,
            layout?.X ?? defX, layout?.Y ?? defY, layout?.W ?? defW, layout?.H ?? defH, rot);
        frame.Archetype = touchesKeys ? "keyboard" : null;
        _frameCache[id] = frame;
        return frame;
    }

    /// <summary>
    /// Default canvas slots: a wide keys band and a thinner underglow band
    /// beneath it (canvas is 0..1000 x 0..600). Slots cycle by zone ordinal
    /// (same wrap rule as the OpenRGB grids) so a custom partition with many
    /// zones alternates between the two bands instead of stacking everything
    /// past the first zone on one slot. User can drag afterward.
    /// </summary>
    internal static (float x, float y, float w, float h) DefaultKeebLayout(int slot) =>
        slot % 2 == 0 ? (120f, 150f, 760f, 140f) : (120f, 300f, 760f, 50f);
}
