using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>The HydroShift II OLED Curved edge LEDs, one fixed zone streamed frame by frame.</summary>
public sealed class HydroShift2CurveLightingDeviceProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource
{
    public const string EdgeZoneId = HydroShift2CurveLcdDriver.Id + ":edge";

    private static readonly string DeviceKey =
        DeviceKeyComputer.ForFirstParty(0x1CBE, HydroShift2CurveProtocol.GlassProductId, "aio");

    private readonly HydroShift2CurveBoard _board;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public HydroShift2CurveLightingDeviceProvider(HydroShift2CurveBoard board, IConfigStore store, Np50IdentifyTracker identify)
    {
        _board = board;
        _store = store;
        _identify = identify;
        _board.AvailabilityChanged += () => DevicesChanged?.Invoke();
    }

    public bool IsConnected => _board.IsAvailable;

    public event Action? DevicesChanged;

    public static bool IsHydroShift2CurveId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(HydroShift2CurveLcdDriver.Id + ":", StringComparison.Ordinal);

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
            ParentDeviceId = HydroShift2CurveLcdDriver.Id,
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
        var frames = new List<DeviceFrame>(1);
        var index = startingIndex;
        foreach (var zone in ZoneResolution.Resolve(BuildStructure(), settings))
        {
            settings.Lighting.DeviceLayouts.TryGetValue(zone.Id, out var layout);
            var (defX, defY, defW, defH) = DefaultLayout;
            var x = layout?.X ?? defX;
            var y = layout?.Y ?? defY;
            var w = layout?.W ?? defW;
            var h = layout?.H ?? defH;
            var rotation = (((layout?.Rotation ?? 0) % 360) + 360) % 360;
            var thisIndex = index++;
            if (_frameCache.TryGetValue(zone.Id, out var frame)
                && frame.Index == thisIndex
                && frame.LedCount == zone.FrameLedCount)
            {
                frame.X = x;
                frame.Y = y;
                frame.W = w;
                frame.H = h;
                frame.Rotation = rotation;
            }
            else
            {
                frame = new DeviceFrame(thisIndex, zone.Id, zone.FrameLedCount, x, y, w, h, rotation);
                _frameCache[zone.Id] = frame;
            }
            frames.Add(frame);
        }
        return frames;
    }

    internal static DeviceStructure BuildStructure()
    {
        const int leds = HydroShift2CurveProtocol.LedCount;
        var u = new float[leds];
        var v = new float[leds];
        for (int i = 0; i < leds; i++)
        {
            (u[i], v[i]) = EdgePosition(i);
        }
        var structure = new DeviceStructure
        {
            DeviceId = HydroShift2CurveLcdDriver.Id,
            Name = "HydroShift II OLED Curved",
            DeviceKey = DeviceKey,
            Partitionable = false,
        };
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = "Screen Edge",
            LedCount = leds,
            FrameLedCount = leds,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = u,
            DefaultV = v,
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = EdgeZoneId,
            Name = "HydroShift II OLED Curved Edge",
            RawName = "Screen Edge",
            DeviceKey = DeviceKey,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = leds } },
        });
        return structure;
    }

    /// <summary>
    /// Where LED <paramref name="index"/> sits round the glass seen from the front, clockwise:
    /// 32-34 and 0-2 down the curved right edge, 3-14 right to left along the bottom, 15-19 up
    /// the left edge, 20-31 left to right along the top.
    /// </summary>
    internal static (float U, float V) EdgePosition(int index)
    {
        const float Margin = 0.04f;
        static float Spread(int i, int count) => Margin + ((i + 0.5f) / count * (1f - (2f * Margin)));
        // Counted from the top of the right edge, three LEDs before index 0.
        var k = (index + 3) % HydroShift2CurveProtocol.LedCount;
        return k switch
        {
            < 6 => (1f, Spread(k, 6)),
            < 18 => (1f - Spread(k - 6, 12), 1f),
            < 23 => (0f, 1f - Spread(k - 18, 5)),
            _ => (Spread(k - 23, 12), 0f),
        };
    }

    /// <summary>The glass's 2288x1080 shape on the effect canvas.</summary>
    private static (float X, float Y, float W, float H) DefaultLayout => (641f, 365f, 318f, 150f);
}
