using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Hyte.SmartHub;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Exposes the Smart Hub's four ARGB ports as drivable
/// <see cref="LightingDevice"/>s grouped under a "HYTE Smart Hub" header,
/// mirroring <see cref="MiniHubLightingDeviceProvider"/>. Same engine→writer
/// pipeline: BuildFrames contributes the zones to the engine;
/// <see cref="SmartHubLightingFrameWriter"/> pushes the rendered LED bytes to
/// the hub each tick.
///
/// The firmware does not enumerate LED counts, so every port starts at 0 LEDs
/// and the user declares the count (zones are resizable up to
/// <see cref="SmartHubProtocol.MaxLedsPerPort"/> via the lighting page).
/// </summary>
public sealed class SmartHubLightingDeviceProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource, IComposableHubSource
{
    private readonly SmartHubHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private string _lastSignature = "";

    public SmartHubLightingDeviceProvider(SmartHubHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    public event Action? DevicesChanged;

    public void OnHubStateUpdated()
    {
        var sig = BuildSignature();
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        try { DevicesChanged?.Invoke(); } catch { /* swallow subscriber failures */ }
    }

    private string BuildSignature()
    {
        if (!_hub.IsConnected) return "disconnected";
        return _hub.DeviceId + (ReadMirror(_store.Load(), _hub.DeviceId) ? "|m" : "");
    }

    /// <summary>The four ARGB ports collapse into one mirror device when set; default off.</summary>
    internal static bool ReadMirror(NexusSettings settings, string hubId)
        => settings.Devices.LightingComposition.TryGetValue(hubId, out var c) && c is not null && c.Mirror;

    internal static string MirrorId(string hubId) => $"{hubId}:mirror";

    /// <summary>The hub a port or mirror structure belongs to (<c>smarthub:&lt;serial&gt;</c>); null for any other device.</summary>
    internal static string? HubIdOf(string structureDeviceId)
    {
        if (!structureDeviceId.StartsWith("smarthub:", StringComparison.Ordinal)) return null;
        var cut = structureDeviceId.LastIndexOf(':');
        if (cut <= "smarthub:".Length) return null;
        var suffix = structureDeviceId.AsSpan(cut + 1);
        return suffix.SequenceEqual("mirror") || (suffix.StartsWith("port") && suffix.Length > 4)
            ? structureDeviceId[..cut]
            : null;
    }

    private static int MirrorLedCount(SmartHubHub hub, IReadOnlyDictionary<string, int> counts)
    {
        if (counts.TryGetValue(MirrorId(hub.DeviceId), out var persisted))
        {
            return Math.Max(0, persisted);
        }
        var max = 0;
        foreach (var port in hub.State.Ports)
        {
            var c = counts.TryGetValue($"{hub.DeviceId}:port{port.Channel}", out var pc)
                ? Math.Max(0, pc)
                : port.LedCount;
            if (c > max) max = c;
        }
        return max;
    }

    // ── IComposableHubSource ──

    public HubCompositionInfo? DescribeComposition(string deviceId)
    {
        var hubId = _hub.DeviceId;
        if (!_hub.IsConnected || string.IsNullOrEmpty(hubId)) return null;
        if (deviceId != hubId && !deviceId.StartsWith(hubId + ":", StringComparison.Ordinal)) return null;

        var active = new bool[SmartHubProtocol.ArgbPortCount];
        for (var i = 0; i < active.Length; i++) active[i] = true;
        return new HubCompositionInfo
        {
            HubId = hubId,
            HubKind = "smarthub",
            PortCount = SmartHubProtocol.ArgbPortCount,
            HasRingsAxis = false,
            HasPortToggle = false,
            HasMirror = true,
            Mirror = ReadMirror(_store.Load(), hubId),
            CombineRings = false,
            ActivePorts = active,
        };
    }

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected) return resp;
        var hubId = _hub.DeviceId;
        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var layouts = settings.Lighting.DeviceLayouts;
        var slot = 0;

