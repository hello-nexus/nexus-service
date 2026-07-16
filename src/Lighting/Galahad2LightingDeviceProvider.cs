using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Galahad2;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

public sealed class Galahad2LightingDeviceProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource, IOpenRgbDeviceOwner
{
    internal const int InnerSegment = 0;
    internal const int OuterSegment = 1;

    private readonly Galahad2Hub _hub;
    private readonly IConfigStore _store;
    private string _lastSignature = "";

    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public Galahad2LightingDeviceProvider(Galahad2Hub hub, IConfigStore store)
    {
        _hub   = hub;
        _store = store;
    }

    public bool IsConnected => _hub.IsConnected;

    // OpenRGB names this device "Lian Li GAII Trinity" (RGBController_LianLiGAIITrinity.cpp).
    public bool OwnsOpenRgbDevice(RgbDevice device) =>
        IsConnected && device.Name.Contains("GAII", StringComparison.OrdinalIgnoreCase);

    public event Action? DevicesChanged;

    public void OnHubStateUpdated()
    {
        var sig = _hub.IsConnected ? "connected" : "disconnected";
        if (sig == _lastSignature)
        {
            return;
        }
        _lastSignature = sig;
        try { DevicesChanged?.Invoke(); } catch { }
    }

    // ── ILightingDeviceProvider ──

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected)
        {
            return resp;
        }
        var settings   = _store.Load();
        var structure  = BuildStructure();
        var slot       = 0;
        foreach (var zone in ZoneResolution.Resolve(structure, settings))
        {
            resp.Devices.Add(BuildCard(structure, zone, slot++, settings));
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
            Id               = zone.Id,
            DeviceKey        = zone.DeviceKey,
            Name             = zone.Name,
            Type             = "ledstrip",
            IconType         = "aio",
            LedsOn           = isOn,
            Brightness       = pref?.Brightness ?? 100,
            Hue              = pref?.Hue ?? 0f,
            Saturation       = pref?.Saturation ?? 1f,
            LedCount         = zone.LedCount,
            EnabledLedCount  = ZoneResolution.CountEnabled(structure, zone, zone.Id, zone.LedCount, zoneHint: 0, settings),
            CanvasX          = layout?.X ?? defX,
            CanvasY          = layout?.Y ?? defY,
            CanvasW          = layout?.W ?? defW,
            CanvasH          = layout?.H ?? defH,
            CanvasRotation   = ((((layout?.Rotation ?? 0) % 360) + 360) % 360),
            ParentDeviceId   = "lianli-aio",
            ZoneIndex        = zone.Ordinal,
            ZoneType         = "point",
            ZoneResizable    = false,
            DeviceId         = structure.DeviceId,
            ZoneCustomizable = false,
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

    public void Identify(string id, int durationMs) { }

    // ── IDeviceStructureSource ──

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        if (!_hub.IsConnected)
        {
            return Array.Empty<DeviceStructure>();
        }
        return new[] { BuildStructure() };
    }

    // ── ILightingFrameContributor ──

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected)
        {
            return Array.Empty<DeviceFrame>();
        }
        var settings = _store.Load();
        var layouts  = settings.Lighting.DeviceLayouts;
        var frames   = new List<DeviceFrame>(2);
        var idx      = startingIndex;
        var structure = BuildStructure();
        var slot      = 0;
        foreach (var zone in ZoneResolution.Resolve(structure, settings))
        {
            frames.Add(BuildOrReuseFrame(zone.Id, zone.FrameLedCount, slot++, layouts, ref idx));
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

    internal DeviceStructure BuildStructure()
    {
        var pid = _hub.ConnectedProductId > 0
            ? _hub.ConnectedProductId
            : Galahad2Protocol.ProductIdPerformance;
        var key = DeviceKeyComputer.ForFirstParty(Galahad2Protocol.VendorId, pid, "aio");

        var structure = new DeviceStructure
        {
            DeviceId  = "lianli-aio",
            Name      = "Galahad II AIO",
            DeviceKey = key,
        };

        structure.Segments.Add(new StructureSegment
        {
            Index         = 0,
            Name          = "Inner Ring",
            LedCount      = 1,
            FrameLedCount = 1,
            Resizable     = false,
            ZoneType      = "point",
        });
        structure.Segments.Add(new StructureSegment
        {
            Index         = 1,
            Name          = "Outer Ring",
            LedCount      = 1,
            FrameLedCount = 1,
            Resizable     = false,
            ZoneType      = "point",
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id              = "lianli-aio:ring:inner",
            Name            = "Galahad II Inner Ring",
            RawName         = "Inner Ring",
            DeviceKey       = key,
            LegacyZoneIndex = -1,
            Slices          = new List<ZoneSlice> { new() { Segment = 0, Start = 0, Count = 1 } },
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id              = "lianli-aio:ring:outer",
            Name            = "Galahad II Outer Ring",
            RawName         = "Outer Ring",
            DeviceKey       = key,
            LegacyZoneIndex = -1,
            Slices          = new List<ZoneSlice> { new() { Segment = 1, Start = 0, Count = 1 } },
        });

        return structure;
    }

    internal static (float x, float y, float w, float h) DefaultLayout(int slot)
    {
        const float BaseX = 700f;
        const float Y     = 500f;
        const float W     = 50f;
        const float H     = 50f;
        const float Gap   = 60f;
        return (BaseX + slot * Gap, Y, W, H);
    }
}
