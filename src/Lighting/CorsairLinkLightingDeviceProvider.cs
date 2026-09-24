using System;
using System.Collections.Generic;
using System.Text;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Exposes each RGB-capable device on the iCUE LINK daisy chain as one
/// <see cref="LightingDevice"/> grouped under a "Corsair iCUE LINK" header.
/// LED counts are model-fixed (non-resizable), so each device is a single
/// non-resizable zone.
///
/// Per-zone Set* operations persist to the shared settings store;
/// <see cref="CorsairLinkLightingFrameWriter"/> applies them each tick.
/// </summary>
public sealed class CorsairLinkLightingDeviceProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource, IOpenRgbDeviceOwner
{
    private readonly CorsairLinkHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;

    // Topology signature used to debounce DevicesChanged: fires only when
    // the set of (channel, ledCount) pairs changes, not on every telemetry tick.
    private string _lastSignature = "";

    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public CorsairLinkLightingDeviceProvider(CorsairLinkHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    /// <summary>
    /// OpenRGB enumerates this hub as "Corsair iCUE Link System Hub". Claiming
    /// it here prevents RgbBridge from seeding OpenRGB frames for our
    /// natively-streamed LEDs, which would overwrite per-device disable/brightness.
    /// </summary>
    public bool OwnsOpenRgbDevice(RgbDevice device) =>
        IsConnected && device.Name.Contains("iCUE Link", StringComparison.OrdinalIgnoreCase);

    public event Action? DevicesChanged;

    /// <summary>
    /// Called by <see cref="CorsairLinkConnectionWorker"/> each poll tick and on
    /// connect/disconnect. Fires <see cref="DevicesChanged"/> only when the
    /// connected device topology (channel set and LED counts) changes.
    /// </summary>
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
        var sb = new StringBuilder("connected");
        foreach (var dev in _hub.State.Devices)
        {
            if (dev.LedCount <= 0) continue;
            sb.Append('|').Append(dev.Channel).Append(':').Append(dev.LedCount);
        }
        return sb.ToString();
    }

