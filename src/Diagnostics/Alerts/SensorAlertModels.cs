using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;

namespace Nexus.Service.Diagnostics.Alerts;

/// <summary>Wire value of <see cref="SensorAlertState.State"/>.</summary>
public static class SensorAlertStates
{
    public const string Ok = "ok";
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Unavailable = "unavailable";
}

/// <summary>Runtime state of one rule. Not persisted.</summary>
public sealed class SensorAlertState
{
    public string RuleId { get; set; } = "";
    public string State { get; set; } = SensorAlertStates.Ok;
    public double? Value { get; set; }
    /// <summary>ISO 8601 UTC start of the current pending, active or unavailable stretch.</summary>
    public string? Since { get; set; }
    /// <summary>Peak above, or trough below, seen during the active episode.</summary>
    public double? Extreme { get; set; }
    /// <summary>"above" | "below" while pending or active.</summary>
    public string? Direction { get; set; }
    public bool Acknowledged { get; set; }
}

public sealed class SensorAlertHistoryEntry
{
    public string RuleId { get; set; } = "";
    public string SensorName { get; set; } = "";
    public string Direction { get; set; } = "";
    public string StartedUtc { get; set; } = "";
    public string? EndedUtc { get; set; }
    public double Extreme { get; set; }
    public double Threshold { get; set; }
}

public sealed class SensorAlertsResponse
{
    public List<SensorAlertRule> Rules { get; set; } = new();
    public List<SensorAlertState> States { get; set; } = new();
    public List<SensorAlertHistoryEntry> History { get; set; } = new();
}

/// <summary>Inline payload of the diagnostics/alerts topic.</summary>
public sealed class SensorAlertsFrame
{
    public List<SensorAlertState> States { get; set; } = new();
}

/// <summary>Rule limits, validation and the per-type clear hysteresis.</summary>
public static class SensorAlertRules
{
    public const int MaxRules = 10;
    public const int MaxSeconds = 600;
    public const string Watch = "watch";
    public const string Act = "act";

    private static readonly int[] AllowedRepeats = { 0, 5, 15, 60 };

    /// <summary>Clamps the delay fields in place. Run before <see cref="Validate"/> and before storing.</summary>
    public static void Normalize(SensorAlertRule rule)
    {
        rule.RaiseAfterSeconds = Math.Clamp(rule.RaiseAfterSeconds, 0, MaxSeconds);
        rule.ClearAfterSeconds = Math.Clamp(rule.ClearAfterSeconds, 0, MaxSeconds);
        rule.Sensor ??= new SensorRef();
    }

    /// <summary>Null when the rule is storable, else the reason.</summary>
    public static string? Validate(SensorAlertRule rule)
    {
        var sensor = rule.Sensor;
        if (sensor is null || string.IsNullOrWhiteSpace(sensor.Category) || string.IsNullOrWhiteSpace(sensor.Type)
            || (string.IsNullOrWhiteSpace(sensor.Id) && string.IsNullOrWhiteSpace(sensor.Name)))
        {
            return "A sensor with a category, type and id or name is required.";
        }
        if (!SensorSnapshotResolver.IsSupportedCategory(sensor.Category))
        {
            return $"Unknown sensor category '{sensor.Category}'.";
        }
        if (rule.Low is null && rule.High is null)
        {
            return "Set a low or a high limit.";
        }
        if ((rule.Low is { } l && !double.IsFinite(l)) || (rule.High is { } h && !double.IsFinite(h)))
        {
            return "Limits must be finite numbers.";
        }
        if (rule.Low is { } lo && rule.High is { } hi && lo >= hi)
        {
            return "The low limit must be below the high limit.";
        }
        if (rule.Severity != Watch && rule.Severity != Act)
        {
            return "Severity must be 'watch' or 'act'.";
        }
        if (Array.IndexOf(AllowedRepeats, rule.RepeatMinutes) < 0)
        {
            return "Repeat must be 0, 5, 15 or 60 minutes.";
        }
        return null;
    }

    /// <summary>Distance inside the band a reading must come back before it counts as clear.</summary>
    public static double Hysteresis(SensorAlertRule rule)
    {
        var fixedValue = rule.Sensor.Type switch
        {
            "Temperature" => 2.0,
            "Current" => 0.5,
            "Power" => 5.0,
            "Voltage" => 0.05,
            "Fan" => 100.0,
            "Load" or "Control" or "Level" => 2.0,
            _ => double.NaN,
        };
        double band;
        if (rule.Low is { } lo && rule.High is { } hi)
        {
            band = hi - lo;
        }
        else
        {
            band = Math.Abs(rule.Low ?? rule.High ?? 0);
        }
        var hyst = double.IsNaN(fixedValue) ? Math.Max(band * 0.01, 0.001) : fixedValue;
        // A narrow band must keep a non-empty clear zone.
        if (rule.Low is not null && rule.High is not null)
        {
            hyst = Math.Min(hyst, band / 4);
        }
        return hyst;
    }
}
