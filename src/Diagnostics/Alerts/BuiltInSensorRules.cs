using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Persistence;

namespace Nexus.Service.Diagnostics.Alerts;

/// <summary>A detected device whose temperature the Cooling history plots. <see cref="SeriesId"/> is the monitoring history series id; <see cref="ComponentId"/> is the raw id the ignore list uses. <see cref="CpuMaxC"/> is the CPU's own maximum temperature, set on the CPU and any integrated GPU so their default limit follows it.</summary>
public sealed record BuiltInDevice(string Kind, string SeriesId, string Name, bool Integrated, string ComponentId, BuiltInSource? Source = null, double? CpuMaxC = null);

/// <summary>
/// One temperature rule per detected CPU, GPU, drive and (when the machine reports one) memory, id <c>builtin:&lt;history series id&gt;</c>.
/// They are ordinary <see cref="SensorAlertRule"/>s with a synthetic sensor the engine resolves itself; the user can turn one
/// off or move its limit but never delete or re-point it. A rule is listed for every device seen since the service started, so a
/// device that drops out of one pass keeps its entry (reading unavailable) and its state; its stored settings stay either way. The legacy per-kind rows (<c>builtin-cpu</c> and so on) are hidden templates
/// that give a device first seen after an upgrade the limits the user had.
/// </summary>
public static class BuiltInSensorRules
{
    /// <summary>Resolver category of the synthetic sensors; the engine reads it, the sensor provider has no such category.</summary>
    public const string Category = "builtin";

    public const string IdPrefix = "builtin:";

    public const string CpuTemp = "cpuTemp";
    public const string GpuTemp = "gpuTemp";
    public const string StorageTemp = "storageTemp";
    public const string RamTemp = "ramTemp";
    public const string FanSpeed = "fanSpeed";

    public const string CpuSeriesId = "cpu-temp";
    public const string GpuSeriesPrefix = "gpu-temp:";
    public const string MemorySeriesId = "mem-temp";
    public const string DriveSeriesPrefix = "drive-temp:";
    public const string FanSeriesPrefix = "fan:";

    /// <summary>Health component id prefix of a fan stall; the ignore list holds the same id, so it is the one on/off switch of a fan entry.</summary>
    public const string FanComponentPrefix = "cooling:";

    /// <summary>Hold before a built-in raises. The temperatures swing second to second under load, so this is longer than a user rule's.</summary>
    public const int RaiseAfterSeconds = 60;

    /// <summary>Same clear delay as any new sensor rule.</summary>
    public const int ClearAfterSeconds = 5;

    /// <summary>Stored settings generation: at this value the built-ins are per device, so the legacy per-kind seeding has run.</summary>
    public const int PerDeviceVersion = 2;

    /// <summary>Health-check switches generation: the first step retired the GPU, storage and cooling toggles; the second dropped the per-GPU ignore entries, since GPU throttle and TDR are always checked.</summary>
    public const int HealthChecksVersion = 2;

    private sealed record Spec(string Kind, string TemplateId, double LimitMin, double LimitMax, double NewInstallHigh);

    // New-install limits sit in each component's throttle region (Intel runs the package near its limit under load by design), so only a real thermal event alerts.
    private static readonly Spec[] Specs =
    {
        new(CpuTemp, "builtin-cpu", 20, 100, 100),
        new(GpuTemp, "builtin-gpu", 20, 100, 90),
        new(StorageTemp, "builtin-storage", 20, 100, 75),
        new(RamTemp, "builtin-ram", 20, 100, 75),
    };

    // Fans are tracked for their speed history; a limit is optional and none is set by default.
    private static readonly Spec FanSpec = new(FanSpeed, "", 0, 100_000, 0);

    private static Spec? SpecOf(string? kind) => kind == FanSpeed ? FanSpec : Specs.FirstOrDefault(s => s.Kind == kind);

    public static bool IsKind(string? kind) => SpecOf(kind) is not null;

