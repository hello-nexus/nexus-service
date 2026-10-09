using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>The Galahad II LCD's pump-head ring as one colour: its firmware takes a static colour, not per-LED frames.</summary>
public sealed class Galahad2LcdLightingProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource
{
    public static readonly string DeviceId = JpegPanelModel.GalahadIiLcd.HandlerId;

    public static readonly string RingZoneId = DeviceId + ":ring";

    private static readonly string DeviceKey =
        DeviceKeyComputer.ForFirstParty(JpegPanelModel.GalahadIiLcd.VendorId, JpegPanelModel.GalahadIiLcd.ProductIds[0], "aio");

    private readonly Galahad2LcdAio _aio;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private DeviceFrame? _frame;

    public Galahad2LcdLightingProvider(Galahad2LcdAio aio, IConfigStore store, Np50IdentifyTracker identify)
    {
        _aio = aio;
        _store = store;
        _identify = identify;
        _aio.ConnectionChanged += () => DevicesChanged?.Invoke();
    }

    public bool IsConnected => _aio.IsConnected;

    public event Action? DevicesChanged;

    public static bool IsGalahad2LcdId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(DeviceId + ":", StringComparison.Ordinal);

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!IsConnected)
        {
            return resp;
        }
        var settings = _store.Load();
        var structure = BuildStructure();
        foreach (var zone in ZoneResolution.Resolve(structure, settings))
        {
            resp.Devices.Add(BuildCard(structure, zone, settings));
        }
        return resp;
    }

    private static LightingDevice BuildCard(DeviceStructure structure, ResolvedZone zone, NexusSettings settings)
    {
        var isOn = !settings.Devices.DisabledLightingDevices.Contains(zone.Id);
        settings.Devices.LightingDevicePrefs.TryGetValue(zone.Id, out var pref);
        settings.Lighting.DeviceLayouts.TryGetValue(zone.Id, out var layout);
        var (defX, defY, defW, defH) = DefaultLayout;
        return new LightingDevice
        {
            Id = zone.Id,
            DeviceKey = zone.DeviceKey,
            Name = zone.Name,
            Type = "ledstrip",
            IconType = "aio",
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
            CanvasRotation = (((layout?.Rotation ?? 0) % 360) + 360) % 360,
            ParentDeviceId = DeviceId,
            ZoneIndex = zone.Ordinal,
            ZoneType = "point",
            ZoneResizable = false,
            DeviceId = structure.DeviceId,
            ZoneCustomizable = false,
        };
    }

    public void SetDisabled(IReadOnlyList<string> ids) => _store.Update(s =>
        s.Devices.DisabledLightingDevices = new List<string>(ids));

    public void SetPower(string id, bool on) => _store.Update(s =>
    {
        var current = s.Devices.DisabledLightingDevices;
        if (on == !current.Contains(id)) return;
        var next = new List<string>(current);
        if (on) next.Remove(id); else next.Add(id);
        s.Devices.DisabledLightingDevices = next;
    });

    public void SetBrightness(string id, int brightness) =>
        UpdatePref(id, pref => pref.Brightness = Math.Clamp(brightness, 0, 100));

    public void SetHue(string id, float hue) => UpdatePref(id, pref => pref.Hue = hue);

    public void SetSaturation(string id, float saturation) => UpdatePref(id, pref => pref.Saturation = saturation);

    public void SetZoneLedCount(string id, int count) { }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    private void UpdatePref(string id, Action<LightingDevicePreference> apply) => _store.Update(s =>
    {
        if (!s.Devices.LightingDevicePrefs.TryGetValue(id, out var pref))
        {
            pref = new LightingDevicePreference();
            s.Devices.LightingDevicePrefs[id] = pref;
        }
        apply(pref);
    });

    public IReadOnlyList<DeviceStructure> GetStructures() =>
        IsConnected ? new[] { BuildStructure() } : Array.Empty<DeviceStructure>();

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!IsConnected)
        {
            return Array.Empty<DeviceFrame>();
        }
        var settings = _store.Load();
        settings.Lighting.DeviceLayouts.TryGetValue(RingZoneId, out var layout);
        var (defX, defY, defW, defH) = DefaultLayout;
        var x = layout?.X ?? defX;
        var y = layout?.Y ?? defY;
        var w = layout?.W ?? defW;
        var h = layout?.H ?? defH;
        var rotation = (((layout?.Rotation ?? 0) % 360) + 360) % 360;
        if (_frame is { } frame && frame.Index == startingIndex)
        {
            frame.X = x;
            frame.Y = y;
            frame.W = w;
            frame.H = h;
            frame.Rotation = rotation;
        }
        else
        {
            _frame = frame = new DeviceFrame(startingIndex, RingZoneId, 1, x, y, w, h, rotation);
        }
        return new[] { frame };
    }

    internal static DeviceStructure BuildStructure()
    {
        var structure = new DeviceStructure
        {
            DeviceId = DeviceId,
            Name = "Galahad II LCD",
            DeviceKey = DeviceKey,
            Partitionable = false,
        };
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = "Pump Ring",
            LedCount = 1,
            FrameLedCount = 1,
            Resizable = false,
            ZoneType = "point",
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = RingZoneId,
            Name = "Galahad II LCD Pump Ring",
            RawName = "Pump Ring",
            DeviceKey = DeviceKey,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = 1 } },
        });
        return structure;
    }

    private static (float X, float Y, float W, float H) DefaultLayout => (700f, 340f, 50f, 50f);
}
