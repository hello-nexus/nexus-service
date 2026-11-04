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
    public const int DialsSegment = 0;

    private static readonly Lazy<(float[] U, float[] V)> Uv = new(GalleonSdProtocol.ComputeUv);

    private readonly GalleonSdHub _hub;
    private readonly GalleonSdDialsHub? _dials;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public GalleonSdLightingDeviceProvider(GalleonSdHub hub, IConfigStore store, Np50IdentifyTracker identify, GalleonSdDialsHub? dials = null)
    {
        _hub = hub;
        _dials = dials;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected || (_dials?.IsConnected ?? false);

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
        var dials = _dials?.Attached;
        if (unit is null && dials is null) return resp;
        var settings = _store.Load();
        if (unit is not null) resp.Devices.AddRange(BuildCards(unit, settings));
        if (dials is not null) resp.Devices.AddRange(BuildDialsCards(dials, settings));
        return resp;
    }

    internal static List<LightingDevice> BuildCards(GalleonSdAttached unit, NexusSettings settings)
    {
        var structure = BuildStructure(unit);
        var zones = ZoneResolution.Resolve(structure, settings);
        var cards = new List<LightingDevice>(zones.Count);
        var slot = 0;
        foreach (var zone in zones) cards.Add(BuildCard(unit.DeviceId, structure, zone, slot++, settings, dials: false));
        return cards;
    }

    internal static List<LightingDevice> BuildDialsCards(GalleonSdDialsAttached unit, NexusSettings settings)
    {
        var structure = BuildDialsStructure(unit);
        var zones = ZoneResolution.Resolve(structure, settings);
        var cards = new List<LightingDevice>(zones.Count);
        var slot = 0;
        foreach (var zone in zones) cards.Add(BuildCard(unit.DeviceId, structure, zone, slot++, settings, dials: true));
        return cards;
    }

    private static LightingDevice BuildCard(string deviceId, DeviceStructure structure, ResolvedZone zone, int slot, NexusSettings settings, bool dials)
    {
        var disabled = settings.Devices.DisabledLightingDevices;
        var isOn = !disabled.Contains(zone.Id);
        settings.Devices.LightingDevicePrefs.TryGetValue(zone.Id, out var pref);
        var (defX, defY, defW, defH) = DefaultLayout(slot, dials);
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
            ParentDeviceId = deviceId,
            ZoneIndex = zone.Ordinal,
            ZoneType = "linear",
            ZoneResizable = false,
            DeviceId = deviceId,
            ControlHandlerId = GalleonSdProtocol.HandlerId,
            ZoneCustomizable = true,
        };
    }

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        var unit = _hub.Attached;
        var dials = _dials?.Attached;
        if (unit is null && dials is null) return Array.Empty<DeviceStructure>();
        var list = new List<DeviceStructure>(2);
        if (unit is not null) list.Add(BuildStructure(unit));
        if (dials is not null) list.Add(BuildDialsStructure(dials));
        return list;
    }

    /// <summary>One linear zone over the lamps in id order; positions from the lamp attributes drive the stock layout when they have any spread.</summary>
    internal static DeviceStructure BuildDialsStructure(GalleonSdDialsAttached unit)
    {
        var deviceKey = DeviceKeyComputer.ForFirstParty(GalleonSdProtocol.VendorId, GalleonSdProtocol.StreamDeckProductId);
        var count = unit.LampCount;
        var uv = Nexus.Service.Peripherals.Hid.LampArray.LampArrayProtocol.ComputeUv(unit.Lamps);
        var structure = new DeviceStructure
        {
            DeviceId = unit.DeviceId,
            Name = GalleonSdProtocol.DialsProductName,
            DeviceKey = deviceKey,
        };
        structure.Segments.Add(new StructureSegment
        {
            Index = DialsSegment,
            Name = "Dials",
            LedCount = count,
            FrameLedCount = count,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = uv?.U,
            DefaultV = uv?.V,
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = unit.DeviceId + LedsSuffix,
            Name = GalleonSdProtocol.DialsProductName,
            RawName = structure.Segments[DialsSegment].Name,
            DeviceKey = deviceKey,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = DialsSegment, Start = 0, Count = count } },
        });
        return structure;
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
        var dials = _dials?.Attached;
        if (unit is null && dials is null)
        {
            _frameCache.Clear();
            return Array.Empty<DeviceFrame>();
        }
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var idx = startingIndex;
        var frames = new List<DeviceFrame>();
        if (unit is not null) AddFrames(BuildStructure(unit), dialsStructure: false, settings, layouts, frames, ref idx);
        if (dials is not null) AddFrames(BuildDialsStructure(dials), dialsStructure: true, settings, layouts, frames, ref idx);
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

    private void AddFrames(DeviceStructure structure, bool dialsStructure, NexusSettings settings, IReadOnlyDictionary<string, DeviceLayout> layouts, List<DeviceFrame> frames, ref int idx)
    {
        foreach (var zone in ZoneResolution.Resolve(structure, settings))
        {
            var frame = BuildOrReuseFrame(zone.Id, zone.FrameLedCount, zone.Ordinal, dialsStructure, layouts, ref idx);
            frame.DeviceId = structure.DeviceId;
            frames.Add(frame);
        }
    }

    // The frame instance survives refreshes so the writer never sees a zero-filled frame for a tick.
    private DeviceFrame BuildOrReuseFrame(string id, int ledCount, int slot, bool dials, IReadOnlyDictionary<string, DeviceLayout> layouts, ref int idx)
    {
        var (defX, defY, defW, defH) = DefaultLayout(slot, dials);
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
            existing.Archetype = dials ? null : "keyboard";
            return existing;
        }
        var frame = new DeviceFrame(thisIdx, id, ledCount,
            layout?.X ?? defX, layout?.Y ?? defY, layout?.W ?? defW, layout?.H ?? defH, rot);
        frame.Archetype = dials ? null : "keyboard";
        _frameCache[id] = frame;
        return frame;
    }

    internal static (float x, float y, float w, float h) DefaultLayout(int slot, bool dials = false) =>
        dials ? (120f, 380f, 240f, 60f) : slot % 2 == 0 ? (120f, 150f, 760f, 140f) : (120f, 300f, 760f, 50f);
}