    /// <summary>The sensor type a kind's synthetic sensor carries.</summary>
    public static string SensorType(string? kind) => kind == FanSpeed ? "Fan" : "Temperature";

    /// <summary>The unit of a kind's reading.</summary>
    public static string Units(string? kind) => kind == FanSpeed ? "RPM" : "°C";

    /// <summary>Reserved ids: the per-device <c>builtin:</c> rules and the hidden <c>builtin-</c> templates. A user rule id is a GUID.</summary>
    public static bool IsBuiltInId(string id) => id.StartsWith("builtin", StringComparison.Ordinal);

    /// <summary>A hidden legacy per-kind row: never listed, evaluated or counted.</summary>
    public static bool IsTemplate(SensorAlertRule rule) => rule.BuiltIn is not null && Specs.Any(s => s.TemplateId == rule.Id);

    public static string RuleId(string seriesId) => IdPrefix + seriesId;

    public static string SeriesId(string ruleId) => ruleId[IdPrefix.Length..];

    /// <summary>The Cooling chart order of a series: cpu, gpu, memory, drive, then by id.</summary>
    public static int KindRank(string seriesId) =>
        seriesId == CpuSeriesId ? 0
        : seriesId.StartsWith(GpuSeriesPrefix, StringComparison.Ordinal) ? 1
        : seriesId == MemorySeriesId ? 2
        : seriesId.StartsWith(FanSeriesPrefix, StringComparison.Ordinal) ? 4
        : 3;

    /// <summary>The stored limit range of a kind, so a hand-edited or stale value is pulled back to something the UI can show.</summary>
    public static (double Min, double Max) LimitRange(string kind)
    {
        var spec = SpecOf(kind)!;
        return (spec.LimitMin, spec.LimitMax);
    }

    /// <summary>The high limit a newly seen device starts with. An integrated GPU shares the CPU die, so it follows the CPU default and never alerts before the CPU entry. The CPU default is the CPU's own maximum when known (<paramref name="cpuMaxC"/>), else the kind's flat default.</summary>
    public static double DefaultHigh(string kind, bool integrated, double? cpuMaxC = null)
    {
        var followsCpu = FollowsCpu(kind, integrated);
        var spec = Specs.First(s => s.Kind == (followsCpu ? CpuTemp : kind));
        return followsCpu && cpuMaxC is { } max && double.IsFinite(max) ? CpuTemperatureDefault.LimitFromMax(max) : spec.NewInstallHigh;
    }

    /// <summary>The CPU entry and an integrated GPU (it shares the CPU die) default to the CPU's own maximum.</summary>
    private static bool FollowsCpu(string kind, bool integrated) => kind == CpuTemp || (integrated && kind == GpuTemp);

    /// <summary>
    /// True when this stored built-in still holds the default it was seeded with (never edited) and the device's default now differs:
    /// a live reading arrived after the table value was used, or the CPU changed.
    /// </summary>
    public static bool CpuDefaultStale(SensorAlertRule rule, BuiltInDevice device) =>
        FollowsCpu(device.Kind, device.Integrated)
        && rule.SeededHigh is { } seeded
        && rule.High == seeded
        && DefaultHigh(device.Kind, device.Integrated, device.CpuMaxC) != seeded;

    /// <summary>Moves an untouched CPU-following limit to the device's current default (see <see cref="CpuDefaultStale"/>).</summary>
    public static void RefreshCpuDefault(SensorAlertRule rule, BuiltInDevice device)
    {
        var high = DefaultHigh(device.Kind, device.Integrated, device.CpuMaxC);
        rule.High = high;
        rule.SeededHigh = high;
    }

    /// <summary>Makes a client-sent built-in safe to store: keeps the stored identity and sensor, and clamps each limit that is set; either may be null.</summary>
    public static void Pin(SensorAlertRule rule, SensorAlertRule current)
    {
        rule.Id = current.Id;
        rule.BuiltIn = current.BuiltIn;
        rule.SeededHigh = current.SeededHigh;
        rule.Sensor = new SensorRef
        {
            Category = current.Sensor.Category,
            Id = current.Sensor.Id,
            Name = current.Sensor.Name,
            Type = current.Sensor.Type,
            ParentName = current.Sensor.ParentName,
        };
        var spec = SpecOf(rule.BuiltIn)!;
        rule.Low = ClampLimit(rule.Low, spec);
        rule.High = ClampLimit(rule.High, spec);
    }

