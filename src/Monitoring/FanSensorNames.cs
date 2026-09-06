using System.Collections.Generic;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Monitoring;

/// <summary>
/// Applies the cooling page's user-renamed fan headers to the sensor lists every picker,
/// gauge and deck key reads, so a header renamed there reads the same everywhere. The
/// rename is stored against the fan channel id, which is a PWM control sensor; sensors are
/// keyed by tachometer id. <see cref="FanChannel.RpmSensorId"/> is the only bridge.
/// </summary>
public static class FanSensorNames
{
    /// <summary>
    /// The list with renamed fan headers carrying their custom name. Returns the input
    /// untouched when nothing matches, and otherwise copies only the sensors it renames, so
    /// a provider that caches its readings is never mutated.
    /// </summary>
    public static IReadOnlyList<HardwareSensor> WithRenames(
        IReadOnlyList<HardwareSensor> sensors,
        IReadOnlyDictionary<string, string>? byRpmSensor)
    {
        if (byRpmSensor is null || byRpmSensor.Count == 0) return sensors;

        List<HardwareSensor>? renamed = null;
        for (var i = 0; i < sensors.Count; i++)
        {
            if (!byRpmSensor.TryGetValue(sensors[i].Id, out var custom)) continue;
            renamed ??= new List<HardwareSensor>(sensors);
            var clone = sensors[i].Clone();
            clone.Name = custom;
            renamed[i] = clone;
        }

        return renamed ?? sensors;
    }

    /// <summary>
    /// One sensor, renamed if it is a renamed header's tach. For callers that resolve a
    /// single sensor: renaming after the lookup keeps id- and name-keyed lookups matching
    /// against the hardware names they were stored from.
    /// </summary>
    public static HardwareSensor WithRename(
        HardwareSensor sensor,
        IReadOnlyDictionary<string, string>? byRpmSensor)
    {
        if (byRpmSensor is null || !byRpmSensor.TryGetValue(sensor.Id, out var custom)) return sensor;
        var clone = sensor.Clone();
        clone.Name = custom;
        return clone;
    }

    /// <summary>
    /// The tach-sensor-id to custom-name map, or null when nothing is renamed, so callers
    /// can skip the sensor walk outright.
    /// </summary>
    public static Dictionary<string, string>? BuildMap(
        IReadOnlyList<FanChannel> channels,
        IReadOnlyDictionary<string, string> fanNames)
    {
        if (fanNames.Count == 0 || channels.Count == 0) return null;

        Dictionary<string, string>? byRpmSensor = null;
        foreach (var ch in channels)
        {
            if (string.IsNullOrEmpty(ch.RpmSensorId)) continue;
            if (!fanNames.TryGetValue(ch.Id, out var custom)) continue;
            if (string.IsNullOrWhiteSpace(custom)) continue;
            byRpmSensor ??= new Dictionary<string, string>();
            byRpmSensor[ch.RpmSensorId] = custom.Trim();
        }

        return byRpmSensor;
    }
}
