using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Exposes the MiniHub's 2 LED-capable ports (port 3 and port 4) as
/// drivable <see cref="LightingDevice"/>s grouped under a "HYTE MiniHub"
/// header, mirroring <see cref="Np50LightingDeviceProvider"/>. Same
/// engine→writer pipeline: BuildFrames contributes the zones to the
/// engine; <see cref="MiniHubLightingFrameWriter"/> pushes the rendered
/// LED bytes to the hub each tick.
/// </summary>
public sealed class MiniHubLightingDeviceProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource
{
    private readonly MiniHubHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private string _lastSignature = "";

    public MiniHubLightingDeviceProvider(MiniHubHub hub, IConfigStore store, Np50IdentifyTracker identify)
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
        return $"{_hub.DeviceId}|p3:{_hub.State.Port3.LedCount}|p4:{_hub.State.Port4.LedCount}";
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
        var counts = settings.Devices.ZoneLedCounts;
        var slot = 0;

        // Ports 1 and 2 are the Nexus-Link fan RGB rings - a known device
        // type with no user-declared chain, so they stay one fixed zone
        // each. The MiniHub firmware doesn't enumerate their LED counts
        // either - the official HYTE tool keeps them in a user-edited
        // config (MiniHubLayoutConfig) - so ZoneResizable stays true and
        // ZoneLedCounts still wins over the sensible defaults below.
        AddZone($"{hubId}:port1", $"{MiniHubHub.ProductName} - Port 1 (1× RGB Fan)", _hub.State.Port1.LedCount);
        AddZone($"{hubId}:port2", $"{MiniHubHub.ProductName} - Port 2 (3× RGB Fans)", _hub.State.Port2.LedCount);

        // Ports 3 and 4 are raw ARGB headers - the firmware cannot say what
        // is daisy-chained to them, so the user declares the chain and each
        // product becomes its own card, in chain order. An unchained port
        // resolves to exactly one zone whose id is the port id, the legacy
        // emission unchanged.
        AddLedPortZones(3, _hub.State.Port3.LedCount);
        AddLedPortZones(4, _hub.State.Port4.LedCount);
        return resp;

        void AddZone(string id, string name, int firmwareLedCount)
        {
            resp.Devices.Add(BuildZone(
                id: id, name: name, firmwareLedCount: firmwareLedCount,
                zoneIndex: slot++, parentDeviceId: hubId,
                disabled, prefs, layouts, counts));
        }

