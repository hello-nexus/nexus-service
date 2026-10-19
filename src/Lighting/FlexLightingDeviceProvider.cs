using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Exposes each Flex fan as a lighting device with one zone per vendor scope: TL Up / Down, SL-INF
/// Center / Inner / Outer. Identity is the receiver MAC. The engine drives a unit only while its
/// mode is the Lighting page one; the frame writer decides, as for the wireless chains.
/// </summary>
public sealed class FlexLightingDeviceProvider : ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource
{
    public const string GroupId = "lianli-flex";
    private const string IdPrefix = "lianli-flex:";

    private readonly FlexHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private string _lastSignature = "";
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public FlexLightingDeviceProvider(FlexHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
        _hub.UnitsChanged += OnHubStateUpdated;
    }

    public bool IsConnected => _hub.IsConnected;

    public event Action? DevicesChanged;

    /// <summary>Fires <see cref="DevicesChanged"/> only when the set of units or a unit's geometry changes.</summary>
    public void OnHubStateUpdated()
    {
        var sig = BuildSignature();
        if (sig == _lastSignature)
        {
            return;
        }
        _lastSignature = sig;
        try
        {
            DevicesChanged?.Invoke();
        }
        catch
        {
            // a subscriber failure must not break the worker
        }
    }

    private string BuildSignature()
    {
        var sb = new System.Text.StringBuilder("units");
        foreach (var u in _hub.Units)
        {
            sb.Append('|').Append(u.Mac).Append(':').Append(u.Fans).Append(':').Append(u.Family).Append(u.Controllable ? "" : ":bound");
        }
        return sb.ToString();
    }

    public static string DeviceIdFor(string mac) => $"{IdPrefix}{mac}";

    public static string MacFromDeviceId(string deviceId) =>
        deviceId.StartsWith(IdPrefix, StringComparison.Ordinal) ? deviceId[IdPrefix.Length..] : "";

    public static string ZoneIdFor(string mac, string zone) => $"{IdPrefix}{mac}:{zone}";

    /// <summary>Zone ids of a family in zone order.</summary>
    public static IReadOnlyList<string> ZonesOf(FlexFamily family) =>
        family == FlexFamily.SlInfFlex ? SlInfZones : TlZones;

    private static readonly string[] TlZones = { "up", "down" };
    private static readonly string[] SlInfZones = { "center", "inner", "outer" };

    public static string ModelName(FlexFamily family) => family == FlexFamily.SlInfFlex ? "Lian Li SL-INF LCD Flex" : "Lian Li TL LCD Flex";

    private static string ZoneLabel(string zone) => zone switch
    {
        "up" => "Up",
        "down" => "Down",
        "center" => "Center",
        "inner" => "Inner",
        "outer" => "Outer",
        _ => zone,
    };

    private static int ZoneLedsPerFan(FlexFamily family, string zone) => FlexProtocol.ZoneWireIndexes(family, zone).Count;

