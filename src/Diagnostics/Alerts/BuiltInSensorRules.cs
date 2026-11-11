using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Persistence;

namespace Nexus.Service.Diagnostics.Alerts;

/// <summary>
/// The four component-temperature rules every install carries: CPU, GPU, storage and memory. They are ordinary
/// <see cref="SensorAlertRule"/>s (same engine, state, history, series and notices) with a fixed id and a synthetic
/// sensor the engine resolves itself, so the user can turn one off or move its limit but never delete or re-point it.
/// </summary>
public static class BuiltInSensorRules
{
    /// <summary>Resolver category of the synthetic sensors; the engine reads it, the sensor provider has no such category.</summary>
    public const string Category = "builtin";

    public const string CpuTemp = "cpuTemp";
    public const string GpuTemp = "gpuTemp";
    public const string StorageTemp = "storageTemp";
    public const string RamTemp = "ramTemp";

    /// <summary>Hold before a built-in raises. The temperatures swing second to second under load, so this is longer than a user rule's.</summary>
    public const int RaiseAfterSeconds = 60;

    /// <summary>Same clear delay as any new sensor rule.</summary>
    public const int ClearAfterSeconds = 5;

    private sealed record Spec(string Kind, string Id, string SensorId, string SensorName, double LimitMin, double LimitMax, double NewInstallHigh);

    // Order is the display order and the stored order (built-ins first). New-install limits sit in each component's throttle region (Intel runs the package near its limit under load by design), so only a real thermal event alerts.
    private static readonly Spec[] Specs =
    {
        new(CpuTemp, "builtin-cpu", "cpu", "CPU", 60, 105, 100),
        new(GpuTemp, "builtin-gpu", "gpu", "GPU", 60, 105, 90),
        new(StorageTemp, "builtin-storage", "storage", "Storage", 40, 90, 75),
        new(RamTemp, "builtin-ram", "ram", "Memory", 40, 90, 75),
    };

    public static IReadOnlyList<string> Ids { get; } = Specs.Select(s => s.Id).ToArray();

    public static bool IsKind(string? kind) => Specs.Any(s => s.Kind == kind);

    public static bool IsBuiltInId(string id) => Specs.Any(s => s.Id == id);

    /// <summary>The stored limit range of a kind: the old Settings slider range, so a hand-edited or stale value is pulled back to something the UI can show.</summary>
    public static (double Min, double Max) LimitRange(string kind)
    {
        var spec = Specs.First(s => s.Kind == kind);
        return (spec.LimitMin, spec.LimitMax);
    }

    /// <summary>Re-points a built-in at its fixed sensor, and clamps each limit that is set; either may be null. The one place a client-sent built-in is made safe to store.</summary>
    public static void Pin(SensorAlertRule rule)
    {
        var spec = Specs.First(s => s.Kind == rule.BuiltIn);
        rule.Id = spec.Id;
        rule.Sensor = FixedSensor(spec);
        rule.Low = ClampLimit(rule.Low, spec);
        rule.High = ClampLimit(rule.High, spec);
    }

    /// <summary>A set limit is pulled into the kind's range; a non-finite one counts as unset.</summary>
    private static double? ClampLimit(double? value, Spec spec) =>
        value is { } v && double.IsFinite(v) ? Math.Clamp(v, spec.LimitMin, spec.LimitMax) : null;

    /// <summary>
    /// Adds each missing built-in and puts the built-ins first, in fixed order. Runs on every settings load: a version gate
    /// would never reseed after a build without sensor alerts dropped the list and kept the schema version. A present
    /// built-in is only re-pinned, so the user's edits survive. Returns whether anything changed.
    /// </summary>
    /// <param name="newInstall">True for a settings file that did not exist: the new-install defaults. Otherwise a missing built-in is seeded from the legacy diagnostics fields, so an upgrade keeps the user's edited limits, toggles and notification choice (a limit still at the old default takes the new one).</param>
    public static bool Ensure(DiagnosticsSettings diagnostics, bool newInstall)
    {
        var changed = false;
        if (diagnostics.SensorAlerts is null)
        {
            diagnostics.SensorAlerts = new List<SensorAlertRule>();
            changed = true;
        }
        var list = diagnostics.SensorAlerts;
        // A hand-edited file can hold null or id-less entries; they would break every lookup below.
        if (list.RemoveAll(r => r is null || string.IsNullOrEmpty(r.Id)) > 0)
        {
            changed = true;
        }
        foreach (var rule in list)
        {
            if (rule.Sensor is null)
            {
                rule.Sensor = new SensorRef();
                changed = true;
            }
        }
        foreach (var spec in Specs)
        {
            var index = list.FindIndex(r => r.Id == spec.Id);
            if (index < 0)
            {
                list.Add(newInstall ? NewInstallRule(spec) : MigratedRule(spec, diagnostics));
                changed = true;
                continue;
            }
            changed |= Repin(list[index], spec);
        }

        var ordered = Specs.Select(spec => list.First(r => r.Id == spec.Id)).Concat(list.Where(r => !Ids.Contains(r.Id))).ToList();
        if (!ordered.SequenceEqual(list))
        {
            list.Clear();
            list.AddRange(ordered);
            changed = true;
        }
        return changed;
    }

