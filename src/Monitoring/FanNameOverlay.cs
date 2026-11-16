using System;
using System.Collections.Generic;
using Nexus.Service.Cooling;
using Nexus.Service.Diagnostics.Alerts;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Routes;

namespace Nexus.Service.Monitoring;

/// <summary>
/// Cooling-page fan renames applied to response copies only. Engine state, stored rules and history keep hardware names
/// and ids, so a rename or clearing one never moves a series, a rule or a running alert.
/// </summary>
internal sealed class FanNameOverlay
{
    private const string MotherboardBlockId = "motherboard";

    private readonly Dictionary<string, string> _names;
    private readonly Dictionary<string, FanChannel> _byRuleId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _bySeriesId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FanChannel> _byTach = new(StringComparer.Ordinal);

    private FanNameOverlay(IReadOnlyList<FanChannel> channels, Dictionary<string, string> names)
    {
        _names = names;
        foreach (var ch in channels)
        {
            var seriesId = MetricsHistory.SanitizeId(ch.Id);
            // The first channel wins a SanitizeId collision, as in the engine's fan discovery.
            if (!_byRuleId.TryAdd(BuiltInSensorRules.RuleId(BuiltInSensorRules.FanSeriesPrefix + seriesId), ch))
            {
                continue;
            }
            if (!string.IsNullOrEmpty(ch.RpmSensorId))
            {
                _byTach.TryAdd(ch.RpmSensorId, ch);
            }
            if (Custom(ch.Id) is { } name)
            {
                _bySeriesId["fan:" + seriesId] = name;
                _bySeriesId["fan-duty:" + seriesId] = name;
            }
        }
    }

    /// <summary>Null when nothing is renamed or the channels cannot be read, so the common case never walks them.</summary>
    public static FanNameOverlay? Build(IConfigStore store, IFanControlProvider? fans)
    {
        if (fans is null)
        {
            return null;
        }
        var names = CoolingSnapshots.FanNames(store.Load().Cooling);
        if (names.Count == 0)
        {
            return null;
        }
        try
        {
            return new FanNameOverlay(fans.GetFanChannels(), names);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[fan-names] overlay skipped: {ex.Message}");
            return null;
        }
    }

    private string? Custom(string key) =>
        _names.TryGetValue(key, out var name) && !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;

    private string? GroupName(FanChannel ch) => ch.DeviceId is { } deviceId ? Custom(deviceId) : Custom(MotherboardBlockId);

    /// <summary>The renamed name of a "fan:" or "fan-duty:" history series, or null to keep it.</summary>
    public string? SeriesName(string seriesId) => _bySeriesId.TryGetValue(seriesId, out var name) ? name : null;

    /// <summary>A copy of the response with the fan series relabeled; ids, points and stored history are untouched.</summary>
    public MetricsHistoryResponse Apply(MetricsHistoryResponse response)
    {
        var series = new List<MetricSeriesWire>(response.Series.Count);
        foreach (var s in response.Series)
        {
            series.Add(SeriesName(s.Id) is { } name ? s with { Name = name } : s);
        }
        return response with { Series = series };
    }

    /// <summary>Copies of the rules with built-in fan entries and custom rules on a renamed fan's tach relabeled; the inputs are left as they are.</summary>
    public List<SensorAlertRule> Project(List<SensorAlertRule> rules)
    {
        var output = new List<SensorAlertRule>(rules.Count);
        foreach (var rule in rules)
        {
            if (rule.BuiltIn == BuiltInSensorRules.FanSpeed && _byRuleId.TryGetValue(rule.Id, out var ch))
            {
                output.Add(Project(rule, ch));
            }
            else if (rule.BuiltIn is null && TachName(rule.Sensor.Id) is { } tachName)
            {
                var copy = rule.Clone();
                copy.Sensor.Name = tachName;
                output.Add(copy);
            }
            else
            {
                output.Add(rule);
            }
        }
        return output;
    }

    /// <summary>The renamed name of the fan whose tach sensor has this id, or null.</summary>
    public string? TachName(string sensorId) => !string.IsNullOrEmpty(sensorId) && _byTach.TryGetValue(sensorId, out var ch) ? Custom(ch.Id) : null;

    /// <summary>The display name of a built-in fan rule, or null to keep it.</summary>
    public string? RuleName(string ruleId) => _byRuleId.TryGetValue(ruleId, out var ch) ? Custom(ch.Id) : null;

    private SensorAlertRule Project(SensorAlertRule rule, FanChannel ch)
    {
        var name = Custom(ch.Id);
        var group = GroupName(ch);
        if (name is null && group is null)
        {
            return rule;
        }
        var copy = rule.Clone();
        if (name is not null)
        {
            copy.Sensor.Name = name;
        }
        if (rule.Source is { } source)
        {
            copy.Source = new BuiltInSource
            {
                Kind = source.Kind,
                HardwareName = group ?? source.HardwareName,
                Sensors = source.Sensors,
                Sensor = source.Sensor is { } tach && name is not null
                    ? new SensorRef { Category = tach.Category, Id = tach.Id, Name = name, Type = tach.Type, ParentName = group ?? tach.ParentName }
                    : source.Sensor is { } groupOnly && group is not null
                        ? new SensorRef { Category = groupOnly.Category, Id = groupOnly.Id, Name = groupOnly.Name, Type = groupOnly.Type, ParentName = group }
                        : source.Sensor,
            };
        }
        return copy;
    }
}
