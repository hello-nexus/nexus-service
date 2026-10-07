using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Strimer;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

public sealed class StrimerLightingDeviceProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource, IOpenRgbDeviceOwner
{
    private readonly StrimerHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private string _lastSignature = "";

    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public StrimerLightingDeviceProvider(StrimerHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    // Prevents RgbBridge from seeding OpenRGB frames for these LEDs when the device
    // appears in OpenRGB alongside the native HID path.
    public bool OwnsOpenRgbDevice(RgbDevice device) =>
        IsConnected && device.Name.Contains("Lian Li Strimer", StringComparison.OrdinalIgnoreCase);

    public event Action? DevicesChanged;

    public void OnHubStateUpdated()
    {
        var sig = !_hub.IsConnected ? "disconnected" : _store.Load().Devices.StrimerLighting.ArgbSync ? "argb" : "connected";
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        try { DevicesChanged?.Invoke(); } catch { /* swallow subscriber failures */ }
    }

    // ── ILightingDeviceProvider ──

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected) return resp;
        var settings = _store.Load();
        // Under ARGB sync the cable plays the motherboard header, so it has no cards of its own.
        if (settings.Devices.StrimerLighting.ArgbSync) return resp;
        var slot = 0;
        var structures = BuildStructures();
        foreach (var structure in structures)
        {
            foreach (var zone in ZoneResolution.Resolve(structure, settings))
            {
                resp.Devices.Add(BuildCard(structure, zone, slot++, settings));
            }
        }
        return resp;
    }

    private static LightingDevice BuildCard(DeviceStructure structure, ResolvedZone zone, int slot, NexusSettings settings)
    {
        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs    = settings.Devices.LightingDevicePrefs;
        var layouts  = settings.Lighting.DeviceLayouts;

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
            Id                = zone.Id,
            DeviceKey         = zone.DeviceKey,
            Name              = zone.Name,
            Type              = "ledstrip",
            IconType          = "ledstrip",
            LedsOn            = isOn,
            Brightness        = pref?.Brightness ?? 100,
            Hue               = pref?.Hue ?? 0f,
            Saturation        = pref?.Saturation ?? 1f,
            LedCount          = zone.LedCount,
            EnabledLedCount   = ZoneResolution.CountEnabled(structure, zone, zone.Id, zone.LedCount, zoneHint: 0, settings),
            CanvasX           = layout?.X ?? defX,
            CanvasY           = layout?.Y ?? defY,
            CanvasW           = layout?.W ?? defW,
            CanvasH           = layout?.H ?? defH,
            CanvasRotation    = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            ParentDeviceId    = "strimer",
            ZoneIndex         = zone.Ordinal,
            ZoneType          = "linear",
            ZoneResizable     = false,
            DeviceId          = structure.DeviceId,
            ZoneCustomizable  = true,
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

    public void SetZoneLedCount(string id, int count) { }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // ── IDeviceStructureSource ──

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        if (!_hub.IsConnected || _store.Load().Devices.StrimerLighting.ArgbSync) return Array.Empty<DeviceStructure>();
        return BuildStructures();
    }

    // ── ILightingFrameContributor ──

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected) return Array.Empty<DeviceFrame>();
        var settings = _store.Load();
        if (settings.Devices.StrimerLighting.ArgbSync) return Array.Empty<DeviceFrame>();
        var layouts  = settings.Lighting.DeviceLayouts;
        var frames   = new List<DeviceFrame>();
        var idx      = startingIndex;
        var slot     = 0;
        var structures = BuildStructures();
        foreach (var structure in structures)
        {
            foreach (var zone in ZoneResolution.Resolve(structure, settings))
            {
                frames.Add(BuildOrReuseFrame(zone.Id, zone.FrameLedCount, slot++, layouts, ref idx));
            }
        }
        if (_frameCache.Count > frames.Count)
        {
            var live  = new HashSet<string>(frames.Count);
            foreach (var f in frames) live.Add(f.Id);
            var stale = new List<string>();
            foreach (var k in _frameCache.Keys)
            {
                if (!live.Contains(k)) stale.Add(k);
            }
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
        var rot     = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);
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
            index:    thisIdx,
            id:       id,
            ledCount: ledCount,
            x:        layout?.X ?? defX,
            y:        layout?.Y ?? defY,
            w:        layout?.W ?? defW,
            h:        layout?.H ?? defH,
            rotation: rot);
        _frameCache[id] = frame;
        return frame;
    }

    internal DeviceStructure[] BuildStructures()
    {
        var atxKey = DeviceKeyComputer.ForFirstParty(StrimerProtocol.VendorId, StrimerProtocol.ProductId, "atx");
        var gpuKey = DeviceKeyComputer.ForFirstParty(StrimerProtocol.VendorId, StrimerProtocol.ProductId, "gpu");

        var atx = new DeviceStructure
        {
            DeviceId  = "strimer:atx",
            Name      = "Strimer 24-Pin ATX",
            DeviceKey = atxKey,
        };
        for (var s = 0; s < StrimerProtocol.AtxZoneCount; s++)
        {
            atx.Segments.Add(new StructureSegment
            {
                Index        = s,
                Name         = $"ATX Strip {s + 1}",
                LedCount     = StrimerProtocol.AtxLedsPerZone,
                FrameLedCount= StrimerProtocol.AtxLedsPerZone,
                Resizable    = false,
                ZoneType     = "linear",
            });
            atx.DefaultZones.Add(new DefaultZoneDef
            {
                Id            = $"strimer:atx:z{s}",
                Name          = $"Strimer ATX {s + 1}",
                RawName       = $"ATX Strip {s + 1}",
                DeviceKey     = atxKey,
                LegacyZoneIndex = -1,
                Slices        = new List<ZoneSlice> { new() { Segment = s, Start = 0, Count = StrimerProtocol.AtxLedsPerZone } },
            });
        }

        var gpu = new DeviceStructure
        {
            DeviceId  = "strimer:gpu",
            Name      = "Strimer 8-Pin GPU",
            DeviceKey = gpuKey,
        };
        for (var s = 0; s < StrimerProtocol.NormalizeGpuZoneCount(_store.Load().Devices.StrimerGpuZones); s++)
        {
            gpu.Segments.Add(new StructureSegment
            {
                Index        = s,
                Name         = $"GPU Strip {s + 1}",
                LedCount     = StrimerProtocol.GpuLedsPerZone,
                FrameLedCount= StrimerProtocol.GpuLedsPerZone,
                Resizable    = false,
                ZoneType     = "linear",
            });
            gpu.DefaultZones.Add(new DefaultZoneDef
            {
                Id            = $"strimer:gpu:z{s}",
                Name          = $"Strimer GPU {s + 1}",
                RawName       = $"GPU Strip {s + 1}",
                DeviceKey     = gpuKey,
                LegacyZoneIndex = -1,
                Slices        = new List<ZoneSlice> { new() { Segment = s, Start = 0, Count = StrimerProtocol.GpuLedsPerZone } },
            });
        }

        return new[] { atx, gpu };
    }

    internal static (float x, float y, float w, float h) DefaultLayout(int slot)
    {
        const float BaseX = 900f;
        const float Y     = 540f;
        const float W     = 150f;
        const float H     = 20f;
        const float Gap   = 160f;
        const int   Cols  = 6;
        var col = slot % Cols;
        var row = slot / Cols;
        return (BaseX + col * Gap, Y + row * (H + 10f), W, H);
    }
}
