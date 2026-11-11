using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Diagnostics.Alerts;

/// <summary>One resolved reading. Valid is false for a missing, stale, non-finite or implausible sensor.</summary>
internal readonly record struct SensorSample(bool Valid, double Value, string Units, string Source = "");

/// <summary>A finished alert the user has not dismissed yet. Time is wall clock, for display and the expiry.</summary>
internal sealed record EndedAlert(DateTimeOffset EndedUtc, double Peak, double Threshold, string Direction);

[Flags]
internal enum SensorAlertEffect
{
    None = 0,
    Raised = 1,
    Cleared = 2,
    /// <summary>The reported state changed; subscribers should refetch.</summary>
    Changed = 8,
}

/// <summary>Mutable per-rule state advanced by <see cref="SensorAlertEngine.Step"/>.</summary>
internal sealed class SensorAlertRuntime
{
    public string Phase = SensorAlertStates.Ok;
    public DateTimeOffset? PhaseSince;
    /// <summary>Wall-clock stamp of <see cref="PhaseSince"/>, for display only; delays run on the monotonic axis.</summary>
    public DateTimeOffset? PhaseSinceUtc;
    public double? Value;
    public double? Extreme;
    public string? Direction;
    public bool Acknowledged;
    public DateTimeOffset? InvalidSince;
    public DateTimeOffset? InvalidSinceUtc;
    public DateTimeOffset? ClearSince;
    public string Units = "";
    public string Reported = SensorAlertStates.Ok;
    public SensorAlertRule? Config;
    public SensorAlertHistoryEntry? Episode;
    /// <summary>Set when an unacknowledged alert clears; kept until dismissed or it ages out. A new raise or a rule edit drops it.</summary>
    public EndedAlert? Ended;
    /// <summary>Device a built-in's reading comes from (hottest GPU or drive); empty otherwise.</summary>
    public string Source = "";

    public void ResetToOk()
    {
        Phase = SensorAlertStates.Ok;
        PhaseSince = null;
        PhaseSinceUtc = null;
        Extreme = null;
        Direction = null;
        Acknowledged = false;
        ClearSince = null;
    }
}

/// <summary>
/// Evaluates the user's sensor threshold rules once per <see cref="Tick"/> and
/// holds their runtime state and episode history. Notices leave through
/// <see cref="NoticeSink"/> (set by DI to the diagnostics alert path) and state
/// changes through <see cref="Changed"/>, so this type depends on neither the
/// alert service nor the socket hub.
/// </summary>
public sealed class SensorAlertEngine : Nexus.Service.Monitoring.History.ISensorSeriesSource
{
    private static readonly TimeSpan ClearDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan UnavailableAfter = TimeSpan.FromSeconds(10);
    private const int MaxHistory = 100;
    private static readonly TimeSpan SmartInterval = TimeSpan.FromMinutes(1);
    // The first read after service start returns no drives until the hardware walk has run; retry inside the 10 s invalid window.
    private static readonly TimeSpan SmartEmptyRetry = TimeSpan.FromSeconds(5);
    // A machine with no readable drive would otherwise poll every retry forever, so the fast retry covers only the start-up walk.
    private static readonly TimeSpan SmartEmptyRetryWindow = TimeSpan.FromMinutes(1);
    private const string NoticeKind = "sensorAlert";

    private readonly IConfigStore _store;
    private readonly ISensorProvider _sensors;
    private readonly Nexus.Service.Diagnostics.Storage.ISmartHealthSource? _drives;
    private readonly TimeProvider _time;
    private readonly long _stamp0;
    private readonly DateTimeOffset _wall0;
    private readonly TimeSpan? _startupHold;
    // The boot startup delay holds the hardware walk, so the fast SMART retry window must outlast it.
    private TimeSpan SmartRetryWindow => SmartEmptyRetryWindow + (_startupHold ?? Nexus.Service.Lifecycle.StartupDelayGate.Configured);
    private readonly object _lock = new();
    private readonly Dictionary<string, SensorAlertRuntime> _states = new(StringComparer.Ordinal);
    private readonly List<SensorAlertHistoryEntry> _history = new();
    private readonly LogThrottle _warnings = new(TimeSpan.FromMinutes(1));
    private (DateTimeOffset At, IReadOnlyList<HardwareSensor> List)? _smart;

    // Latest resolved reading per enabled rule, swapped whole each tick; the
    // metrics sampler stamps it onto its own 1 Hz samples. The engine and the
    // sampler run on independent 1 Hz timers, so a snapshot younger than the max
    // age is stamped: jitter then never leaves a gap (at worst one second is
    // doubled), while a stall longer than the age leaves a gap, not a flat run.
    private static readonly TimeSpan SeriesMaxAge = TimeSpan.FromMilliseconds(1500);
    private sealed record SeriesEntry(string RuleId, double Value, SensorRef Sensor);

    private sealed record SeriesSnapshot(DateTimeOffset At, IReadOnlyList<SeriesEntry> Entries);

    private Dictionary<string, IReadOnlyList<HardwareSensor>> _lastLists = new(StringComparer.Ordinal);

    private static SensorRef WithId(SensorRef sensor, string id) => new()
    {
        Category = sensor.Category,
        Id = id,
        Name = sensor.Name,
        Type = sensor.Type,
        ParentName = sensor.ParentName,
    };

