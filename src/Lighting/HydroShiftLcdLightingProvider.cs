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

/// <summary>The HydroShift LCD 360R's three fans as one fan ring zone, fed per LED. Other HydroShift LCD products list nothing here.</summary>
public sealed class HydroShiftLcdLightingProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource
{
    public static readonly string DeviceId = JpegPanelModel.HydroShiftLcd.HandlerId;

    private const int FanCount = 3;

    private const int LedsPerFan = LianLiAioProtocol.HydroShiftFanLedCount / FanCount;

    public static readonly string FansZoneId = DeviceId + ":fans";

    private static readonly string DeviceKey =
        DeviceKeyComputer.ForFirstParty(JpegPanelModel.HydroShiftLcd.VendorId, HydroShiftFanLeds.ProductId360R, "aio");

    private readonly HydroShiftFanLeds _leds;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private DeviceFrame? _frame;

    public HydroShiftLcdLightingProvider(HydroShiftFanLeds leds, IConfigStore store, Np50IdentifyTracker identify)
    {
        _leds = leds;
        _store = store;
        _identify = identify;
        _leds.ConnectionChanged += () => DevicesChanged?.Invoke();
    }

    public bool IsConnected => _leds.IsConnected;

    public event Action? DevicesChanged;

    public static bool IsHydroShiftLcdId(string id) =>
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
        var (defX, defY, defW, defH) = LianLiLightingDeviceProvider.DefaultLayout(0);
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
            CanvasRotation = (((layout?.Rotation ?? 0) % 360) + 360) % 360,
            ParentDeviceId = DeviceId,
            ZoneIndex = zone.Ordinal,
            ZoneType = "linear",
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
        settings.Lighting.DeviceLayouts.TryGetValue(FansZoneId, out var layout);
        var (defX, defY, defW, defH) = LianLiLightingDeviceProvider.DefaultLayout(0);
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
            _frame = frame = new DeviceFrame(startingIndex, FansZoneId, LianLiAioProtocol.HydroShiftFanLedCount, x, y, w, h, rotation);
        }
        return new[] { frame };
    }

    internal static DeviceStructure BuildStructure()
    {
        var (u, v) = LianLiZoneSupport.BuildFanRingUV(FanCount, LedsPerFan, LianLiZoneSupport.SingleRingRadius, indexRunsDown: false);
        var structure = new DeviceStructure
        {
            DeviceId = DeviceId,
            Name = "HydroShift LCD 360R",
            DeviceKey = DeviceKey,
            Partitionable = false,
        };
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = "Fans",
            LedCount = LianLiAioProtocol.HydroShiftFanLedCount,
            FrameLedCount = LianLiAioProtocol.HydroShiftFanLedCount,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = u,
            DefaultV = v,
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = FansZoneId,
            Name = "HydroShift LCD 360R Fans",
            RawName = "Fans",
            DeviceKey = DeviceKey,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = LianLiAioProtocol.HydroShiftFanLedCount } },
        });
        return structure;
    }
}
