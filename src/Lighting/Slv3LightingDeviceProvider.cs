using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting;

/// <summary>
/// Exposes every SLV3 wireless fan chain currently bound to this host as a
/// lighting device with inner/outer ring zones, mirroring
/// <see cref="LianLiLightingDeviceProvider"/> for the wired hub. One device
/// per bound MAC (identity is the MAC, stable across rebinds and rx_type
/// slot reassignment - never the dynamically-assigned slot). Unlike the
/// wired hub there is no fixed port range or mirror/combine composition:
/// each bound chain is already a natural 1:1 device, so this provider does
/// not implement IComposableHubSource.
///
/// The family's wire LED count per fan (Slv3Protocol.LedsPerFanFor) is split
/// as two equal ring zones - "Inner Ring" = the first half of the wire
/// indices, "Outer Ring" = the last half - a documented v1 approximation
/// pending a hardware camera check of the true ring boundary (SLV3's physical
/// sub-rings are 12+8+12+8).
///
/// A bound Strimer Wireless cable is a device of its own on the same link:
/// one fixed segment holding the whole cable (Slv3Protocol.StrimerGeometryFor),
/// pre-wired on first sight with its catalog product the way the Nollie 32
/// pre-wires its Strimer ports, so the card carries the cable's LED map. It
/// reports no fans, so it never goes through the fan path.
/// </summary>
public sealed class Slv3LightingDeviceProvider : ILightingDeviceProvider, ILightingFrameContributor, IDeviceStructureSource
{
    public const int InnerSegment = 0;
    public const int OuterSegment = 1;

    /// <summary>Logical group label for card layout; matches the device-handler id ("lianli-wireless") used elsewhere.</summary>
    public const string GroupId = "lianli-wireless";

    // Concentric ring radii in canvas-normalised units, matching LianLiZoneSupport's convention.
    private const float InnerRadius = 0.24f;
    private const float OuterRadius = 0.42f;

    private readonly Slv3Hub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private string _lastSignature = "";
    private readonly Dictionary<string, DeviceFrame> _frameCache = new();

    public Slv3LightingDeviceProvider(Slv3Hub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _hub = hub;
        _store = store;
        _identify = identify;
    }

    public bool IsConnected => _hub.IsConnected;

    public event Action? DevicesChanged;

    /// <summary>
    /// Called by the connection worker each poll tick. Fires
    /// <see cref="DevicesChanged"/> only when the set of bound fan chains (or
    /// a chain's fan count) changes, so the bridge rebuilds frame mappings
    /// without churning on every RPM/telemetry tick.
    /// </summary>
    public void OnHubStateUpdated()
    {
        var sig = BuildSignature();
        if (sig == _lastSignature) return;
        _lastSignature = sig;
        WireNewStrimers();
        try { DevicesChanged?.Invoke(); } catch { /* swallow subscriber failures */ }
    }

    // First sight of a bound Strimer wires its catalog product to the cable:
    // one zone, the product's LED map applied. ZoneLedCounts is the seeded
    // marker here as on the Nollie, written even when the product cannot be
    // wired so the seed runs once; a cable the user has since reset or
    // re-partitioned is left alone.
    private void WireNewStrimers()
    {
        List<(string DeviceId, string Key, int LedCount)>? cables = null;
        foreach (var fan in _hub.State.Fans)
        {
            if (!fan.BoundToUs || !Slv3Protocol.IsStrimerDevType((byte)fan.DevType)) continue;
            var ledCount = StrimerLedCountFor((byte)fan.DevType);
            if (ledCount <= 0) continue;
            (cables ??= new()).Add((DeviceIdFor(fan.Mac), StrimerProductKeyFor((byte)fan.DevType), ledCount));
        }
        if (cables is null) return;
        var seeded = _store.Load().Devices.ZoneLedCounts;
        var pending = false;
        foreach (var cable in cables)
        {
            if (!seeded.ContainsKey(cable.DeviceId)) { pending = true; break; }
        }
        if (!pending) return;
        _store.Update(s =>
        {
            foreach (var (deviceId, key, ledCount) in cables)
            {
                if (s.Devices.ZoneLedCounts.ContainsKey(deviceId)) continue;
                if (PortChainWriter.WireProduct(s, deviceId, key, ledCount))
                {
                    ServiceLog.Info($"[lianli-wireless] pre-wired {deviceId} as {key}");
                }
                else
                {
                    s.Devices.ZoneLedCounts[deviceId] = ledCount;
                    ServiceLog.Warn($"[lianli-wireless] {deviceId}: catalog product {key} not wired, cable left as one zone");
                }
            }
        });
    }