    /// <summary>
    /// Whether <paramref name="reference"/>'s id names a sensor right now. Reads the latest tick's lists; a category not read
    /// yet is read directly, except smart, which wakes sleeping drives and so counts as not resolving.
    /// </summary>
    public bool IdResolves(SensorRef reference)
    {
        if (string.IsNullOrEmpty(reference.Id))
        {
            return false;
        }
        var lists = Volatile.Read(ref _lastLists);
        if (!lists.TryGetValue(reference.Category, out var list))
        {
            if (reference.Category == "smart")
            {
                return false;
            }
            lists = ReadCategories(new List<SensorAlertRule> { new() { Sensor = reference } }, Mono());
            if (!lists.TryGetValue(reference.Category, out list))
            {
                return false;
            }
        }
        return list.Any(x => x.Id == reference.Id && string.Equals(x.Type, reference.Type, StringComparison.OrdinalIgnoreCase));
    }

    private SeriesSnapshot _series = new(DateTimeOffset.MinValue, Array.Empty<SeriesEntry>());

    /// <summary>
    /// Readings of the enabled rules from the latest tick, for the sample second <paramref name="tsSec"/>.
    /// Empty once that tick is older than the max age, and for a rule that was deleted, disabled or
    /// re-pointed since the tick.
    /// </summary>
    public IReadOnlyList<Nexus.Service.Monitoring.History.SensorSeriesReading> CurrentReadings(long tsSec)
    {
        var snapshot = Volatile.Read(ref _series);
        if (Mono() - snapshot.At > SeriesMaxAge || snapshot.Entries.Count == 0)
        {
            return Array.Empty<Nexus.Service.Monitoring.History.SensorSeriesReading>();
        }
        var live = new List<Nexus.Service.Monitoring.History.SensorSeriesReading>(snapshot.Entries.Count);
        _store.Read(s => LiveReadings(snapshot.Entries, s.Diagnostics.SensorAlerts, live));
        return live;
    }

    private static void LiveReadings(
        IReadOnlyList<SeriesEntry> entries,
        IEnumerable<SensorAlertRule>? rules,
        List<Nexus.Service.Monitoring.History.SensorSeriesReading> into)
    {
        var current = (rules ?? Enumerable.Empty<SensorAlertRule>()).Where(r => r.Enabled).ToDictionary(r => r.Id, StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (current.TryGetValue(e.RuleId, out var rule) && SensorAlertRules.SameSensor(e.Sensor, rule.Sensor, bIdResolves: true))
            {
                into.Add(new Nexus.Service.Monitoring.History.SensorSeriesReading(e.RuleId, e.Value));
            }
        }
    }

    /// <summary>Delivers a notice. The second argument is the shared-cooldown key.</summary>
    public Action<DiagnosticsAlertNotice, string>? NoticeSink { get; set; }

    /// <summary>Raised after a transition or a rule change, never for a plain value update.</summary>
    public event Action? Changed;

    public SensorAlertEngine(IConfigStore store, ISensorProvider sensors, Nexus.Service.Diagnostics.Storage.ISmartHealthSource? drives = null)
        : this(store, sensors, TimeProvider.System, null, drives)
    {
    }

    internal SensorAlertEngine(
        IConfigStore store, ISensorProvider sensors, TimeProvider time, TimeSpan? startupHold = null,
        Nexus.Service.Diagnostics.Storage.ISmartHealthSource? drives = null)
    {
        _store = store;
        _sensors = sensors;
        _drives = drives;
        _time = time;
        _stamp0 = time.GetTimestamp();
        _wall0 = time.GetUtcNow();
        _startupHold = startupHold;
    }

    // Monotonic axis anchored to the wall clock, so a clock jump never shortens or extends a delay.
    private DateTimeOffset Mono() => _wall0 + _time.GetElapsedTime(_stamp0);

