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
    private readonly Nexus.Service.Cooling.IFanControlProvider? _fans;
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
    /// Empty once that tick is older than the max age, and for a rule that was deleted or
    /// re-pointed since the tick. A disabled rule still records: the switch only controls alerting.
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
        var current = (rules ?? Enumerable.Empty<SensorAlertRule>()).ToDictionary(r => r.Id, StringComparer.Ordinal);
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

    public SensorAlertEngine(IConfigStore store, ISensorProvider sensors, Nexus.Service.Diagnostics.Storage.ISmartHealthSource? drives = null, Nexus.Service.Cooling.IFanControlProvider? fans = null)
        : this(store, sensors, TimeProvider.System, null, drives, fans)
    {
    }

    internal SensorAlertEngine(
        IConfigStore store, ISensorProvider sensors, TimeProvider time, TimeSpan? startupHold = null,
        Nexus.Service.Diagnostics.Storage.ISmartHealthSource? drives = null,
        Nexus.Service.Cooling.IFanControlProvider? fans = null)
    {
        _store = store;
        _sensors = sensors;
        _cpuMax = new CpuMaxCache(
            () => _sensors.GetCpuModel(),
            () => _sensors.GetCpuTjMaxC(),
            ex => Warn("builtin-cpumax", $"[sensor-alerts] CPU maximum lookup failed: {ex.Message}", Mono()));
        _drives = drives;
        _fans = fans;
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

    /// <summary>Keeps the reading of a disabled rule current without any alert logic. Returns whether the reported state changed.</summary>
    private static bool StepDisabled(SensorSample sample, DateTimeOffset now, DateTimeOffset wall, SensorAlertRuntime st)
    {
        if (sample.Valid)
        {
            st.InvalidSince = null;
            st.InvalidSinceUtc = null;
            st.Value = sample.Value;
            st.Units = sample.Units;
        }
        else
        {
            st.InvalidSince ??= now;
            st.InvalidSinceUtc ??= wall;
            st.Value = null;
        }
        var reported = DisabledReported(st, now);
        if (reported == st.Reported)
        {
            return false;
        }
        st.Reported = reported;
        return true;
    }

    private static string DisabledReported(SensorAlertRuntime st, DateTimeOffset now) =>
        st.InvalidSince is { } since && now - since >= UnavailableAfter ? SensorAlertStates.Unavailable : SensorAlertStates.Off;

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
        var builtIns = BuiltIns().Devices;
        // Also here, not only on a tick: a settings reset removes the stored rules while the detected devices stay.
        SeedBuiltIns(builtIns);
        var devices = builtIns.ToDictionary(d => BuiltInSensorRules.RuleId(d.SeriesId), StringComparer.Ordinal);
        foreach (var (id, detected) in devices)
        {
            _knownDevices[id] = detected;
        }
        var rules = new List<SensorAlertRule>();
        var builtInRules = new List<SensorAlertRule>();
        var fahrenheit = false;
        _store.Read(s =>
        {
            var ignored = s.Diagnostics.IgnoredComponents;
            if (s.Diagnostics.SensorAlerts is { } list)
            {
                foreach (var rule in list)
                {
                    if (rule.BuiltIn is null)
                    {
                        rules.Add(rule.Clone());
                    }
                    // A device missing from one pass (a failed read, a dropped sensor) keeps its entry as last seen, so its state is not torn down.
                    else if (devices.TryGetValue(rule.Id, out var device) || _knownDevices.TryGetValue(rule.Id, out device))
                    {
                        var copy = rule.Clone();
                        copy.Sensor.Name = device.Name;
                        copy.Source = device.Source;
                        // A fan's on/off is the Cooling page's stall-monitoring toggle, so the ignore list decides, not the stored flag.
                        if (device.Kind == BuiltInSensorRules.FanSpeed)
                        {
                            copy.Enabled = ignored?.Contains(device.ComponentId, StringComparer.Ordinal) != true;
                        }
                        builtInRules.Add(copy);
                    }
                }
            }
            fahrenheit = string.Equals(s.Units.MonitoringTempUnit, "f", StringComparison.OrdinalIgnoreCase);
        });
        // Built-ins first, in the Cooling chart's order.
        builtInRules.Sort((a, b) =>
        {
            var byKind = BuiltInSensorRules.KindRank(BuiltInSensorRules.SeriesId(a.Id)) - BuiltInSensorRules.KindRank(BuiltInSensorRules.SeriesId(b.Id));
            return byKind != 0 ? byKind : string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);
        });
        builtInRules.AddRange(rules);
        return new Snap(builtInRules, fahrenheit);
    }

    /// <summary>The configured rules, as copies: the built-ins of every device seen since startup (one missing now reads unavailable), then the user's.</summary>
    public List<SensorAlertRule> Rules() => Snapshot().Rules;

    /// <summary>The devices that have a built-in entry: those detected now plus any seen since startup that this pass missed.</summary>
    public IReadOnlyList<BuiltInDevice> BuiltInDevices()
    {
        var detected = BuiltIns().Devices;
        var seen = detected.Select(d => BuiltInSensorRules.RuleId(d.SeriesId)).ToHashSet(StringComparer.Ordinal);
        var absent = _knownDevices.Where(kv => !seen.Contains(kv.Key)).Select(kv => kv.Value).ToList();
        return absent.Count == 0 ? detected : detected.Concat(absent).ToList();
    }

    /// <summary>Refreshes the detected devices, then steps every rule once.</summary>
    public void Tick()
    {
        RefreshBuiltIns();
        var snap = Snapshot();
        var (rules, fahrenheit) = snap;
        var now = Mono();
        var changed = ReconcileCore(rules, now);

        var enabled = rules;
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
                    // A built-in's series is the monitoring history's own, so it is not recorded twice.
                    if (rule.BuiltIn is null && sample.Valid && double.IsFinite(sample.Value))
                    {
                        // A healed id is the same sensor, so the entry carries it and compares equal to the rule once the heal lands.
                        readings.Add(new SeriesEntry(rule.Id, sample.Value, healed is null ? rule.Sensor : WithId(rule.Sensor, healed)));
                    }
                    if (sample.Valid)
                    {
                        st.Source = sample.Source;
                    }
                    // The switch controls alerting only: a disabled rule is read and recorded but never stepped.
                    if (!rule.Enabled)
                    {
                        changed |= StepDisabled(sample, now, wall, st);
                        continue;
                    }
                    var effect = Step(rule, sample, now, st, wall);
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
            return BuiltIns().Sensors;
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

    private sealed record BuiltInSnapshot(IReadOnlyList<BuiltInDevice> Devices, IReadOnlyList<HardwareSensor> Sensors);

    private BuiltInSnapshot? _builtIns;

    // Every device detected since the process started, by rule id; values are replaced whole, never mutated.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BuiltInDevice> _knownDevices = new(StringComparer.Ordinal);

    private BuiltInSnapshot BuiltIns() => Volatile.Read(ref _builtIns) ?? RefreshBuiltIns();

    /// <summary>
    /// Detects the devices the Cooling temperature history plots, each read the way the sampler reads it: the CPU package
    /// (else the first CPU temperature), each GPU's core temperature, each drive with a SMART temperature (the cache, so no
    /// drive wakes) and the mean of the memory-module sensors. Each source reads on its own, so one failure leaves the others
    /// live.
    /// </summary>
    private BuiltInSnapshot RefreshBuiltIns()
    {
        var empty = new BuiltInSnapshot(Array.Empty<BuiltInDevice>(), Array.Empty<HardwareSensor>());
        var migrated = false;
        _store.Read(s => migrated = s.Diagnostics.BuiltInSensorsVersion >= BuiltInSensorRules.PerDeviceVersion);
        // Settings that never went through BuiltInSensorRules.Ensure have no legacy templates yet, so nothing is detected or read.
        if (!migrated)
        {
            return empty;
        }
        var devices = new List<BuiltInDevice>();
        var sensors = new List<HardwareSensor>();
        var now = Mono();
        // One value per pass, shared by the CPU and an integrated GPU.
        var cpuMax = _cpuMax.Get()?.MaxC;
        TryDiscover(devices, sensors, "cpu", now, (d, s) => DiscoverCpu(d, s, cpuMax));
        TryDiscover(devices, sensors, "gpu", now, (d, s) => DiscoverGpus(d, s, cpuMax));
        TryDiscover(devices, sensors, "memory", now, DiscoverMemory);
        TryDiscover(devices, sensors, "storage", now, DiscoverDrives);
        TryDiscover(devices, sensors, "fans", now, DiscoverFans);
        ApplyPendingIgnores(devices);
        var snapshot = new BuiltInSnapshot(devices, sensors);
        Volatile.Write(ref _builtIns, snapshot);
        return snapshot;
    }

    /// <summary>The window after the startup hold in which late-enumerating hubs and slow drives still get the retired "off" choice.</summary>
    private static readonly TimeSpan PendingSettle = TimeSpan.FromMinutes(5);

    private readonly HashSet<string> _pendingApplied = new(StringComparer.Ordinal);

    /// <summary>
    /// Applies the retired cooling and storage health toggles to each fan and drive the first time it appears. No hub or drive
    /// source has a ready signal (the sensor provider's only covers its own walk), so the flags clear once a bounded window
    /// after the startup hold has passed, not on the first enumeration.
    /// </summary>
    private void ApplyPendingIgnores(IReadOnlyList<BuiltInDevice> devices)
    {
        var fans = false;
        var drives = false;
        _store.Read(s => (fans, drives) = (s.Diagnostics.PendingFansOff, s.Diagnostics.PendingDrivesOff));
        if (!fans && !drives)
        {
            return;
        }
        var settled = _time.GetElapsedTime(_stamp0) >= PendingSettle + (_startupHold ?? Nexus.Service.Lifecycle.StartupDelayGate.Configured);
        var fresh = new List<string>();
        lock (_pendingApplied)
        {
            if (fans)
            {
                foreach (var d in devices.Where(d => d.Kind == BuiltInSensorRules.FanSpeed))
                {
                    if (_pendingApplied.Add(d.ComponentId))
                    {
                        fresh.Add(d.ComponentId);
                    }
                }
            }
            if (drives && _drives?.Snapshot() is { Supported: true } smart)
            {
                foreach (var drive in smart.Drives)
                {
                    if (_pendingApplied.Add(drive.Id))
                    {
                        fresh.Add(drive.Id);
                    }
                }
            }
        }
        if (fresh.Count == 0 && !settled)
        {
            return;
        }
        try
        {
            _store.Update(s =>
            {
                var ignored = s.Diagnostics.IgnoredComponents ??= new List<string>();
                foreach (var id in fresh.Where(id => !ignored.Contains(id, StringComparer.Ordinal)))
                {
                    ignored.Add(id);
                }
                if (settled)
                {
                    s.Diagnostics.PendingFansOff = false;
                    s.Diagnostics.PendingDrivesOff = false;
                }
            });
        }
        catch (Exception ex)
        {
            lock (_pendingApplied)
            {
                _pendingApplied.ExceptWith(fresh);
            }
            Warn("health-migration", $"[sensor-alerts] applying the retired health toggles failed: {ex.Message}", Mono());
        }
    }

    private void SeedBuiltIns(IReadOnlyList<BuiltInDevice> devices)
    {
        var missing = false;
        _store.Read(s =>
        {
            var have = new Dictionary<string, SensorAlertRule>(StringComparer.Ordinal);
            foreach (var r in s.Diagnostics.SensorAlerts ?? new List<SensorAlertRule>())
            {
                have.TryAdd(r.Id, r);
            }
            missing = devices.Any(d => !have.TryGetValue(BuiltInSensorRules.RuleId(d.SeriesId), out var r) || BuiltInSensorRules.CpuDefaultStale(r, d));
        });
        if (!missing)
        {
            return;
        }
        try
        {
            _store.Update(s =>
            {
                var list = s.Diagnostics.SensorAlerts ??= new List<SensorAlertRule>();
                foreach (var device in devices)
                {
                    var existing = list.FirstOrDefault(r => r.Id == BuiltInSensorRules.RuleId(device.SeriesId));
                    if (existing is null)
                    {
                        list.Add(BuiltInSensorRules.Seed(device, s.Diagnostics));
                    }
                    else if (BuiltInSensorRules.CpuDefaultStale(existing, device))
                    {
                        BuiltInSensorRules.RefreshCpuDefault(existing, device);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Warn("builtin-seed", $"[sensor-alerts] built-in seeding failed: {ex.Message}", Mono());
        }
    }

    private void TryDiscover(List<BuiltInDevice> devices, List<HardwareSensor> sensors, string key, DateTimeOffset now, Action<List<BuiltInDevice>, List<HardwareSensor>> discover)
    {
        try
        {
            discover(devices, sensors);
        }
        catch (Exception ex)
        {
            Warn("builtin-" + key, $"[sensor-alerts] built-in {key} read failed: {ex.Message}", now);
        }
    }

    private void DiscoverCpu(List<BuiltInDevice> devices, List<HardwareSensor> sensors, double? cpuMaxC)
    {
        var temp = Nexus.Service.Monitoring.History.SystemMetricsSource.FindSensor(_sensors.GetCpuSensors(), "Temperature", "Package");
        if (temp is null)
        {
            return;
        }
        var name = Nexus.Service.Routes.MonitoringHistoryRoutes.ResolveCpuTempName(_sensors.GetCpuModel());
        Add(devices, sensors, new BuiltInDevice(BuiltInSensorRules.CpuTemp, BuiltInSensorRules.CpuSeriesId, name, false, "", SingleSource("cpu", temp, name), cpuMaxC), temp.Value, temp.Stale);
    }

    private readonly CpuMaxCache _cpuMax;

    private void DiscoverGpus(List<BuiltInDevice> devices, List<HardwareSensor> sensors, double? cpuMaxC)
    {
        foreach (var gpu in _sensors.GetGpus())
        {
            var temp = Nexus.Service.Monitoring.History.SystemMetricsSource.FindSensor(gpu.Sensors, "Temperature", "Core");
            if (temp is null)
            {
                continue;
            }
            var seriesId = BuiltInSensorRules.GpuSeriesPrefix + Nexus.Service.Monitoring.History.MetricsHistory.SanitizeId(gpu.Id);
            Add(devices, sensors, new BuiltInDevice(BuiltInSensorRules.GpuTemp, seriesId, gpu.Name, gpu.Integrated, gpu.Id, SingleSource("gpu", temp, gpu.Name), gpu.Integrated ? cpuMaxC : null), temp.Value, temp.Stale);
        }
    }

    private void DiscoverMemory(List<BuiltInDevice> devices, List<HardwareSensor> sensors)
    {
        var usable = Nexus.Service.Monitoring.History.SystemMetricsSource.FindRamTempSensors(_sensors.GetMemorySensors());
        if (usable.Count == 0)
        {
            return;
        }
        var fresh = usable.Where(IsUsable).ToList();
        var value = fresh.Count == 0 ? float.NaN : fresh.Average(x => x.Value);
        // One contributing sensor is that sensor; several are a mean no single sensor id describes.
        var source = fresh.Count == 1
            ? SingleSource("memory", fresh[0], "Memory")
            : new BuiltInSource { Kind = "average", Sensors = fresh.Select(x => Ref("memory", x)).ToList(), HardwareName = "Memory" };
        Add(devices, sensors, new BuiltInDevice(BuiltInSensorRules.RamTemp, BuiltInSensorRules.MemorySeriesId, "Memory Temperature", false, "", source), value, fresh.Count == 0);
    }

    private void DiscoverDrives(List<BuiltInDevice> devices, List<HardwareSensor> sensors)
    {
        if (_drives?.Snapshot() is not { Supported: true } smart)
        {
            return;
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var drive in smart.Drives.Where(d => d.TemperatureC is not null).OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            var seriesId = BuiltInSensorRules.DriveSeriesPrefix + Nexus.Service.Routes.MonitoringHistoryRoutes.ResolveUniqueSanitizedId(drive.Id, used);
            // The SMART health poll, not an LHM sensor of the picker's smart category, so there is no picker id to give.
            var source = new BuiltInSource { Kind = "drive", Sensor = null, HardwareName = drive.Name };
            Add(devices, sensors, new BuiltInDevice(BuiltInSensorRules.StorageTemp, seriesId, drive.Name, false, drive.Id, source), (float)drive.TemperatureC!.Value, false);
        }
    }

    /// <summary>One entry per fan or pump channel, keyed like the monitoring history's fan RPM series ("fan:" plus the sanitized channel id).</summary>
    private void DiscoverFans(List<BuiltInDevice> devices, List<HardwareSensor> sensors)
    {
        if (_fans is null)
        {
            return;
        }
        // Read once per refresh, and only when a channel has a tach sensor to look up.
        IReadOnlyList<HardwareSensor>? gpuTachs = null;
        IReadOnlyList<HardwareSensor>? boardTachs = null;
        foreach (var ch in _fans.GetFanChannels())
        {
            var seriesId = BuiltInSensorRules.FanSeriesPrefix + Nexus.Service.Monitoring.History.MetricsHistory.SanitizeId(ch.Id);
            if (devices.Any(d => d.SeriesId == seriesId))
            {
                continue;
            }
            BuiltInSource source;
            var hardware = ch.DeviceName ?? ch.Name;
            // The tach sensor behind a motherboard or graphics card channel when the picker lists it; a hub channel has none.
            HardwareSensor? tach = null;
            if (!string.IsNullOrEmpty(ch.RpmSensorId))
            {
                var list = ch.IsGpu
                    ? gpuTachs ??= _sensors.GetGpus().SelectMany(g => g.Sensors).ToList()
                    : boardTachs ??= _sensors.GetMotherboardSensorsCached();
                tach = list.FirstOrDefault(x => x.Id == ch.RpmSensorId);
            }
            source = tach is null
                ? new BuiltInSource { Kind = "channel", Sensor = null, HardwareName = hardware }
                : new BuiltInSource { Kind = "sensor", Sensor = Ref(ch.IsGpu ? "gpu" : "motherboard", tach), HardwareName = hardware };
            var device = new BuiltInDevice(BuiltInSensorRules.FanSpeed, seriesId, ch.Name, false, BuiltInSensorRules.FanComponentPrefix + ch.Id, source);
            // A channel with no readable tach has no value, as in the history.
            Add(devices, sensors, device, ch.RpmUnavailable ? float.NaN : ch.Rpm, ch.RpmUnavailable);
        }
    }

    private static SensorRef Ref(string category, HardwareSensor s) => new()
    {
        Category = category,
        Id = s.Id,
        Name = s.Name,
        Type = s.Type,
        ParentName = s.Parent.Name,
    };

    private static BuiltInSource SingleSource(string category, HardwareSensor s, string hardwareName) => new()
    {
        Kind = "sensor",
        Sensor = Ref(category, s),
        HardwareName = hardwareName,
    };

    private static void Add(List<BuiltInDevice> devices, List<HardwareSensor> sensors, BuiltInDevice device, float value, bool stale)
    {
        devices.Add(device);
        sensors.Add(new HardwareSensor
        {
            Id = device.SeriesId,
            Name = device.Name,
            Type = BuiltInSensorRules.SensorType(device.Kind),
            Value = value,
            Units = BuiltInSensorRules.Units(device.Kind),
            Stale = stale,
            Parent = new SensorParent { Id = device.SeriesId, Name = device.Name },
        });
    }

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

    /// <summary>States of every rule. A disabled rule reports its reading with the off state, or unavailable with no value.</summary>
    public List<SensorAlertState> States()
    {
        var enabledById = Snapshot().Rules.ToDictionary(r => r.Id, r => r.Enabled, StringComparer.Ordinal);
        var now = Mono();
        lock (_lock)
        {
            return _states.Where(kv => enabledById.ContainsKey(kv.Key)).Select(kv => ToState(kv.Key, kv.Value, now, enabledById[kv.Key])).ToList();
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

    private static SensorAlertState ToState(string id, SensorAlertRuntime st, DateTimeOffset now, bool enabled)
    {
        var reported = enabled ? Reported(st, now) : DisabledReported(st, now);
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
        if (source.Length > 0 && rule.BuiltIn is null)
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
            Urgent: rule.Severity == SensorAlertRules.Act,
            Severity: rule.Severity);

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
    internal static string DisplayName(SensorAlertRule rule) => rule.BuiltIn is not null ? rule.Sensor.Name : DisplayName(rule.Sensor);

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
        return $"{DisplayName(rule)} {(below ? "below" : "above")} {Format(threshold, rule.Sensor.Type, units, fahrenheit)}{delay} (now {Format(value, rule.Sensor.Type, units, fahrenheit)})";
    }

    /// <summary>The sentence for a finished alert that still waits for the user.</summary>
    internal static string DescribeEnded(SensorAlertRule rule, EndedAlert ended, bool fahrenheit, string source, string units)
    {
        var type = rule.Sensor.Type;
        var below = ended.Direction == "below";
        return $"{DisplayName(rule)} was {(below ? "below" : "above")} {Format(ended.Threshold, type, units, fahrenheit)} (peak {Format(ended.Peak, type, units, fahrenheit)}), back to normal";
    }

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