    private string BuildSignature()
    {
        if (!_hub.IsConnected) return "disconnected";
        var sb = new System.Text.StringBuilder("connected");
        foreach (var fan in _hub.State.Fans)
        {
            if (!fan.BoundToUs) continue;
            // FanType is part of the signature because the family sets the LED
            // count: a chain first seen with a starved beacon (fans_type all 0,
            // Unknown family) rebuilds once the real subtype arrives. DevType
            // tells a Strimer (and its lane geometry) from a fan chain.
            sb.Append('|').Append(fan.Mac).Append(':').Append(fan.FanCount).Append(':').Append(fan.FanType).Append(':').Append(fan.DevType);
        }
        return sb.ToString();
    }

    // ── ILightingDeviceProvider ──

    public GetLightingDevicesResponse GetAll()
    {
        var resp = new GetLightingDevicesResponse { IsInit = true };
        if (!_hub.IsConnected) return resp;
        var settings = _store.Load();
        var slot = 0;
        foreach (var structure in BuildStructures())
        {
            var iconType = IsStrimerStructure(structure) ? "ledstrip" : "fan";
            foreach (var zone in ZoneResolution.Resolve(structure, settings))
            {
                resp.Devices.Add(BuildCard(structure, zone, slot++, settings, iconType));
            }
        }
        return resp;
    }

    private static LightingDevice BuildCard(DeviceStructure structure, ResolvedZone zone, int slot, NexusSettings settings, string iconType)
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
            IconType = iconType,
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

    // Ring LED counts are fan-derived, not separately editable.
    public void SetZoneLedCount(string id, int count) { }

    public void Identify(string id, int durationMs) => _identify.Schedule(id, durationMs);

    // ── IDeviceStructureSource ──

    public IReadOnlyList<DeviceStructure> GetStructures()
    {
        if (!_hub.IsConnected) return Array.Empty<DeviceStructure>();
        return BuildStructures();
    }

    // ── ILightingFrameContributor ──