    /// <summary>A set limit is pulled into the kind's range; a non-finite one counts as unset.</summary>
    private static double? ClampLimit(double? value, Spec spec) =>
        value is { } v && double.IsFinite(v) ? Math.Clamp(v, spec.LimitMin, spec.LimitMax) : null;

    /// <summary>
    /// Cleans the stored list and, once per file, seeds the hidden per-kind templates. Runs on every settings load.
    /// A present built-in is only clamped, so the user's edits survive. Returns whether anything changed.
    /// </summary>
    /// <param name="newInstall">True for a settings file that did not exist: no templates, every device takes the new-install defaults. Otherwise a missing template is seeded from the legacy diagnostics fields, so an upgrade keeps the user's edited limits, toggles and notification choice (a limit still at the old default takes the new one).</param>
    public static bool Ensure(DiagnosticsSettings diagnostics, bool newInstall)
    {
        var changed = false;
        if (diagnostics.SensorAlerts is null)
        {
            diagnostics.SensorAlerts = new List<SensorAlertRule>();
            changed = true;
        }
        var list = diagnostics.SensorAlerts;
        // A hand-edited file can hold null or id-less entries, or a built-in id with no kind; they would break every lookup.
        if (list.RemoveAll(r => r is null || string.IsNullOrEmpty(r.Id)
            || (IsBuiltInId(r.Id) && !IsKind(r.BuiltIn))) > 0)
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
        if (diagnostics.BuiltInSensorsVersion < PerDeviceVersion)
        {
            if (!newInstall)
            {
                foreach (var spec in Specs.Where(spec => !list.Any(r => r.Id == spec.TemplateId)))
                {
                    list.Add(MigratedTemplate(spec, diagnostics));
                }
            }
            diagnostics.BuiltInSensorsVersion = PerDeviceVersion;
            changed = true;
        }
        if (diagnostics.HealthChecksVersion < HealthChecksVersion)
        {
            // A user who had one off keeps that intent per drive and per fan; the devices are not known at load, so the engine applies it on first sight.
            if (!newInstall)
            {
                diagnostics.PendingFansOff = !diagnostics.Components.Cooling;
                diagnostics.PendingDrivesOff = !diagnostics.Components.Storage;
            }
            diagnostics.Components.Gpu = true;
            diagnostics.Components.Storage = true;
            diagnostics.Components.Cooling = true;
            diagnostics.HealthChecksVersion = 1;
            changed = true;
        }
        if (diagnostics.HealthChecksVersion < 2)
        {
            diagnostics.IgnoredComponents?.RemoveAll(id => id is not null && id.StartsWith("gpu:", StringComparison.Ordinal));
            diagnostics.HealthChecksVersion = HealthChecksVersion;
            changed = true;
        }
        foreach (var rule in list.Where(r => r.BuiltIn is not null))
        {
            changed |= Repin(rule);
        }
        return changed;
    }

    /// <summary>The factory state of one device's built-in, for "Restore default" and first sight: the new-install values, not the user's legacy ones.</summary>
    public static SensorAlertRule Defaults(BuiltInDevice device) => new()
    {
        Id = RuleId(device.SeriesId),
        Enabled = true,
        Sensor = FixedSensor(device),
        High = device.Kind == FanSpeed ? null : DefaultHigh(device.Kind, device.Integrated, device.CpuMaxC),
        SeededHigh = device.Kind == FanSpeed ? null : DefaultHigh(device.Kind, device.Integrated, device.CpuMaxC),
        RaiseAfterSeconds = RaiseAfterSeconds,
        ClearAfterSeconds = ClearAfterSeconds,
        Severity = SensorAlertRules.Act,
        NotifyDesktop = true,
        ShowInHealth = true,
        RepeatMinutes = 0,
        BuiltIn = device.Kind,
    };