    /// <summary>Advances <paramref name="st"/> by one sample and reports what happened.</summary>
    internal static SensorAlertEffect Step(SensorAlertRule rule, SensorSample sample, DateTimeOffset now, SensorAlertRuntime st, DateTimeOffset? wall = null)
    {
        var stamp = wall ?? now;
        var effect = SensorAlertEffect.None;
        if (!sample.Valid)
        {
            if (st.InvalidSince is null)
            {
                st.InvalidSince = now;
                st.InvalidSinceUtc = stamp;
            }
            if (st.Phase != SensorAlertStates.Active)
            {
                st.Value = null;
            }
            if (st.Phase == SensorAlertStates.Pending)
            {
                // A gap breaks "continuously breached".
                st.ResetToOk();
            }
            // Unavailable never clears an active alert.
            st.ClearSince = null;
            return Finish(st, now, effect);
        }

        st.InvalidSince = null;
        st.InvalidSinceUtc = null;
        st.Value = sample.Value;
        st.Units = sample.Units;
        var v = sample.Value;
        var direction = Breach(rule, v);

        if (st.Phase == SensorAlertStates.Ok && direction is not null)
        {
            st.Phase = SensorAlertStates.Pending;
            st.PhaseSince = now;
            st.PhaseSinceUtc = stamp;
            st.Direction = direction;
            st.Extreme = v;
        }
        else if (st.Phase == SensorAlertStates.Pending)
        {
            if (direction is null)
            {
                st.ResetToOk();
            }
            else if (direction != st.Direction)
            {
                st.PhaseSince = now;
                st.PhaseSinceUtc = stamp;
                st.Direction = direction;
                st.Extreme = v;
            }
            else
            {
                st.Extreme = Extend(st.Direction, st.Extreme, v);
            }
        }
        else if (st.Phase == SensorAlertStates.Active)
        {
            if (direction is not null && direction != st.Direction)
            {
                // A band with both limits can be left through the other side.
                st.Direction = direction;
                st.Extreme = v;
            }
            else
            {
                st.Extreme = Extend(st.Direction, st.Extreme, v);
            }
            if (st.Episode is { } open && st.Extreme is { } ex)
            {
                open.Extreme = ex;
                open.Direction = st.Direction ?? open.Direction;
                open.Threshold = (st.Direction == "below" ? rule.Low : rule.High) ?? open.Threshold;
            }
            effect |= StepActive(rule, v, now, stamp, st);
            return Finish(st, now, effect);
        }

        if (st.Phase == SensorAlertStates.Pending && st.PhaseSince is { } since
            && now - since >= TimeSpan.FromSeconds(rule.RaiseAfterSeconds))
        {
            st.Phase = SensorAlertStates.Active;
            st.PhaseSince = now;
            st.PhaseSinceUtc = stamp;
            st.ClearSince = null;
            st.Acknowledged = false;
            st.Ended = null;
            effect |= SensorAlertEffect.Raised;
        }
        return Finish(st, now, effect);
    }

    private static SensorAlertEffect StepActive(SensorAlertRule rule, double v, DateTimeOffset now, DateTimeOffset wall, SensorAlertRuntime st)
    {
        var hyst = SensorAlertRules.Hysteresis(rule);
        var inside = (rule.High is not { } hi || v <= hi - hyst) && (rule.Low is not { } lo || v >= lo + hyst);
        if (inside)
        {
            st.ClearSince ??= now;
            if (now - st.ClearSince.Value >= ClearDelay)
            {
                // An alert the user already dismissed leaves nothing behind.
                st.Ended = st.Acknowledged
                    ? null
                    : new EndedAlert(wall, st.Extreme ?? v, (st.Direction == "below" ? rule.Low : rule.High) ?? 0, st.Direction ?? "above");
                st.ResetToOk();
                return SensorAlertEffect.Cleared;
            }
            return SensorAlertEffect.None;
        }

        st.ClearSince = null;
        return SensorAlertEffect.None;
    }

    private static SensorAlertEffect Finish(SensorAlertRuntime st, DateTimeOffset now, SensorAlertEffect effect)
    {
        var reported = Reported(st, now);
        if (reported != st.Reported)
        {
            st.Reported = reported;
            effect |= SensorAlertEffect.Changed;
        }
        return effect;
    }

    private static string Reported(SensorAlertRuntime st, DateTimeOffset now)
    {
        if (st.Phase == SensorAlertStates.Active)
        {
            return SensorAlertStates.Active;
        }
        if (st.InvalidSince is { } since && now - since >= UnavailableAfter)
        {
            return SensorAlertStates.Unavailable;
        }
        return st.Phase;
    }

    private static string? Breach(SensorAlertRule rule, double v)
    {
        if (rule.High is { } hi && v > hi)
        {
            return "above";
        }
        if (rule.Low is { } lo && v < lo)
        {
            return "below";
        }
        return null;
    }

    private static double Extend(string? direction, double? extreme, double v) =>
        extreme is not { } e ? v : direction == "below" ? Math.Min(e, v) : Math.Max(e, v);

    /// <summary>
    /// The sensor a rule points at, or an invalid sample. <paramref name="healedId"/> is set when the sensor was found by name and its id differs from the stored one.
    /// </summary>
    internal static SensorSample Resolve(IReadOnlyList<HardwareSensor> list, SensorRef reference, out string? healedId)
    {
        healedId = null;
        HardwareSensor? found = null;
        if (!string.IsNullOrEmpty(reference.Id))
        {
            for (var i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s.Id == reference.Id
                    && string.Equals(s.Type, reference.Type, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(reference.Name) || s.Name == reference.Name))
                {
                    found = s;
                    break;
                }
            }
        }
        if (found is null && !string.IsNullOrEmpty(reference.Name))
        {
            for (var i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s.Name != reference.Name || !string.Equals(s.Type, reference.Type, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                found ??= s;
                if (s.Parent.Name == reference.ParentName)
                {
                    found = s;
                    break;
                }
            }
            if (found is not null && found.Id != reference.Id)
            {
                healedId = found.Id;
            }
        }
        if (found is null)
        {
            return new SensorSample(false, 0, "");
        }
        return new SensorSample(IsUsable(found), found.Value, found.Units, found.Parent.Name);
    }

    private static bool IsUsable(HardwareSensor s)
    {
        if (s.Stale || !float.IsFinite(s.Value))
        {
            return false;
        }
        return s.Type switch
        {
            "Temperature" => s.Value > -100f && s.Value <= 250f,
            "Fan" or "Current" => s.Value >= 0f,
            _ => true,
        };
    }