    // ── ILightingDeviceProvider ──

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected)
        {
            return resp;
        }
        var settings = _store.Load();
        var slot = 0;
        foreach (var structure in BuildStructures())
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
        var prefs = settings.Devices.LightingDevicePrefs;
        var layouts = settings.Lighting.DeviceLayouts;
        var isOn = !disabled.Contains(zone.Id);
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
            ParentDeviceId = GroupId,
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
            if (!current.Contains(id))
            {
                return;
            }
            s.Devices.DisabledLightingDevices = current.FindAll(x => x != id);
        }
        else
        {
            if (current.Contains(id))
            {
                return;
            }
            var next = new List<string>(current) { id };
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

    public void SetZoneLedCount(string id, int count)
    {
    }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // ── IDeviceStructureSource ──

    public IReadOnlyList<DeviceStructure> GetStructures() => _hub.IsConnected ? BuildStructures() : Array.Empty<DeviceStructure>();

    // ── ILightingFrameContributor ──

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected)
        {
            return Array.Empty<DeviceFrame>();
        }
        var settings = _store.Load();
        var layouts = settings.Lighting.DeviceLayouts;
        var frames = new List<DeviceFrame>();
        var idx = startingIndex;
        var slot = 0;
        foreach (var structure in BuildStructures())
        {
            foreach (var zone in ZoneResolution.Resolve(structure, settings))
            {
                frames.Add(BuildOrReuseFrame(zone.Id, zone.FrameLedCount, slot++, layouts, ref idx));
            }
        }
        if (_frameCache.Count > frames.Count)
        {
            var live = new HashSet<string>(frames.Count);
            foreach (var f in frames)
            {
                live.Add(f.Id);
            }
            foreach (var k in new List<string>(_frameCache.Keys))
            {
                if (!live.Contains(k))
                {
                    _frameCache.Remove(k);
                }
            }
        }
        return frames;
    }

    private DeviceFrame BuildOrReuseFrame(
        string id, int ledCount, int slot, IReadOnlyDictionary<string, DeviceLayout> layouts, ref int idx)
    {
        var (defX, defY, defW, defH) = DefaultLayout(slot);
        layouts.TryGetValue(id, out var layout);
        var rot = ((((layout?.Rotation ?? 0) % 360) + 360) % 360);
        var thisIdx = idx++;
        if (_frameCache.TryGetValue(id, out var existing) && existing.Index == thisIdx && existing.LedCount == ledCount)
        {
            existing.X = layout?.X ?? defX;
            existing.Y = layout?.Y ?? defY;
            existing.W = layout?.W ?? defW;
            existing.H = layout?.H ?? defH;
            existing.Rotation = rot;
            return existing;
        }
        var frame = new DeviceFrame(
            index: thisIdx, id: id, ledCount: ledCount,
            x: layout?.X ?? defX, y: layout?.Y ?? defY, w: layout?.W ?? defW, h: layout?.H ?? defH, rotation: rot);
        _frameCache[id] = frame;
        return frame;
    }

    /// <summary>Structures of the units the engine can drive: wired control available (not RF-bound elsewhere).</summary>
    internal List<DeviceStructure> BuildStructures()
    {
        var structures = new List<DeviceStructure>();
        foreach (var unit in _hub.Units)
        {
            if (unit.Controllable)
            {
                structures.Add(BuildStructure(unit.Mac, unit.Family, unit.Fans));
            }
        }
        return structures;
    }

    internal static DeviceStructure BuildStructure(string mac, FlexFamily family, int fans)
    {
        var deviceId = DeviceIdFor(mac);
        var pid = family == FlexFamily.SlInfFlex ? FlexProtocol.SlInfReceiverProductId : FlexProtocol.TlReceiverProductId;
        var structure = new DeviceStructure
        {
            DeviceId = deviceId,
            Name = ModelName(family),
            DeviceKey = DeviceKeyComputer.ForFirstParty(FlexProtocol.ReceiverVendorId, pid, "flex-fan"),
        };
        var zones = ZonesOf(family);
        for (var i = 0; i < zones.Count; i++)
        {
            var zone = zones[i];
            var perFan = ZoneLedsPerFan(family, zone);
            var total = perFan * fans;
            var (u, v) = BuildUV(family, zone, fans);
            structure.Segments.Add(new StructureSegment
            {
                Index = i,
                Name = ZoneLabel(zone),
                LedCount = total,
                FrameLedCount = total,
                Resizable = false,
                ZoneType = "linear",
                DefaultU = u,
                DefaultV = v,
            });
            structure.DefaultZones.Add(new DefaultZoneDef
            {
                Id = ZoneIdFor(mac, zone),
                Name = $"{ModelName(family)} - {ZoneLabel(zone)}",
                RawName = ZoneLabel(zone),
                DeviceKey = DeviceKeyComputer.ForFirstParty(FlexProtocol.ReceiverVendorId, pid, $"flex-fan-{zone}"),
                LegacyZoneIndex = -1,
                Slices = { new ZoneSlice { Segment = i, Start = 0, Count = total } },
            });
        }
        return structure;
    }

    // Approximate stock positions: fans side by side along u, LEDs around each fan's ring. The
    // direction of LED 0 is not known from the hardware, so a user layout edit is expected.
    private static (float[] U, float[] V) BuildUV(FlexFamily family, string zone, int fans)
    {
        var perFan = ZoneLedsPerFan(family, zone);
        var radius = (family, zone) switch
        {
            (FlexFamily.TlFlex, _) => 0.42f,
            (_, "center") => 0.14f,
            (_, "inner") => 0.30f,
            _ => 0.44f,
        };
        // TL halves cover opposite arcs; SL-INF zones are full rings.
        var arcStart = family == FlexFamily.TlFlex && zone == "down" ? Math.PI : 0.0;
        var arcSpan = family == FlexFamily.TlFlex ? Math.PI : 2.0 * Math.PI;
        var u = new float[perFan * fans];
        var v = new float[perFan * fans];
        for (var f = 0; f < fans; f++)
        {
            var cu = (f + 0.5f) / fans;
            for (var i = 0; i < perFan; i++)
            {
                var angle = arcStart + (((i + 0.5) / perFan) * arcSpan);
                u[(f * perFan) + i] = cu + ((radius / fans) * (float)Math.Cos(angle));
                v[(f * perFan) + i] = 0.5f + (radius * (float)Math.Sin(angle));
            }
        }
        return (u, v);
    }

    internal static (float x, float y, float w, float h) DefaultLayout(int slot)
    {
        const float BaseX = 40f;
        const float Y = 370f;
        const float W = 120f;
        const float H = 105f;
        const float Gap = 140f;
        const int Cols = 4;
        const int Rows = 2;
        const float RowGap = 105f;
        var s = ((slot % (Cols * Rows)) + (Cols * Rows)) % (Cols * Rows);
        return (BaseX + ((s % Cols) * Gap), Y + ((s / Cols) * RowGap), W, H);
    }
}