        void AddLedPortZones(int channel, int firmwareLedCount)
        {
            var structure = BuildLedPortStructure(settings, hubId, channel, firmwareLedCount);
            foreach (var zone in ZoneResolution.Resolve(structure, settings))
            {
                resp.Devices.Add(BuildZoneCard(structure, zone, slot++, hubId, disabled, prefs, layouts, settings));
            }
        }
    }

    private static LightingDevice BuildZone(
        string id, string name, int firmwareLedCount, int zoneIndex, string parentDeviceId,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        IReadOnlyDictionary<string, DeviceLayout> layouts,
        IReadOnlyDictionary<string, int> counts)
    {
        var isOn = true;
        for (var i = 0; i < disabled.Count; i++) if (disabled[i] == id) { isOn = false; break; }
        var brightness = 100;
        var hue = 0f;
        var saturation = 1f;
        if (prefs.TryGetValue(id, out var pref))
        {
            brightness = pref.Brightness; hue = pref.Hue; saturation = pref.Saturation;
        }
        // LED count override (user can shorten / expand within firmware-reported max)
        var effectiveLedCount = firmwareLedCount;
        if (counts.TryGetValue(id, out var persisted))
            effectiveLedCount = Math.Max(0, persisted);
        var (defX, defY, defW, defH) = DefaultMiniHubLayout(zoneIndex);
        layouts.TryGetValue(id, out var layout);
        return new LightingDevice
        {
            Id = id, Name = name, Type = "ledstrip", IconType = "strip",
            LedsOn = isOn, Brightness = brightness, Hue = hue, Saturation = saturation,
            LedCount = effectiveLedCount,
            CanvasX = layout?.X ?? defX, CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW, CanvasH = layout?.H ?? defH,
            CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            ParentDeviceId = parentDeviceId, ZoneIndex = zoneIndex,
            ZoneType = "linear", ZoneResizable = true,
        };
    }

    /// <summary>Card for one resolved zone of a chainable ARGB port (port 3 or 4).</summary>
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
        var (defX, defY, defW, defH) = DefaultMiniHubLayout(slot);
        layouts.TryGetValue(id, out var layout);
        return new LightingDevice
        {
            Id = id,
            // The port's own name when it is whole; the product's name once a
            // chain owns it.
            Name = zone.Name,
            Type = "ledstrip", IconType = "strip",
            LedsOn = isOn, Brightness = brightness, Hue = hue, Saturation = saturation,
            LedCount = zone.LedCount,
            EnabledLedCount = ZoneResolution.CountEnabled(structure, zone, id, zone.LedCount, zoneHint: 0, settings),
            CanvasX = layout?.X ?? defX, CanvasY = layout?.Y ?? defY,
            CanvasW = layout?.W ?? defW, CanvasH = layout?.H ?? defH,
            CanvasRotation = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            ParentDeviceId = parentDeviceId, ZoneIndex = zone.Ordinal,
            ZoneType = "linear",
            // Only a zone that owns the whole port may resize it; a chain
            // link is sized by its product, and resizing one would silently
            // restate the port's total and break the tiling.
            ZoneResizable = ZoneResolution.WholeResizableSegment(structure, zone, settings) >= 0,
            // The chain and zone editors address the PORT, which is the
            // device the user actually wired something to.
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

    // ── IDeviceStructureSource ──

    /// <summary>One structure per port card so the LED map editor resolves the port's LED space; counts follow the same ZoneLedCounts override GetAll and BuildFrames honour.</summary>
    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        if (!_hub.IsConnected || string.IsNullOrEmpty(_hub.DeviceId))
        {
            return Array.Empty<DeviceStructure>();
        }
        var hubId = _hub.DeviceId;
        var settings = _store.Load();
        var counts = settings.Devices.ZoneLedCounts;
        return new[]
        {
            BuildStructure($"{hubId}:port1", $"{MiniHubHub.ProductName} - Port 1 (1× RGB Fan)", "Port 1", _hub.State.Port1.LedCount, counts),
            BuildStructure($"{hubId}:port2", $"{MiniHubHub.ProductName} - Port 2 (3× RGB Fans)", "Port 2", _hub.State.Port2.LedCount, counts),
            BuildLedPortStructure(settings, hubId, 3, _hub.State.Port3.LedCount),
            BuildLedPortStructure(settings, hubId, 4, _hub.State.Port4.LedCount),
        };
    }

    /// <summary>
    /// One chainable ARGB port's structure (port 3 or 4). Shared with the
    /// frame writer so the card list, the engine frames, and the bytes on
    /// the wire all resolve the same chain - a port split three ways in one
    /// of them and not the others would light the wrong LEDs rather than
    /// fail visibly. Partitionable defaults true: unlike ports 1/2 this is a
    /// user-declared port, not a fixed fan-ring device.
    /// </summary>
    internal static DeviceStructure BuildLedPortStructure(NexusSettings settings, string hubId, int channel, int firmwareLedCount)
    {
        var portId = $"{hubId}:port{channel}";
        var effectiveLedCount = settings.Devices.ZoneLedCounts.TryGetValue(portId, out var persisted)
            ? Math.Max(0, persisted)
            : firmwareLedCount;
        var name = $"{MiniHubHub.ProductName} - Port {channel} (LED Strip)";
        var rawName = $"Port {channel}";
        var structure = new DeviceStructure { DeviceId = portId, Name = name };
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = rawName,
            LedCount = effectiveLedCount,
            FrameLedCount = effectiveLedCount,
            Resizable = true,
            // Without the ceiling a chain past it saves and renders while
            // BuildLightingStream drops the tail, so the extra LEDs are simply
            // dark. Port 4 is the big output.
            MaxLedCount = channel == 4
                ? Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubProtocol.Port4MaxLedCount
                : Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubProtocol.OtherPortMaxLedCount,
            ZoneType = "linear",
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = portId,
            Name = name,
            RawName = rawName,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = effectiveLedCount } },
        });
        return structure;
    }

    /// <summary>The zones one chainable port currently resolves to, in chain order.</summary>
    internal static IReadOnlyList<ResolvedZone> ResolveLedPortZones(NexusSettings settings, string hubId, int channel, int firmwareLedCount)
        => ZoneResolution.Resolve(BuildLedPortStructure(settings, hubId, channel, firmwareLedCount), settings);

    private static DeviceStructure BuildStructure(
        string id, string name, string rawName, int firmwareLedCount,
        IReadOnlyDictionary<string, int> counts)
    {
        var ledCount = counts.TryGetValue(id, out var persisted) ? Math.Max(0, persisted) : firmwareLedCount;
        var structure = new DeviceStructure { DeviceId = id, Name = name, Partitionable = false };
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = rawName,
            LedCount = ledCount,
            FrameLedCount = ledCount,
            Resizable = true,
            ZoneType = "linear",
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = id,
            Name = name,
            RawName = rawName,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = ledCount } },
        });
        return structure;
    }

    // Same rationale as Np50LightingDeviceProvider._frameCache: reuse the
    // DeviceFrame instance across RgbBridge's 3 s refresh so the writer
    // doesn't see a fresh zero-filled frame for one tick and blank the hub.
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected) return Array.Empty<DeviceFrame>();
        var frames = new List<DeviceFrame>();
        var hubId = _hub.DeviceId;
        var idx = startingIndex;
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var counts = settings.Devices.ZoneLedCounts;
        var slot = 0;

        // One DeviceFrame per channel. Channels 1+2 cover the RGB-fan rings
        // on the Nexus-Link fan ports; 3+4 are the standalone LED outputs.
        // Always emitted regardless of declared LED count so the writer
        // always pushes a blank frame to every channel - keeps the hub
        // from falling back to firmware animation on un-addressed channels.
        frames.Add(BuildOrReuseFrame($"{hubId}:port1", _hub.State.Port1.LedCount, slot++, layouts, counts, ref idx));
        frames.Add(BuildOrReuseFrame($"{hubId}:port2", _hub.State.Port2.LedCount, slot++, layouts, counts, ref idx));

        // Ports 3/4 are chainable: one frame per resolved zone, matching
        // GetAll - a chained port has one per product and each needs its
        // own frame to light. The port id override in ZoneLedCounts is
        // already folded into zone.LedCount, so it is safe to pass through
        // as the frame's "firmware" count.
        foreach (var zone in ZoneResolution.Resolve(BuildLedPortStructure(settings, hubId, 3, _hub.State.Port3.LedCount), settings))
            frames.Add(BuildOrReuseFrame(zone.Id, zone.LedCount, slot++, layouts, counts, ref idx));
        foreach (var zone in ZoneResolution.Resolve(BuildLedPortStructure(settings, hubId, 4, _hub.State.Port4.LedCount), settings))
            frames.Add(BuildOrReuseFrame(zone.Id, zone.LedCount, slot++, layouts, counts, ref idx));

        // Prune cache entries no longer in the live set (e.g. hub serial
        // changed). For MiniHub the live set is fixed at 4 ports so this
        // mostly only fires across reconnects to a different physical hub.
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
        string id, int firmwareLedCount, int zoneIndex,
        IReadOnlyDictionary<string, DeviceLayout> layouts,
        IReadOnlyDictionary<string, int> counts,
        ref int idx)
    {
        var effectiveLedCount = firmwareLedCount;
        if (counts.TryGetValue(id, out var persisted))
            effectiveLedCount = Math.Max(0, persisted);
        var (defX, defY, defW, defH) = DefaultMiniHubLayout(zoneIndex);
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

    /// <summary>Default canvas slots for MiniHub zones: a 4-col, 2-row grid
    /// so up to eight ports (two hubs) fit before wrapping. The second row
    /// lands within the canvas drag clamp (y + h ≤ CH - PAD = 588), so the
    /// card doesn't snap upward on the first interaction.</summary>
    internal static (float x, float y, float w, float h) DefaultMiniHubLayout(int slot)
    {
        const float Y = 370f;
        const float W = 120f;
        const float H = 105f;
        const float Gap = 140f;
        const float BaseX = 40f;
        const int Cols = 4;
        const int Rows = 2;
        const float RowGap = 105f; // row 1 lands at y=475, last edge 580 ≤ clamp
        var s = ((slot % (Cols * Rows)) + Cols * Rows) % (Cols * Rows);
        var col = s % Cols;
        var row = s / Cols;
        return (BaseX + col * Gap, Y + row * RowGap, W, H);
    }
}