    /// <summary>
    /// The rule a device gets the first time it is seen: the kind's template when an upgrade left one (its limits, toggle,
    /// severity, hold and notify choice), else the defaults. A drive on the ignore list starts disabled.
    /// </summary>
    public static SensorAlertRule Seed(BuiltInDevice device, DiagnosticsSettings diagnostics)
    {
        var rule = Defaults(device);
        if (device.Kind == FanSpeed)
        {
            rule.Enabled = diagnostics.IgnoredComponents?.Contains(device.ComponentId, StringComparer.Ordinal) != true;
            return rule;
        }
        var spec = Specs.First(s => s.Kind == device.Kind);
        var template = diagnostics.SensorAlerts?.FirstOrDefault(r => r.Id == spec.TemplateId);
        if (template is not null)
        {
            rule.Enabled = template.Enabled;
            // A template limit still equal to its recorded seed was never chosen, so the CPU and an integrated GPU take their own default (rule.High and SeededHigh already hold it).
            var untouched = FollowsCpu(device.Kind, device.Integrated) && template.SeededHigh is { } ts && template.High == ts;
            if (template.High is { } th && !untouched)
            {
                rule.High = ClampLimit(th, spec);
                rule.SeededHigh = null;
            }
            else if (template.High is null)
            {
                rule.High = null;
                rule.SeededHigh = null;
            }
            rule.Low = ClampLimit(template.Low, spec);
            rule.RaiseAfterSeconds = template.RaiseAfterSeconds;
            rule.Severity = template.Severity;
            rule.NotifyDesktop = template.NotifyDesktop;
        }
        if (device.Kind == StorageTemp && diagnostics.IgnoredComponents?.Contains(device.ComponentId, StringComparer.Ordinal) == true)
        {
            rule.Enabled = false;
        }
        return rule;
    }

    /// <summary>Writes a fan entry's on/off into the ignore list the Cooling page's stall toggle uses, so the two never disagree.</summary>
    public static void SetFanEnabled(DiagnosticsSettings diagnostics, BuiltInDevice device, bool enabled)
    {
        var ignored = diagnostics.IgnoredComponents ??= new List<string>();
        if (enabled)
        {
            ignored.RemoveAll(x => x == device.ComponentId);
        }
        else if (!ignored.Contains(device.ComponentId, StringComparer.Ordinal))
        {
            ignored.Add(device.ComponentId);
        }
    }

    private static bool Repin(SensorAlertRule rule)
    {
        var spec = SpecOf(rule.BuiltIn);
        if (spec is null)
        {
            return false;
        }
        var changed = false;
        var type = SensorType(rule.BuiltIn);
        if (rule.Sensor.Category != Category || rule.Sensor.Type != type || rule.Sensor.ParentName != "")
        {
            rule.Sensor.Category = Category;
            rule.Sensor.Type = type;
            rule.Sensor.ParentName = "";
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

    private static SensorAlertRule MigratedTemplate(Spec spec, DiagnosticsSettings d)
    {
        var rule = new SensorAlertRule
        {
            Id = spec.TemplateId,
            Enabled = true,
            Sensor = new SensorRef { Category = Category, Id = spec.TemplateId, Type = "Temperature", ParentName = "" },
            High = spec.NewInstallHigh,
            SeededHigh = spec.NewInstallHigh,
            RaiseAfterSeconds = RaiseAfterSeconds,
            Severity = SensorAlertRules.Act,
            BuiltIn = spec.Kind,
        };
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
            // The user chose this limit, even when it equals a new default.
            rule.SeededHigh = null;
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

    /// <summary>The synthetic sensor a device's rule points at; its name is the device name.</summary>
    public static SensorRef FixedSensor(BuiltInDevice device) => new()
    {
        Category = Category,
        Id = device.SeriesId,
        Name = device.Name,
        Type = SensorType(device.Kind),
        ParentName = "",
    };
}