    /// <summary>Copies of the rules and the temperature unit, taken under the store's lock so a route mutation never races the walk.</summary>
    private sealed record Snap(List<SensorAlertRule> Rules, bool Fahrenheit)
    {
        public void Deconstruct(out List<SensorAlertRule> rules, out bool fahrenheit)
        {
            rules = Rules;
            fahrenheit = Fahrenheit;
        }
    }

    private Snap Snapshot()
    {
        var rules = new List<SensorAlertRule>();
        var fahrenheit = false;
        _store.Read(s =>
        {
            if (s.Diagnostics.SensorAlerts is { } list)
            {
                foreach (var rule in list)
                {
                    rules.Add(rule.Clone());
                }
            }
            fahrenheit = string.Equals(s.Units.MonitoringTempUnit, "f", StringComparison.OrdinalIgnoreCase);
        });
        return new Snap(rules, fahrenheit);
    }

    /// <summary>The configured rules, as copies.</summary>
    public List<SensorAlertRule> Rules() => Snapshot().Rules;

    /// <summary>Evaluates every enabled rule once. Does nothing, and reads no sensor, without one.</summary>
    public void Tick()
    {
        var snap = Snapshot();
        var (rules, fahrenheit) = snap;
        var now = Mono();
        var changed = ReconcileCore(rules, now);

        var enabled = rules.Where(r => r.Enabled).ToList();
        var notices = new List<(DiagnosticsAlertNotice Notice, bool Notify, string Key)>();
        var heals = new List<(string RuleId, string SensorName, string SensorType, string NewId)>();
        var readings = new List<SeriesEntry>(enabled.Count);
        if (enabled.Count > 0)
        {
            var lists = ReadCategories(enabled, now);
            Volatile.Write(ref _lastLists, lists);
            var wall = _time.GetUtcNow();
            lock (_lock)
            {
                foreach (var rule in enabled)
                {
                    // A route edit between the snapshot and here has already reset this state to its new config; stepping the old rule on it would undo that.
                    if (!_states.TryGetValue(rule.Id, out var st) || st.Config is not { } cfg || !cfg.SameConfig(rule))
                    {
                        continue;
                    }
                    var list = lists.TryGetValue(rule.Sensor.Category, out var l) ? l : Array.Empty<HardwareSensor>();
                    var sample = Resolve(list, rule.Sensor, out var healed);
                    if (healed is not null)
                    {
                        heals.Add((rule.Id, rule.Sensor.Name, rule.Sensor.Type, healed));
                    }
                    if (sample.Valid && double.IsFinite(sample.Value))
                    {
                        // A healed id is the same sensor, so the entry carries it and compares equal to the rule once the heal lands.
                        readings.Add(new SeriesEntry(rule.Id, sample.Value, healed is null ? rule.Sensor : WithId(rule.Sensor, healed)));
                    }
                    var effect = Step(rule, sample, now, st, wall);
                    if (sample.Valid)
                    {
                        st.Source = sample.Source;
                    }
                    changed |= ApplyEffect(rule, st, effect, wall, fahrenheit, snap, notices);
                }
            }
        }

        changed |= ExpireEnded(_time.GetUtcNow());
        Volatile.Write(ref _series, new SeriesSnapshot(now, readings));
        if (heals.Count > 0)
        {
            HealSensorIds(heals);
        }
        // After the heal, so a healed id already matches the stored rule.
        DropStaleSeries();
        foreach (var (notice, notify, key) in notices)
        {
            if (notify)
            {
                DeliverNotice(notice, key);
            }
        }
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private bool ExpireEnded(DateTimeOffset wall)
    {
        var any = false;
        lock (_lock)
        {
            foreach (var st in _states.Values)
            {
                if (st.Ended is { } ended && wall - ended.EndedUtc >= SensorAlertRules.EndedLinger)
                {
                    st.Ended = null;
                    any = true;
                }
            }
        }
        return any;
    }

    private Dictionary<string, IReadOnlyList<HardwareSensor>> ReadCategories(List<SensorAlertRule> enabled, DateTimeOffset now)
    {
        var categories = enabled.Select(r => r.Sensor.Category).Distinct(StringComparer.Ordinal).ToList();
        var sources = default(SensorSnapshotSources);
        if (categories.Any(SensorSnapshotResolver.CategoryUsesExtras))
        {
            try
            {
                sources = new SensorSnapshotSources(Extras: _sensors.GetSensorExtras());
            }
            catch (Exception ex)
            {
                Warn("extras", $"[sensor-alerts] extras read failed: {ex.Message}", now);
            }
        }
        var lists = new Dictionary<string, IReadOnlyList<HardwareSensor>>(StringComparer.Ordinal);
        foreach (var category in categories)
        {
            try
            {
                lists[category] = ReadCategory(category, sources, now);
            }
            catch (Exception ex)
            {
                Warn(category, $"[sensor-alerts] {category} read failed: {ex.Message}", now);
                lists[category] = Array.Empty<HardwareSensor>();
            }
        }
        return lists;
    }

    private IReadOnlyList<HardwareSensor> ReadCategory(string category, SensorSnapshotSources sources, DateTimeOffset now)
    {
        if (category == BuiltInSensorRules.Category)
        {
            return ReadBuiltIns();
        }
        // Drive temperatures exist only under smart, and each read wakes sleeping drives, so it is sampled slowly.
        if (category == "smart")
        {
            if (_smart is { } cached && now - cached.At < (cached.List.Count == 0 && now - _wall0 < SmartRetryWindow ? SmartEmptyRetry : SmartInterval))
            {
                return cached.List;
            }
            var fresh = SensorSnapshotResolver.GetCategorySensors(_sensors, category, sources);
            _smart = (now, fresh);
            return fresh;
        }
        // The cached read leaves the Super I/O refresh to the regular locked walk.
        if (category == "motherboard")
        {
            return _sensors.GetMotherboardSensorsCached();
        }
        return SensorSnapshotResolver.GetCategorySensors(_sensors, category, sources);
    }

    /// <summary>
    /// One synthetic temperature sensor per component, from the same picks the temperature history uses: the CPU package
    /// (else the first CPU temperature), the hottest dedicated GPU (none means no GPU reading, so a SoC or APU graphics
    /// die never duplicates the CPU), the hottest drive that is not ignored (the SMART cache, so no drive wakes) and the
    /// hottest memory-module sensor. Each component reads on its own, so one failing source leaves the others live.
    /// </summary>
    private IReadOnlyList<HardwareSensor> ReadBuiltIns()
    {
        var list = new List<HardwareSensor>(4);
        var now = Mono();
        TryAdd(list, "cpu", now, ReadCpuBuiltIn);
        TryAdd(list, "gpu", now, ReadGpuBuiltIn);
        TryAdd(list, "storage", now, ReadStorageBuiltIn);
        TryAdd(list, "ram", now, ReadRamBuiltIn);
        return list;
    }

    private void TryAdd(List<HardwareSensor> list, string key, DateTimeOffset now, Func<HardwareSensor?> read)
    {
        try
        {
            if (read() is { } sensor)
            {
                list.Add(sensor);
            }
        }
        catch (Exception ex)
        {
            Warn("builtin-" + key, $"[sensor-alerts] built-in {key} read failed: {ex.Message}", now);
        }
    }

    private HardwareSensor? ReadCpuBuiltIn() =>
        SummarySensors.PickCpuTemp(_sensors.GetCpuSensors()) is { } cpu
            ? Synthetic("cpu", "CPU", cpu.Value, cpu.Stale, string.IsNullOrEmpty(cpu.Parent.Name) ? "CPU" : cpu.Parent.Name)
            : null;

    private HardwareSensor? ReadGpuBuiltIn()
    {
        HardwareSensor? hottest = null;
        var name = "";
        foreach (var gpu in _sensors.GetGpus())
        {
            if (gpu.Integrated)
            {
                continue;
            }
            var temp = gpu.Sensors.FirstOrDefault(x => string.Equals(x.Type, "Temperature", StringComparison.OrdinalIgnoreCase) && x.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                ?? gpu.Sensors.FirstOrDefault(x => string.Equals(x.Type, "Temperature", StringComparison.OrdinalIgnoreCase));
            if (temp is not null && IsUsable(temp) && (hottest is null || temp.Value > hottest.Value))
            {
                hottest = temp;
                name = gpu.Name;
            }
        }
        return hottest is null ? null : Synthetic("gpu", "GPU", hottest.Value, false, name);
    }

    private HardwareSensor? ReadStorageBuiltIn()
    {
        if (_drives?.Snapshot() is not { Supported: true } smart)
        {
            return null;
        }
        var ignored = _store.Load().Diagnostics.IgnoredComponents ?? new List<string>();
        Nexus.Service.Diagnostics.Storage.SmartDriveInfo? hottest = null;
        foreach (var drive in smart.Drives)
        {
            if (drive.TemperatureC is { } c && double.IsFinite(c) && !ignored.Contains(drive.Id, StringComparer.Ordinal)
                && (hottest is null || c > hottest.TemperatureC))
            {
                hottest = drive;
            }
        }
        return hottest?.TemperatureC is { } driveC ? Synthetic("storage", "Storage", (float)driveC, false, hottest.Name) : null;
    }

    private HardwareSensor? ReadRamBuiltIn()
    {
        HardwareSensor? hottest = null;
        foreach (var sensor in Nexus.Service.Monitoring.History.SystemMetricsSource.FindRamTempSensors(_sensors.GetMemorySensors()))
        {
            if (IsUsable(sensor) && (hottest is null || sensor.Value > hottest.Value))
            {
                hottest = sensor;
            }
        }
        return hottest is null ? null : Synthetic("ram", "Memory", hottest.Value, false, hottest.Name);
    }

    private static HardwareSensor Synthetic(string id, string name, float value, bool stale, string source) => new()
    {
        Id = id,
        Name = name,
        Type = "Temperature",
        Value = value,
        Units = "°C",
        Stale = stale,
        Parent = new SensorParent { Id = id, Name = source },
    };

    private void Warn(string key, string message, DateTimeOffset now)
    {
        if (_warnings.ShouldLog(key, now))
        {
            ServiceLog.Warn(message);
        }
    }

    private bool ApplyEffect(
        SensorAlertRule rule,
        SensorAlertRuntime st,
        SensorAlertEffect effect,
        DateTimeOffset now,
        bool fahrenheit,
        Snap snap,
        List<(DiagnosticsAlertNotice, bool, string)> notices)
    {
        if (effect == SensorAlertEffect.None)
        {
            return false;
        }
        if ((effect & SensorAlertEffect.Raised) != 0)
        {
            var threshold = (st.Direction == "below" ? rule.Low : rule.High) ?? 0;
            var entry = new SensorAlertHistoryEntry
            {
                RuleId = rule.Id,
                SensorName = rule.Sensor.Name,
                Direction = st.Direction ?? "above",
                StartedUtc = Iso(now),
                Extreme = st.Extreme ?? st.Value ?? 0,
                Threshold = threshold,
            };
            st.Episode = entry;
            _history.Add(entry);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveAt(0);
            }
        }
        if ((effect & SensorAlertEffect.Raised) != 0)
        {
            notices.Add((BuildNotice(rule, st, fahrenheit), rule.NotifyDesktop, "alert:" + rule.Id));
        }
        if ((effect & SensorAlertEffect.Cleared) != 0)
        {
            if (st.Episode is { } episode)
            {
                episode.EndedUtc = Iso(now);
            }
            st.Episode = null;
        }
        return (effect & (SensorAlertEffect.Raised | SensorAlertEffect.Cleared | SensorAlertEffect.Changed)) != 0;
    }

    private void DeliverNotice(DiagnosticsAlertNotice notice, string key)
    {
        try
        {
            NoticeSink?.Invoke(notice, key);
        }
        catch (Exception ex)
        {
            Warn("notify", $"[sensor-alerts] notify failed: {ex.Message}", Mono());
        }
    }

    private void HealSensorIds(List<(string RuleId, string SensorName, string SensorType, string NewId)> heals)
    {
        try
        {
            _store.Update(s =>
            {
                foreach (var (ruleId, name, type, newId) in heals)
                {
                    var rule = s.Diagnostics.SensorAlerts?.FirstOrDefault(r => r.Id == ruleId);
                    if (rule is not null && rule.Sensor.Name == name && rule.Sensor.Type == type)
                    {
                        rule.Sensor.Id = newId;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Warn("heal", $"[sensor-alerts] id self-heal failed: {ex.Message}", Mono());
        }
    }

    /// <summary>Applies rule edits and removals to the runtime state: an edited rule restarts from ok and a removed one is dropped. Call after the rule list changes.</summary>
    public void Reconcile()
    {
        ReconcileCore(Snapshot().Rules, Mono());
        DropStaleSeries();
        Changed?.Invoke();
    }

    private bool ReconcileCore(List<SensorAlertRule> rules, DateTimeOffset now)
    {
        var changed = false;
        var wall = _time.GetUtcNow();
        lock (_lock)
        {
            var live = new HashSet<string>(rules.Select(r => r.Id), StringComparer.Ordinal);
            foreach (var id in _states.Keys.Where(id => !live.Contains(id)).ToList())
            {
                EndEpisode(_states[id], wall);
                _states.Remove(id);
                changed = true;
            }
            foreach (var rule in rules)
            {
                if (!_states.TryGetValue(rule.Id, out var st))
                {
                    _states[rule.Id] = new SensorAlertRuntime { Config = rule.Clone() };
                    changed = true;
                    continue;
                }
                if (st.Config is { } cfg && cfg.SameConfig(rule))
                {
                    continue;
                }
                EndEpisode(st, wall);
                st.ResetToOk();
                st.Episode = null;
                st.InvalidSince = null;
                st.InvalidSinceUtc = null;
                st.Value = null;
                st.Reported = SensorAlertStates.Ok;
                st.Ended = null;
                st.Config = rule.Clone();
                changed = true;
            }
        }
        return changed;
    }

    // A deleted, disabled or re-pointed rule's reading must not be stamped onto a sample after the route dropped its history.
    private void DropStaleSeries()
    {
        var snapshot = Volatile.Read(ref _series);
        if (snapshot.Entries.Count == 0)
        {
            return;
        }
        var live = new List<Nexus.Service.Monitoring.History.SensorSeriesReading>();
        _store.Read(s => LiveReadings(snapshot.Entries, s.Diagnostics.SensorAlerts, live));
        if (live.Count == snapshot.Entries.Count)
        {
            return;
        }
        var keep = live.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var kept = new SeriesSnapshot(snapshot.At, snapshot.Entries.Where(e => keep.Contains(e.RuleId)).ToList()) ;
        Interlocked.CompareExchange(ref _series, kept, snapshot);
    }

    private static void EndEpisode(SensorAlertRuntime st, DateTimeOffset wall)
    {
        if (st.Phase == SensorAlertStates.Active && st.Episode is { EndedUtc: null } episode)
        {
            episode.EndedUtc = Iso(wall);
        }
    }

    /// <summary>Dismisses an active alert (its banner and health entry go until it raises again) or a finished one that is still waiting for the user.</summary>
    public bool Acknowledge(string ruleId)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(ruleId, out var st))
            {
                return false;
            }
            if (st.Phase == SensorAlertStates.Active && !st.Acknowledged)
            {
                st.Acknowledged = true;
            }
            else if (st.Ended is not null)
            {
                st.Ended = null;
            }
            else
            {
                return false;
            }
        }
        Changed?.Invoke();
        return true;
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            _history.RemoveAll(h => h.EndedUtc is not null);
        }
        Changed?.Invoke();
    }

    /// <summary>States of the enabled rules; a disabled rule has nothing to report.</summary>
    public List<SensorAlertState> States()
    {
        var enabled = Snapshot().Rules.Where(r => r.Enabled).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var now = Mono();
        lock (_lock)
        {
            return _states.Where(kv => enabled.Contains(kv.Key)).Select(kv => ToState(kv.Key, kv.Value, now)).ToList();
        }
    }

    /// <summary>Newest first.</summary>
    public List<SensorAlertHistoryEntry> History()
    {
        lock (_lock)
        {
            return _history.AsEnumerable().Reverse().Select(h => new SensorAlertHistoryEntry
            {
                RuleId = h.RuleId,
                SensorName = h.SensorName,
                Direction = h.Direction,
                StartedUtc = h.StartedUtc,
                EndedUtc = h.EndedUtc,
                Extreme = h.Extreme,
                Threshold = h.Threshold,
            }).ToList();
        }
    }

    private static SensorAlertState ToState(string id, SensorAlertRuntime st, DateTimeOffset now)
    {
        var reported = Reported(st, now);
        var since = reported switch
        {
            SensorAlertStates.Unavailable => st.InvalidSinceUtc,
            SensorAlertStates.Ok => null,
            _ => st.PhaseSinceUtc,
        };
        var inEpisode = reported is SensorAlertStates.Pending or SensorAlertStates.Active;
        return new SensorAlertState
        {
            RuleId = id,
            State = reported,
            Value = reported == SensorAlertStates.Unavailable ? null : st.Value,
            Since = since is { } s ? Iso(s) : null,
            Extreme = inEpisode ? st.Extreme : null,
            Direction = inEpisode ? st.Direction : null,
            Threshold = inEpisode && st.Config is { } cfg ? (st.Direction == "below" ? cfg.Low : cfg.High) : null,
            EndedAt = st.Ended is { } ended ? Iso(ended.EndedUtc) : null,
            EndedPeak = st.Ended?.Peak,
            EndedThreshold = st.Ended?.Threshold,
            EndedDirection = st.Ended?.Direction,
            Acknowledged = st.Acknowledged,
        };
    }

    /// <summary>
    /// One component per rule that opted into health and is either active and not acknowledged (the rule's severity) or
    /// finished and not yet dismissed (watch, like the thermal guard, until it ages out).
    /// </summary>
    internal List<HealthComponent> HealthComponents()
    {
        var snap = Snapshot();
        var (rules, fahrenheit) = snap;
        var wall = _time.GetUtcNow();
        var result = new List<HealthComponent>();
        lock (_lock)
        {
            foreach (var rule in rules)
            {
                if (!rule.Enabled || !_states.TryGetValue(rule.Id, out var st)
                    || st.Config is not { } cfg || !cfg.SameConfig(rule))
                {
                    continue;
                }
                if (st.Phase == SensorAlertStates.Active && !st.Acknowledged)
                {
                    var severity = rule.Severity == SensorAlertRules.Act ? HealthStatuses.Act : HealthStatuses.Watch;
                    var text = SensorAlertText.Describe(rule, st.Direction, st.Value ?? st.Extreme ?? 0, st.Units, fahrenheit, st.Source);
                    result.Add(Component(rule, severity, "alerts.sensorThreshold", text, $"ruleId={rule.Id} category={rule.Sensor.Category} direction={st.Direction}",
                        ThresholdParams(rule, st)));
                }
                else if (st.Ended is { } ended && wall - ended.EndedUtc < SensorAlertRules.EndedLinger)
                {
                    var text = SensorAlertText.DescribeEnded(rule, ended, fahrenheit, st.Source, st.Units);
                    result.Add(Component(rule, HealthStatuses.Watch, "alerts.sensorThresholdEnded", text, $"ruleId={rule.Id} category={rule.Sensor.Category} direction={ended.Direction}",
                        EndedParams(rule, ended, st.Units, st.Source)));
                }
            }
        }
        return result;
    }

    private static HealthComponent Component(SensorAlertRule rule, string severity, string code, string text, string detail, Dictionary<string, string> p) => new()
    {
        Id = $"alert:{rule.Id}",
        Kind = "alerts",
        Name = SensorAlertText.DisplayName(rule),
        Status = severity,
        Reasons = new List<HealthComponentReason> { new(code, severity, text, detail, p) },
    };

    // Values are native units (temperature in Celsius); the UI converts for display.
    private static Dictionary<string, string> ThresholdParams(SensorAlertRule rule, SensorAlertRuntime st)
    {
        var p = BaseParams(rule, st.Source, st.Units);
        p["direction"] = st.Direction ?? "";
        p["value"] = (st.Value ?? st.Extreme ?? 0).ToString(CultureInfo.InvariantCulture);
        p["active"] = "true";
        if (st.Extreme is { } peak)
        {
            p["peak"] = peak.ToString(CultureInfo.InvariantCulture);
        }
        if (st.PhaseSinceUtc is { } since)
        {
            p["since"] = since.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        }
        if ((st.Direction == "below" ? rule.Low : rule.High) is { } threshold)
        {
            p["threshold"] = threshold.ToString(CultureInfo.InvariantCulture);
        }
        return p;
    }

    private static Dictionary<string, string> EndedParams(SensorAlertRule rule, EndedAlert ended, string units, string source)
    {
        var p = BaseParams(rule, source, units);
        p["direction"] = ended.Direction;
        p["active"] = "false";
        p["peak"] = ended.Peak.ToString(CultureInfo.InvariantCulture);
        p["threshold"] = ended.Threshold.ToString(CultureInfo.InvariantCulture);
        p["ended"] = ended.EndedUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        return p;
    }

    private static Dictionary<string, string> BaseParams(SensorAlertRule rule, string source, string units)
    {
        var p = new Dictionary<string, string>
        {
            ["ruleId"] = rule.Id,
            ["category"] = rule.Sensor.Category,
            ["type"] = rule.Sensor.Type,
            ["sensorName"] = rule.Sensor.Name,
            ["units"] = units,
        };
        if (rule.BuiltIn is not null)
        {
            p["builtIn"] = rule.BuiltIn;
        }
        if (source.Length > 0)
        {
            p["sourceName"] = source;
        }
        return p;
    }

    private static DiagnosticsAlertNotice BuildNotice(SensorAlertRule rule, SensorAlertRuntime st, bool fahrenheit) =>
        new(
            "Sensor alert",
            SensorAlertText.Describe(rule, st.Direction, st.Value ?? st.Extreme ?? 0, st.Units, fahrenheit, st.Source),
            NoticeKind,
            Urgent: rule.Severity == SensorAlertRules.Act);

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>Allows one log line per key per interval, so a persistent failure does not log on every tick.</summary>
internal sealed class LogThrottle
{
    private readonly TimeSpan _interval;
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);

    public LogThrottle(TimeSpan interval) => _interval = interval;

    public bool ShouldLog(string key, DateTimeOffset now)
    {
        lock (_last)
        {
            if (_last.TryGetValue(key, out var last) && now - last < _interval)
            {
                return false;
            }
            _last[key] = now;
            return true;
        }
    }
}

/// <summary>Toast and health wording, in the user's temperature unit.</summary>
internal static class SensorAlertText
{
    /// <summary>The sensor's name with its type appended unless the name already says it.</summary>
    internal static string DisplayName(SensorAlertRule rule) => rule.BuiltIn switch
    {
        BuiltInSensorRules.CpuTemp => "CPU temperature",
        BuiltInSensorRules.GpuTemp => "GPU temperature",
        BuiltInSensorRules.StorageTemp => "Storage temperature",
        BuiltInSensorRules.RamTemp => "Memory temperature",
        _ => DisplayName(rule.Sensor),
    };

