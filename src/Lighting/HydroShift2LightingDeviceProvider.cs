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

/// <summary>The HydroShift II LCD-S or LCD-C pump-head ring, driven over USB as one fixed zone.</summary>
public sealed class HydroShift2LightingDeviceProvider :
    ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource
{
    public const string RingZoneId = HydroShift2LcdDriver.Id + ":ring";

    private static readonly string SquareDeviceKey =
        DeviceKeyComputer.ForFirstParty(0x1CBE, HydroShift2Protocol.ProductIdSquare, "aio");

    private static readonly string CircleDeviceKey =
        DeviceKeyComputer.ForFirstParty(0x1CBE, HydroShift2Protocol.ProductIdCircle, "aio");

    private readonly HydroShift2Aio _aio;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public HydroShift2LightingDeviceProvider(HydroShift2Aio aio, IConfigStore store, Np50IdentifyTracker identify)
    {
        _aio = aio;
        _store = store;
        _identify = identify;
        _aio.AvailabilityChanged += () => DevicesChanged?.Invoke();
    }

    public bool IsConnected => _aio.IsAvailable;

    public event Action? DevicesChanged;

    public static bool IsHydroShift2Id(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(HydroShift2LcdDriver.Id + ":", StringComparison.Ordinal);

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!IsConnected)
        {
            return resp;
        }
        var settings = _store.Load();
        var structure = BuildStructure(_aio.Round);
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
            ParentDeviceId = HydroShift2LcdDriver.Id,
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
        IsConnected ? new[] { BuildStructure(_aio.Round) } : Array.Empty<DeviceStructure>();

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!IsConnected)
        {
            return Array.Empty<DeviceFrame>();
        }
        var settings = _store.Load();
        var frames = new List<DeviceFrame>(1);
        var index = startingIndex;
        foreach (var zone in ZoneResolution.Resolve(BuildStructure(_aio.Round), settings))
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

    internal static DeviceStructure BuildStructure(bool round = false)
    {
        const int leds = HydroShift2Protocol.RingLedCount;
        var u = new float[leds];
        var v = new float[leds];
        for (int i = 0; i < leds; i++)
        {
            (u[i], v[i]) = round ? CircleRingPosition(i) : SquareRingPosition(i);
        }
        var deviceKey = round ? CircleDeviceKey : SquareDeviceKey;
        var structure = new DeviceStructure
        {
            DeviceId = HydroShift2LcdDriver.Id,
            Name = "HydroShift II",
            DeviceKey = deviceKey,
            Partitionable = false,
        };
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = "Pump Ring",
            LedCount = leds,
            FrameLedCount = leds,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = u,
            DefaultV = v,
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = RingZoneId,
            Name = "HydroShift II Pump Ring",
            RawName = "Pump Ring",
            DeviceKey = deviceKey,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = leds } },
        });
        return structure;
    }

    /// <summary>
    /// Where LED <paramref name="index"/> sits on the square pump head, seen from the front with
    /// the tubes up: evenly spaced clockwise round the bezel, six to a side, LED 0 at the top
    /// centre and LEDs 3, 9, 15 and 21 in the corners (camera-mapped one LED at a time).
    /// </summary>
    internal static (float U, float V) SquareRingPosition(int index)
    {
        const float Margin = 0.08f;
        const int leds = HydroShift2Protocol.RingLedCount;
        // Distance round the perimeter from the top-left corner, in sides.
        var s = ((index + 3) % leds) * 4f / leds;
        var (u, v) = s switch
        {
            < 1f => (s, 0f),
            < 2f => (1f, s - 1f),
            < 3f => (3f - s, 1f),
            _ => (0f, 4f - s),
        };
        return (Margin + (u * (1f - (2f * Margin))), Margin + (v * (1f - (2f * Margin))));
    }

    /// <summary>Where LED <paramref name="index"/> sits on the round head: LED 0 at the top, clockwise (seen on an LCD-C).</summary>
    internal static (float U, float V) CircleRingPosition(int index)
    {
        const float Radius = 0.42f;
        var angle = (index / (double)HydroShift2Protocol.RingLedCount * 2.0 * Math.PI) - (Math.PI / 2.0);
        return (0.5f + (Radius * (float)Math.Cos(angle)), 0.5f + (Radius * (float)Math.Sin(angle)));
    }

    /// <summary>A square on the effect canvas so the ring samples a spread of the effect rather than one patch.</summary>
    private static (float X, float Y, float W, float H) DefaultLayout => (700f, 340f, 200f, 200f);
}