    public IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex)
    {
        if (!_hub.IsConnected) return Array.Empty<DeviceFrame>();

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
            var stale = new List<string>();
            foreach (var k in _frameCache.Keys)
            {
                if (!live.Contains(k)) stale.Add(k);
            }
            foreach (var k in stale)
            {
                _frameCache.Remove(k);
            }
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

    /// <summary>Stable per-fan device id: the MAC survives rebinds and rx_type slot reassignment, unlike either.</summary>
    public static string DeviceIdFor(string macHex) => $"lianli-wireless:{macHex}";

    /// <summary>Strips the device id prefix back to the fan's MAC hex, or "" if it is not one of ours.</summary>
    public static string MacFromDeviceId(string deviceId)
    {
        const string prefix = "lianli-wireless:";
        return deviceId.StartsWith(prefix, StringComparison.Ordinal) ? deviceId[prefix.Length..] : "";
    }

    /// <summary>Every currently-bound chain as a partitionable structure. Skips a chain with no LEDs to build.</summary>
    internal List<DeviceStructure> BuildStructures()
    {
        var structures = new List<DeviceStructure>();
        foreach (var fan in _hub.State.Fans)
        {
            if (!fan.BoundToUs) continue;
            if (Slv3Protocol.IsStrimerDevType((byte)fan.DevType))
            {
                var ledCount = StrimerLedCountFor((byte)fan.DevType);
                if (ledCount > 0) structures.Add(BuildStrimerStructure(fan, ledCount));
                continue;
            }
            if (fan.FanCount <= 0) continue;
            structures.Add(BuildStructure(fan));
        }
        return structures;
    }

    // Community mappings pool and auto-apply by card DeviceKey, which the
    // default zone's card carries (a pre-wired custom zone has none), so each
    // cable model gets its own key; the shared prefix is what marks a Strimer.
    private static readonly string StrimerDeviceKeyPrefix =
        DeviceKeyComputer.ForFirstParty(Slv3Protocol.TxVendorId, Slv3Protocol.TxProductId, "wireless-strimer");

    private static string StrimerDeviceKeyFor(byte devType) => $"{StrimerDeviceKeyPrefix}-{devType}";

    /// <summary>Whole-cable LED count for a Strimer dev_type; 0 when the geometry is unknown.</summary>
    internal static int StrimerLedCountFor(byte devType)
    {
        var (lanes, ledsPerLane) = Slv3Protocol.StrimerGeometryFor(devType);
        return lanes * ledsPerLane;
    }

    /// <summary>
    /// Built-in catalog product pre-wired to a Strimer dev_type. dev_type 1 and
    /// 3 each cover two cables with the same LED count, so one product carries both.
    /// </summary>
    internal static string StrimerProductKeyFor(byte devType) => devType switch
    {
        1 => "product:lianli-lian-li-strimer-wireless-gpu-2x8",
        2 => "product:lianli-lian-li-strimer-wireless-24-pin",
        3 => "product:lianli-lian-li-strimer-wireless-gpu-3x8",
        4 => "product:lianli-lian-li-strimer-wireless-cpu-2x8",
        _ => "",
    };

    /// <summary>
    /// True for a structure built by <see cref="BuildStrimerStructure"/>; its
    /// one segment is the whole cable, not fan rings. Keyed on the structure's
    /// DeviceKey prefix, so every Strimer model must keep
    /// <see cref="StrimerDeviceKeyPrefix"/>: a key without it would send the
    /// frame writer down the fan branch.
    /// </summary>
    internal static bool IsStrimerStructure(DeviceStructure structure) =>
        structure.DeviceKey.StartsWith(StrimerDeviceKeyPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Model per Strimer dev_type (lian-li.com Strimer Wireless line), the
    /// unwired default zone's name under the Lian Li Wireless parent.
    /// </summary>
    internal static string StrimerModelNameFor(byte devType) => devType switch
    {
        1 => "Strimer GPU 2x8 / 16-8",
        2 => "Strimer 24-Pin",
        3 => "Strimer GPU 3x8 / 16-12",
        4 => "Strimer CPU 2x8",
        _ => "Strimer",
    };

    // The whole cable as one fixed segment: the firmware sets the LED count,
    // so there is nothing to resize or chain, and the pre-wired product's
    // partition (one zone over segment 0) tiles it exactly. The structure is
    // named for the parent only, so the custom zone reads
    // "Lian Li Wireless - <product>" and the web groups it with the fans.
    private static DeviceStructure BuildStrimerStructure(Slv3FanInfo fan, int ledCount)
    {
        var deviceId = DeviceIdFor(fan.Mac);
        var model = StrimerModelNameFor((byte)fan.DevType);
        var deviceKey = StrimerDeviceKeyFor((byte)fan.DevType);
        var structure = new DeviceStructure
        {
            DeviceId = deviceId,
            Name = "Lian Li Wireless",
            DeviceKey = deviceKey,
        };
        structure.Segments.Add(new StructureSegment
        {
            Index = 0,
            Name = model,
            LedCount = ledCount,
            FrameLedCount = ledCount,
            Resizable = false,
            MaxLedCount = ledCount,
            ZoneType = "linear",
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = $"{deviceId}:strimer",
            Name = $"Lian Li Wireless - {model}",
            RawName = model,
            DeviceKey = deviceKey,
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = 0, Start = 0, Count = ledCount } },
        });
        return structure;
    }

    /// <summary>Half the family's wire LED count: the two ring zones split each fan's LEDs evenly (v1 approximation).</summary>
    internal static int RingLedsFor(Slv3FanInfo fan) =>
        Slv3Protocol.LedsPerFanFor(Slv3Protocol.ClassifyFanFamily((byte)fan.FanType)) / 2;

    private static DeviceStructure BuildStructure(Slv3FanInfo fan)
    {
        var deviceId = DeviceIdFor(fan.Mac);
        var deviceKey = DeviceKeyComputer.ForFirstParty(Slv3Protocol.TxVendorId, Slv3Protocol.TxProductId, "wireless-fan");
        var ledsPerRing = RingLedsFor(fan);
        var ringLeds = fan.FanCount * ledsPerRing;
        var (innerU, innerV) = BuildFanRingUV(fan.FanCount, InnerRadius, ledsPerRing);
        var (outerU, outerV) = BuildFanRingUV(fan.FanCount, OuterRadius, ledsPerRing);

        var structure = new DeviceStructure
        {
            DeviceId = deviceId,
            Name = "Lian Li Wireless Fan",
            DeviceKey = deviceKey,
        };
        structure.Segments.Add(new StructureSegment
        {
            Index = InnerSegment,
            Name = "Inner Ring",
            LedCount = ringLeds,
            FrameLedCount = ringLeds,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = innerU,
            DefaultV = innerV,
        });
        structure.Segments.Add(new StructureSegment
        {
            Index = OuterSegment,
            Name = "Outer Ring",
            LedCount = ringLeds,
            FrameLedCount = ringLeds,
            Resizable = false,
            ZoneType = "linear",
            DefaultU = outerU,
            DefaultV = outerV,
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = $"{deviceId}:inner",
            Name = "Lian Li Wireless - Inner Ring",
            RawName = "Inner Ring",
            DeviceKey = DeviceKeyComputer.ForFirstParty(Slv3Protocol.TxVendorId, Slv3Protocol.TxProductId, "wireless-fan-inner"),
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = InnerSegment, Start = 0, Count = ringLeds } },
        });
        structure.DefaultZones.Add(new DefaultZoneDef
        {
            Id = $"{deviceId}:outer",
            Name = "Lian Li Wireless - Outer Ring",
            RawName = "Outer Ring",
            DeviceKey = DeviceKeyComputer.ForFirstParty(Slv3Protocol.TxVendorId, Slv3Protocol.TxProductId, "wireless-fan-outer"),
            LegacyZoneIndex = -1,
            Slices = { new ZoneSlice { Segment = OuterSegment, Start = 0, Count = ringLeds } },
        });
        return structure;
    }

    // One ring of ledsPerRing LEDs per fan, fans laid side by side along
    // u; radius/fans in u keeps each ring round inside its column. Mirrors
    // LianLiZoneSupport.BuildFanRingUV.
    private static (float[] u, float[] v) BuildFanRingUV(int fans, float radius, int ledsPerRing)
    {
        var ledCount = fans * ledsPerRing;
        var u = new float[ledCount];
        var v = new float[ledCount];
        for (var f = 0; f < fans; f++)
        {
            var centerU = (f + 0.5f) / fans;
            for (var i = 0; i < ledsPerRing; i++)
            {
                var angle = (i / (double)ledsPerRing) * 2.0 * Math.PI;
                u[f * ledsPerRing + i] = centerU + (radius / fans) * (float)Math.Cos(angle);
                v[f * ledsPerRing + i] = 0.5f + radius * (float)Math.Sin(angle);
            }
        }
        return (u, v);
    }

    /// <summary>4-col, 2-row grid, same shape as the MiniHub/NP50 defaults,
    /// so zone cards stay on canvas instead of wrapping past the bottom
    /// edge.</summary>
    internal static (float x, float y, float w, float h) DefaultLayout(int slot)
    {
        const float BaseX = 40f;
        const float Y = 370f;
        const float W = 120f;
        const float H = 105f;
        const float Gap = 140f;
        const int Cols = 4;
        const int Rows = 2; // 370 + 105 + 105 = 580 ≤ canvas bottom
        const float RowGap = 105f;
        var s = ((slot % (Cols * Rows)) + Cols * Rows) % (Cols * Rows);
        var col = s % Cols;
        var row = s / Cols;
        return (BaseX + col * Gap, Y + row * RowGap, W, H);
    }
}