    internal static string DisplayName(SensorRef sensor) =>
        sensor.Name.EndsWith(sensor.Type, StringComparison.OrdinalIgnoreCase) || sensor.Type.Length == 0
            ? sensor.Name
            : $"{sensor.Name} {sensor.Type}";

    /// <summary>The toast sentence: name, direction, limit, hold time and the current reading.</summary>
    internal static string Describe(SensorAlertRule rule, string? direction, double value, string units, bool fahrenheit, string source = "")
    {
        var below = direction == "below";
        var threshold = (below ? rule.Low : rule.High) ?? 0;
        var delay = rule.RaiseAfterSeconds > 0 ? $" for {rule.RaiseAfterSeconds} s" : "";
        return $"{WithSource(rule, source)} {(below ? "below" : "above")} {Format(threshold, rule.Sensor.Type, units, fahrenheit)}{delay} (now {Format(value, rule.Sensor.Type, units, fahrenheit)})";
    }

    /// <summary>The sentence for a finished alert that still waits for the user.</summary>
    internal static string DescribeEnded(SensorAlertRule rule, EndedAlert ended, bool fahrenheit, string source, string units)
    {
        var type = rule.Sensor.Type;
        var below = ended.Direction == "below";
        return $"{WithSource(rule, source)} was {(below ? "below" : "above")} {Format(ended.Threshold, type, units, fahrenheit)} (peak {Format(ended.Peak, type, units, fahrenheit)}), back to normal";
    }

    // A built-in has no name of its own to say which GPU or drive it follows.
    private static string WithSource(SensorAlertRule rule, string source) =>
        rule.BuiltIn is BuiltInSensorRules.GpuTemp or BuiltInSensorRules.StorageTemp && source.Length > 0
            ? $"{DisplayName(rule)} ({source})"
            : DisplayName(rule);

    internal static string Format(double value, string type, string units, bool fahrenheit)
    {
        if (string.Equals(type, "Temperature", StringComparison.OrdinalIgnoreCase))
        {
            var shown = fahrenheit ? value * 9 / 5 + 32 : value;
            return shown.ToString("0.#", CultureInfo.InvariantCulture) + (fahrenheit ? " °F" : " °C");
        }
        var number = value.ToString("0.##", CultureInfo.InvariantCulture);
        if (units.Length == 0)
        {
            return number;
        }
        return units == "%" ? number + "%" : $"{number} {units}";
    }
}