    /// <summary>The factory state of one built-in, for "Restore default": the new-install values, not the user's legacy ones.</summary>
    public static SensorAlertRule Defaults(string id) => NewInstallRule(Specs.First(s => s.Id == id));

    private static bool Repin(SensorAlertRule rule, Spec spec)
    {
        var changed = false;
        if (rule.BuiltIn != spec.Kind)
        {
            rule.BuiltIn = spec.Kind;
            changed = true;
        }
        var pinned = FixedSensor(spec);
        if (rule.Sensor is null || rule.Sensor.Category != pinned.Category || rule.Sensor.Id != pinned.Id
            || rule.Sensor.Name != pinned.Name || rule.Sensor.Type != pinned.Type || rule.Sensor.ParentName != pinned.ParentName)
        {
            rule.Sensor = pinned;
            changed = true;
        }
        // Both limits are optional (none = tracking only); a hand edit is only pulled into range, and a crossed band loses its low.
        var high = ClampLimit(rule.High, spec);
        var low = ClampLimit(rule.Low, spec);
        if (low is { } lo && high is { } hi && lo >= hi)
        {
            low = null;
        }
        if (rule.High != high || rule.Low != low)
        {
            rule.High = high;
            rule.Low = low;
            changed = true;
        }
        return changed;
    }

    private static SensorAlertRule NewInstallRule(Spec spec) => new()
    {
        Id = spec.Id,
        Enabled = true,
        Sensor = FixedSensor(spec),
        High = spec.NewInstallHigh,
        RaiseAfterSeconds = RaiseAfterSeconds,
        ClearAfterSeconds = ClearAfterSeconds,
        Severity = SensorAlertRules.Watch,
        NotifyDesktop = true,
        ShowInHealth = true,
        RepeatMinutes = 0,
        BuiltIn = spec.Kind,
    };

    private static SensorAlertRule MigratedRule(Spec spec, DiagnosticsSettings d)
    {
        var rule = NewInstallRule(spec);
        var (legacyEnabled, legacyLimit, defaultLimit) = spec.Kind switch
        {
            CpuTemp => (d.Components.Cpu, d.Thresholds.CpuC, TemperatureInsights.CpuThresholdC),
            GpuTemp => (d.Components.Gpu, d.Thresholds.GpuC, TemperatureInsights.GpuThresholdC),
            StorageTemp => (d.Components.Storage, d.Thresholds.StorageC, TemperatureInsights.StorageThresholdC),
            _ => (d.Components.Ram, d.Thresholds.RamC, TemperatureInsights.RamThresholdC),
        };
        rule.Enabled = legacyEnabled;
        // A limit still at the old shipped default was never chosen by the user, so it moves to the new default; an edited one is kept.
        if (double.IsFinite(legacyLimit) && legacyLimit != defaultLimit)
        {
            rule.High = Math.Clamp(legacyLimit, spec.LimitMin, spec.LimitMax);
        }
        // What the user effectively had: the old high-temperature toast needed both the master and its own category.
        rule.NotifyDesktop = d.Notifications.Enabled && d.Notifications.HighTemp;
        // Ignoring the aggregate "cooling" component dropped every temperature warning, in health and notifications.
        if (d.IgnoredComponents?.Contains("cooling") == true)
        {
            rule.Enabled = false;
        }
        return rule;
    }

    private static SensorRef FixedSensor(Spec spec) => new()
    {
        Category = Category,
        Id = spec.SensorId,
        Name = spec.SensorName,
        Type = "Temperature",
        ParentName = "",
    };
}
