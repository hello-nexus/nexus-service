using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.Galahad2;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Exposes the twelve logical RGB positions on a Galahad II LCD pump head. The LCD and
/// pump RGB share one HID interface, so this provider uses the panel hub rather than
/// opening a second handle.
/// </summary>
public sealed class Galahad2LcdLightingDeviceProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource, IOpenRgbDeviceOwner
{
    public const string DeviceId = "lianli-galahad2-lcd";
    public const string PumpZoneId = "lianli-galahad2-lcd:pump";
    public const int PumpLedCount = Galahad2Protocol.PumpLedCount;
    internal const int PumpSegment = 0;

    private readonly JpegPanelHub _hub;
    private readonly IConfigStore _store;
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();
    private string _lastSignature = "";

    public Galahad2LcdLightingDeviceProvider(JpegPanelHub hub, IConfigStore store)
    {
        _hub = hub;
        _store = store;
        _hub.StateChanged += OnHubStateUpdated;
    }

    public bool IsConnected => _hub.IsConnected;

    public bool OwnsOpenRgbDevice(RgbDevice device) =>
        IsConnected && device.Name.Contains("GAII", StringComparison.OrdinalIgnoreCase);

    public event Action? DevicesChanged;

    public void OnHubStateUpdated()
    {
        var signature = _hub.IsConnected ? "connected" : "disconnected";
        if (signature == _lastSignature)
        {
            return;
        }
        _lastSignature = signature;
        try { DevicesChanged?.Invoke(); } catch { }
    }

    public GetLightingDevicesResponse GetAll()
    {
        var response = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected)
        {
            return response;
        }

        var settings = _store.Load();
        var structure = BuildStructure();
        foreach (var zone in ZoneResolution.Resolve(structure, settings))
        {
            response.Devices.Add(BuildCard(structure, zone, settings));
        }
        return response;
    }

    private static LightingDevice BuildCard(DeviceStructure structure, ResolvedZone zone, NexusSettings settings)
    {
        var isOn = !settings.Devices.DisabledLightingDevices.Contains(zone.Id);
        settings.Devices.LightingDevicePrefs.TryGetValue(zone.Id, out var preference);
        settings.Lighting.DeviceLayouts.TryGetValue(zone.Id, out var layout);

        return new LightingDevice
        {
            Id = zone.Id,
            DeviceKey = zone.DeviceKey,
            Name = zone.Name,
            Type = "ledstrip",
            IconType = "aio",
            LedsOn = isOn,
            Brightness = preference?.Brightness ?? 100,
            Hue = preference?.Hue ?? 0f,
            Saturation = preference?.Saturation ?? 1f,
            LedCount = zone.LedCount,
            EnabledLedCount = ZoneResolution.CountEnabled(structure, zone, zone.Id, zone.LedCount, zoneHint: 0, settings),
            CanvasX = layout?.X ?? 700f,
            CanvasY = layout?.Y ?? 500f,
            CanvasW = layout?.W ?? 50f,
            CanvasH = layout?.H ?? 50f,
            CanvasRotation = NormalizeRotation(layout?.Rotation ?? 0),
            ParentDeviceId = DeviceId,
            ZoneIndex = zone.Ordinal,
            ZoneType = "linear",
            ZoneResizable = false,
            DeviceId = structure.DeviceId,
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
            foreach (var value in current)
            {
                if (value != id) next.Add(value);
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
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var preference))
        {
            preference = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = preference;
        }
        preference.Brightness = Math.Clamp(brightness, 0, 100);
    });

    public void SetHue(string id, float hue) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var preference))
        {
            preference = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = preference;
        }
        preference.Hue = hue;
    });

    public void SetSaturation(string id, float saturation) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var preference))
        {
            preference = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = preference;
        }
        preference.Saturation = saturation;
    });

    public void SetZoneLedCount(string id, int count) { }

    public void Identify(string id, int durationMs) { }

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        if (!_hub.IsConnected)
        {
            return Array.Empty<DeviceStructure>();
        }
        return new[] { BuildStructure() };
    }

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected)
        {
            return Array.Empty<DeviceFrame>();
        }

        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var structure = BuildStructure();
        var zones = ZoneResolution.Resolve(structure, settings);
        var frames = new List<DeviceFrame>(zones.Count);
        var index = startingIndex;
        foreach (var zone in zones)
        {
            var (x, y, w, h) = layouts.TryGetValue(zone.Id, out var layout)
                ? (layout.X, layout.Y, layout.W, layout.H)
                : (700f, 500f, 50f, 50f);
            var rotation = NormalizeRotation(layout?.Rotation ?? 0);

            if (_frameCache.TryGetValue(zone.Id, out var existing)
                && existing.Index == index
                && existing.LedCount == zone.FrameLedCount)
            {
                existing.X = x;
                existing.Y = y;
                existing.W = w;
                existing.H = h;
                existing.Rotation = rotation;
                frames.Add(existing);
                index++;
                continue;
            }

            var frame = new DeviceFrame(
                index: index,
                id: zone.Id,
                ledCount: zone.FrameLedCount,
                x: x,
                y: y,
                w: w,
                h: h,
                rotation: rotation);
            _frameCache[zone.Id] = frame;
            frames.Add(frame);
            index++;
        }
        return frames;
    }

    internal DeviceStructure BuildStructure()
    {
        var key = DeviceKeyComputer.ForFirstParty(
            Galahad2Protocol.VendorId,
            Galahad2Protocol.ProductIdLcd,
            "aio");
        // The controller exposes the pump as a linear logical strip (the two visible
        // sides mirror those positions), so keep the default map free of unverified
        // physical geometry.
        var (u, v) = BuildLinearUv(Galahad2Protocol.PumpLedCount);
        var structure = new DeviceStructure
        {
            DeviceId = DeviceId,
            Name = "Galahad II AIO",
            DeviceKey = key,
            Partitionable = false,
        };
        structure.Segments.Add(new StructureSegment
        {
            Index = PumpSegment,
            Name = "Pump Head",
            LedCount = Galahad2Protocol.PumpLedCount,
            FrameLedCount = Galahad2Protocol.PumpLedCount,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = u,
            DefaultV = v,
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = PumpZoneId,
            Name = "Galahad II Pump Head",
            RawName = "Pump Head",
            DeviceKey = key,
            LegacyZoneIndex = -1,
            Slices = new List<ZoneSlice>
            {
                new() { Segment = PumpSegment, Start = 0, Count = Galahad2Protocol.PumpLedCount },
            },
        });
        return structure;
    }

    private static (float[] u, float[] v) BuildLinearUv(int ledCount)
    {
        var u = new float[ledCount];
        var v = new float[ledCount];
        for (var i = 0; i < ledCount; i++)
        {
            u[i] = ledCount <= 1 ? 0.5f : i / (float)(ledCount - 1);
            v[i] = 0.5f;
        }
        return (u, v);
    }

    private static int NormalizeRotation(int rotation) => ((rotation % 360) + 360) % 360;
}