        // One card per RESOLVED zone, not per port: the firmware cannot say
        // what is daisy-chained to a port, so the user declares the chain and
        // each product becomes its own card, in chain order. An unchained port
        // resolves to exactly one zone whose id is the port id, which is the
        // legacy emission unchanged.
        foreach (var structure in GetStructures())
        {
            foreach (var zone in ZoneResolution.Resolve(structure, settings))
            {
                resp.Devices.Add(BuildZoneCard(structure, zone, slot++, hubId, disabled, prefs, layouts, settings));
            }
        }
        return resp;
    }

    // ── IDeviceStructureSource ──

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        if (!_hub.IsConnected || string.IsNullOrEmpty(_hub.DeviceId))
        {
            return Array.Empty<DeviceStructure>();
        }
        var hubId = _hub.DeviceId;
        var settings = _store.Load();
        var counts = settings.Devices.ZoneLedCounts;

        if (ReadMirror(settings, hubId))
        {
            return new[] { BuildMirrorStructure(settings, hubId, _hub) };
        }

        var structures = new List<DeviceStructure>(_hub.State.Ports.Length);
        foreach (var port in _hub.State.Ports)
        {
            structures.Add(BuildPortStructure(settings, hubId, port.Channel, port.LedCount));
        }
        return structures;
    }

    /// <summary>
    /// One port's structure. Shared with the frame writer so the card list,
    /// the engine frames, and the bytes on the wire all resolve the same chain
    /// - a port split three ways in one of them and not the others would light
    /// the wrong LEDs rather than fail visibly.
    /// </summary>
    internal static DeviceStructure BuildPortStructure(NexusSettings settings, string hubId, int channel, int firmwareLedCount)
    {
        var portId = $"{hubId}:port{channel}";
        var effectiveLedCount = settings.Devices.ZoneLedCounts.TryGetValue(portId, out var persisted)
            ? Math.Max(0, persisted)
            : firmwareLedCount;
        return BuildStructure(portId, $"{SmartHubHub.ProductName} - Port {channel} (ARGB)",
            $"Port {channel}", $"port{channel}", effectiveLedCount);
    }

    /// <summary>The zones one port currently resolves to, in chain order.</summary>
    internal static IReadOnlyList<ResolvedZone> ResolvePortZones(NexusSettings settings, string hubId, int channel, int firmwareLedCount)
        => ZoneResolution.Resolve(BuildPortStructure(settings, hubId, channel, firmwareLedCount), settings);

    /// <summary>The mirror device's structure: the four ports collapsed into one, sized by the largest.</summary>
    internal static DeviceStructure BuildMirrorStructure(NexusSettings settings, string hubId, SmartHubHub hub)
        => BuildStructure(MirrorId(hubId), $"{SmartHubHub.ProductName} - All Ports (ARGB)",
            "All Ports", "mirror", MirrorLedCount(hub, settings.Devices.ZoneLedCounts));

    /// <summary>The mirror device's zones, which every physical port streams while mirroring is on.</summary>
    internal static IReadOnlyList<ResolvedZone> ResolveMirrorZones(NexusSettings settings, string hubId, SmartHubHub hub)
        => ZoneResolution.Resolve(BuildMirrorStructure(settings, hubId, hub), settings);

    private static DeviceStructure BuildStructure(string id, string name, string rawName, string keySlug, int ledCount)
    {
        var key = DeviceKeyComputer.ForFirstParty(SmartHubProtocol.VendorId, SmartHubProtocol.ProductId, keySlug);
        var structure = new DeviceStructure { DeviceId = id, Name = name, DeviceKey = key };
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = rawName,
            LedCount = ledCount,
            FrameLedCount = ledCount,
            Resizable = true,
            MaxLedCount = SmartHubProtocol.MaxLedsPerPort,
            ZoneType = "linear",
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = id,
            Name = name,
            RawName = rawName,
            DeviceKey = key,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = ledCount } },
        });
        return structure;
    }

    private static LightingDevice BuildZoneCard(
        DeviceStructure structure, ResolvedZone zone, int slot, string parentDeviceId,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        IReadOnlyDictionary<string, DeviceLayout> layouts,
        NexusSettings settings)
    {
        var id = zone.Id;
        var isOn = true;
        for (var i = 0; i < disabled.Count; i++) if (disabled[i] == id) { isOn = false; break; }
        var brightness = 100;
        var hue = 0f;
        var saturation = 1f;
        if (prefs.TryGetValue(id, out var pref))
        {
            brightness = pref.Brightness; hue = pref.Hue; saturation = pref.Saturation;
        }
        var (defX, defY, defW, defH) = DefaultSmartHubLayout(slot);
        layouts.TryGetValue(id, out var layout);
        return new LightingDevice
        {
            Id = id,
            // The port's own name when it is whole; the product's name once a
            // chain owns it. Both already read as "Smart Hub - ...".
            Name = zone.Name,
            Type = "ledstrip", IconType = "strip",
            LedsOn = isOn, Brightness = brightness, Hue = hue, Saturation = saturation,
            LedCount = zone.LedCount,
            // Contributor cards render through ResolveSeeded, whose zone hint
            // is always 0; the count has to be computed the same way.
            EnabledLedCount = ZoneResolution.CountEnabled(structure, zone, id, zone.LedCount, 0, settings),
            CanvasX = layout?.X ?? defX, CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW, CanvasH = layout?.H ?? defH,
            CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            ParentDeviceId = parentDeviceId, ZoneIndex = slot,
            ZoneType = "linear",
            // Only a zone that owns the whole port may resize it; a chain link
            // is sized by its product, and resizing one would silently restate
            // the port's total and break the tiling.
            ZoneResizable = ZoneResolution.WholeResizableSegment(structure, zone, settings) >= 0,
            // The chain and zone editors address the PORT, which is the device
            // the user actually wired something to.
            DeviceId = structure.DeviceId,
            ZoneCustomizable = true,
        };
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
            next.AddRange(current); next.Add(id);
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

    public void SetZoneLedCount(string id, int count)
    {
        if (count < 0) return;
        _store.Update(s =>
        {
            s.Devices.ZoneLedCounts[id] = count;
            ZoneResolution.DropChainForCount(s, id);
        });
    }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // Reuse the DeviceFrame instance across RgbBridge's refresh so the writer
    // doesn't see a fresh zero-filled frame for one tick and blank the hub
    // (same rationale as MiniHub / NP50 providers).
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected) return Array.Empty<DeviceFrame>();
        var frames = new List<DeviceFrame>();
        var idx = startingIndex;
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var slot = 0;

        // One frame per resolved zone, matching GetAll. A chained port has one
        // per product; without frames of their own those cards would render in
        // the list and never light.
        foreach (var structure in GetStructures())
        {
            foreach (var zone in ZoneResolution.Resolve(structure, settings))
            {
                frames.Add(BuildOrReuseFrame(zone.Id, zone.LedCount, slot++, layouts, ref idx));
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

    private DeviceFrame BuildOrReuseFrame(
        string id, int effectiveLedCount, int slot,
        IReadOnlyDictionary<string, DeviceLayout> layouts,
        ref int idx)
    {
        var (defX, defY, defW, defH) = DefaultSmartHubLayout(slot);
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);
        var thisIdx = idx++;

        if (_frameCache.TryGetValue(id, out var existing)
            && existing.Index == thisIdx
            && existing.LedCount == effectiveLedCount)
        {
            existing.X = layout?.X ?? defX;
            existing.Y = layout?.Y ?? defY;
            existing.W = layout?.W ?? defW;
            existing.H = layout?.H ?? defH;
            existing.Rotation = rot;
            return existing;
        }

        var frame = new DeviceFrame(
            index: thisIdx, id: id, ledCount: effectiveLedCount,
            x: layout?.X ?? defX, y: layout?.Y ?? defY,
            w: layout?.W ?? defW, h: layout?.H ?? defH, rotation: rot);
        _frameCache[id] = frame;
        return frame;
    }

    /// <summary>Default canvas slots for the four Smart Hub ARGB ports - a single row of four cards, same shape as the MiniHub default layout.</summary>
    internal static (float x, float y, float w, float h) DefaultSmartHubLayout(int slot)
    {
        const float Y = 370f;
        const float W = 120f;
        const float H = 105f;
        const float Gap = 140f;
        const float BaseX = 40f;
        const int Cols = 4;
        var s = ((slot % Cols) + Cols) % Cols;
        return (BaseX + s * Gap, Y, W, H);
    }
}
