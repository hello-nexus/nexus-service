using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// One-time move of a wireless SL-INF Flex chain's saved zone data from the old two-ring split
/// (inner = wire 0..21 of each fan, outer = wire 22..43) to Center / Inner / Outer. TL Flex keeps
/// the same LEDs under both layouts and every other family is untouched.
/// </summary>
internal static class Slv3FlexZoneMigration
{
    private const int OldInnerPerFan = 22;

    /// <summary>Runs once per chain MAC; false when it was already done.</summary>
    public static bool Apply(NexusSettings settings, string mac)
    {
        var done = settings.Devices.LianLiWireless.FlexZonesMigrated;
        if (done.Contains(mac, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }
        var deviceId = Slv3LightingDeviceProvider.DeviceIdFor(mac);
        settings.Devices.ZonePartitions.Remove(deviceId);
        foreach (var suffix in new[] { "inner", "outer" })
        {
            var zoneId = $"{deviceId}:{suffix}";
            settings.Devices.LedGroups.Remove(zoneId);
            settings.Devices.AppliedMappings.Remove(zoneId);
        }
        if (settings.Devices.DeviceLedOverrides.TryGetValue(deviceId, out var overrides))
        {
            var moved = new List<SegmentLedOverride>(overrides.Count);
            foreach (var o in overrides)
            {
                if (Remap(o) is { } m)
                {
                    moved.Add(m);
                }
            }
            if (moved.Count == 0)
            {
                settings.Devices.DeviceLedOverrides.Remove(deviceId);
            }
            else
            {
                settings.Devices.DeviceLedOverrides[deviceId] = moved;
            }
        }
        done.Add(mac);
        return true;
    }

    /// <summary>The override at its Center / Inner / Outer segment and local index by the per-fan formula, whatever the chain's fan count; null only for an override that never pointed at a fan LED.</summary>
    internal static SegmentLedOverride? Remap(SegmentLedOverride old)
    {
        if (old.Segment is < 0 or > 1 || old.LedIndex < 0)
        {
            return null;
        }
        var fan = old.LedIndex / OldInnerPerFan;
        var inFan = old.LedIndex % OldInnerPerFan;
        var wire = (old.Segment * OldInnerPerFan) + inFan;
        var zones = Slv3LightingDeviceProvider.FlexZonesOf(FlexFamily.SlInfFlex);
        for (var z = 0; z < zones.Length; z++)
        {
            var map = FlexProtocol.ZoneWireIndexes(FlexFamily.SlInfFlex, zones[z].Key);
            for (var k = 0; k < map.Count; k++)
            {
                if (map[k] == wire)
                {
                    return new SegmentLedOverride { Segment = z, LedIndex = (fan * map.Count) + k, U = old.U, V = old.V, Disabled = old.Disabled };
                }
            }
        }
        return null;
    }
}