    // ── ILightingDeviceProvider ──

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected) return resp;
        var hubId = _hub.DeviceId;
        var settings = _store.Load();
        var slot = 0;
        foreach (var dev in _hub.State.Devices)
        {
            if (dev.LedCount <= 0) continue;
            var id = $"corsair:ch{dev.Channel}";
            var structure = BuildStructure(id, dev);
            foreach (var zone in ZoneResolution.Resolve(structure, settings))
            {
                resp.Devices.Add(BuildCard(hubId, structure, zone, slot++, settings));
            }
        }
        return resp;
    }

    private static LightingDevice BuildCard(
        string hubId, DeviceStructure structure, ResolvedZone zone, int slot, NexusSettings settings)
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
        var (defX, defY, defW, defH) = DefaultLayout(slot);
        layouts.TryGetValue(zone.Id, out var layout);
        return new LightingDevice
        {
            Id = zone.Id,
            DeviceKey = zone.DeviceKey,
            Name = zone.Name,
            Type = "ledstrip",
            IconType = "fan",
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
            ParentDeviceId = hubId,
            ZoneIndex = zone.Ordinal,
            ZoneType = "linear",
            ZoneResizable = false,
            DeviceId = structure.DeviceId,
            ZoneCustomizable = true,
        };
    }

    public void SetDisabled(IReadOnlyList<string> ids) => _store.Update(s =>
    {
        s.Devices.DisabledLightingDevices = new List<string>(ids);
    });

    public void SetPower(string id, bool on) => _store.Update(s =>
    {
        var current = s.Devices.DisabledLightingDevices;
        if (on)
        {
            if (!current.Contains(id)) return;
            var next = new List<string>(current.Count);
            foreach (var x in current)
            {
                if (x != id) next.Add(x);
            }
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
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        pref.Brightness = Math.Clamp(brightness, 0, 100);
    });

    public void SetHue(string id, float hue) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        pref.Hue = hue;
    });

    public void SetSaturation(string id, float saturation) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        pref.Saturation = saturation;
    });

    // LED counts are model-fixed and not user-resizable.
    public void SetZoneLedCount(string id, int count) { }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // ── IDeviceStructureSource ──

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        if (!_hub.IsConnected) return Array.Empty<DeviceStructure>();
        var structures = new List<DeviceStructure>();
        foreach (var dev in _hub.State.Devices)
        {
            if (dev.LedCount <= 0) continue;
            structures.Add(BuildStructure($"corsair:ch{dev.Channel}", dev));
        }
        return structures;
    }

    // ── ILightingFrameContributor ──

    // Frames built by previous BuildFrames calls, keyed by zone id. Reused
    // when the topology is unchanged so the existing DeviceFrame (and its
    // last-rendered LED buffer) survives the RgbBridge rebuild cycle without
    // a black-out tick.
    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected) return Array.Empty<DeviceFrame>();
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var frames = new List<DeviceFrame>();
        var idx = startingIndex;
        var slot = 0;
        foreach (var dev in _hub.State.Devices)
        {
            if (dev.LedCount <= 0) continue;
            var id = $"corsair:ch{dev.Channel}";
            var structure = BuildStructure(id, dev);
            foreach (var zone in ZoneResolution.Resolve(structure, settings))
            {
                frames.Add(BuildOrReuseFrame(zone.Id, zone.FrameLedCount, slot++, layouts, ref idx));
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
        string id, int ledCount, int slot,
        IReadOnlyDictionary<string, DeviceLayout> layouts,
        ref int idx)
    {
        var (defX, defY, defW, defH) = DefaultLayout(slot);
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);
        var thisIdx = idx++;

        if (_frameCache.TryGetValue(id, out var existing)
            && existing.Index == thisIdx
            && existing.LedCount == ledCount)
        {
            existing.X = layout?.X ?? defX;
            existing.Y = layout?.Y ?? defY;
            existing.W = layout?.W ?? defW;
            existing.H = layout?.H ?? defH;
            existing.Rotation = rot;
            return existing;
        }

        var frame = new DeviceFrame(
            index: thisIdx,
            id: id,
            ledCount: ledCount,
            x: layout?.X ?? defX,
            y: layout?.Y ?? defY,
            w: layout?.W ?? defW,
            h: layout?.H ?? defH,
            rotation: rot);
        _frameCache[id] = frame;
        return frame;
    }

    // ── Helpers ──

    /// <summary>
    /// One-segment structure for a single LINK device. Called by both
    /// <see cref="GetStructures"/> and <see cref="CorsairLinkLightingFrameWriter"/>
    /// (which rebuilds structures each tick to resolve the current zone partition).
    /// </summary>
    internal static DeviceStructure BuildStructure(string id, CorsairLinkDevice dev)
    {
        var name = $"{dev.Name} (Fan {dev.Channel})";
        var key = DeviceKeyComputer.ForFirstParty(
            CorsairLinkProtocol.VendorId, CorsairLinkProtocol.ProductId, $"ch{dev.Channel}");
        var structure = new DeviceStructure { DeviceId = id, Name = name, DeviceKey = key };
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = "All",
            LedCount = dev.LedCount,
            FrameLedCount = dev.LedCount,
            Resizable = false,
            ZoneType = "linear",
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = id,
            Name = name,
            RawName = "All",
            DeviceKey = key,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = dev.LedCount } },
        });
        return structure;
    }

    internal static (float x, float y, float w, float h) DefaultLayout(int slot)
    {
        const float Y = 370f;
        const float W = 120f;
        const float H = 105f;
        const float Gap = 140f;
        const float BaseX = 40f;
        const int Cols = 4;
        const int Rows = 2; // row 1 lands at y=475, last edge 580 ≤ canvas bottom
        const float RowGap = 105f;
        var s = ((slot % (Cols * Rows)) + Cols * Rows) % (Cols * Rows);
        var col = s % Cols;
        var row = s / Cols;
        return (BaseX + col * Gap, Y + row * RowGap, W, H);
    }
}
