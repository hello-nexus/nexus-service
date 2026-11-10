using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Diagnostics.Alerts;

/// <summary>One resolved reading. Valid is false for a missing, stale, non-finite or implausible sensor.</summary>
internal readonly record struct SensorSample(bool Valid, double Value, string Units);

[Flags]
internal enum SensorAlertEffect
{
    None = 0,
    Raised = 1,
    Cleared = 2,
    Repeated = 4,
    /// <summary>The reported state changed; subscribers should refetch.</summary>
    Changed = 8,
}

/// <summary>Mutable per-rule state advanced by <see cref="SensorAlertEngine.Step"/>.</summary>
internal sealed class SensorAlertRuntime
{
    public string Phase = SensorAlertStates.Ok;
    public DateTimeOffset? PhaseSince;
    public double? Value;
    public double? Extreme;
    public string? Direction;
    public bool Acknowledged;
    public DateTimeOffset? InvalidSince;
    public DateTimeOffset? ClearSince;
    public DateTimeOffset LastNotified;
    public string Units = "";
    public string Reported = SensorAlertStates.Ok;
    public SensorAlertRule? Config;
    public SensorAlertHistoryEntry? Episode;

    public void ResetToOk()
    {
        Phase = SensorAlertStates.Ok;
        PhaseSince = null;
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
public sealed class SensorAlertEngine
{
    internal static readonly TimeSpan UnavailableAfter = TimeSpan.FromSeconds(10);
    private const int MaxHistory = 100;
    private const string NoticeKind = "sensorAlert";

    private readonly IConfigStore _store;
    private readonly ISensorProvider _sensors;
    private readonly TimeProvider _time;
    private readonly long _stamp0;
    private readonly DateTimeOffset _wall0;
    private readonly object _lock = new();
    private readonly Dictionary<string, SensorAlertRuntime> _states = new(StringComparer.Ordinal);
    private readonly List<SensorAlertHistoryEntry> _history = new();

    /// <summary>Delivers a notice; the second argument is ignoreMaster.</summary>
    public Action<DiagnosticsAlertNotice, bool>? NoticeSink { get; set; }

    /// <summary>Raised after a transition or a rule change, never for a plain value update.</summary>
    public event Action? Changed;

    public SensorAlertEngine(IConfigStore store, ISensorProvider sensors) : this(store, sensors, TimeProvider.System)
    {
    }

    internal SensorAlertEngine(IConfigStore store, ISensorProvider sensors, TimeProvider time)
    {
        _store = store;
        _sensors = sensors;
        _time = time;
        _stamp0 = time.GetTimestamp();
        _wall0 = time.GetUtcNow();
    }

    // Monotonic axis anchored to the wall clock, so a clock jump never shortens or extends a delay.
    private DateTimeOffset Mono() => _wall0 + _time.GetElapsedTime(_stamp0);

    // ---- pure step ----

    /// <summary>Advances <paramref name="st"/> by one sample and reports what happened.</summary>
    internal static SensorAlertEffect Step(SensorAlertRule rule, SensorSample sample, DateTimeOffset now, SensorAlertRuntime st)
    {
        var effect = SensorAlertEffect.None;
        if (!sample.Valid)
        {
            st.InvalidSince ??= now;
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
        st.Value = sample.Value;
        st.Units = sample.Units;
        var v = sample.Value;
        var direction = Breach(rule, v);

        if (st.Phase == SensorAlertStates.Ok && direction is not null)
        {
            st.Phase = SensorAlertStates.Pending;
            st.PhaseSince = now;
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
            st.Extreme = Extend(st.Direction, st.Extreme, v);
            if (st.Episode is not null && st.Extreme is { } ex)
            {
                st.Episode.Extreme = ex;
            }
            effect |= StepActive(rule, v, now, st);
            return Finish(st, now, effect);
        }

        if (st.Phase == SensorAlertStates.Pending && st.PhaseSince is { } since
            && now - since >= TimeSpan.FromSeconds(rule.RaiseAfterSeconds))
        {
            st.Phase = SensorAlertStates.Active;
            st.PhaseSince = now;
            st.LastNotified = now;
            st.ClearSince = null;
            st.Acknowledged = false;
            effect |= SensorAlertEffect.Raised;
        }
        return Finish(st, now, effect);
    }

    private static SensorAlertEffect StepActive(SensorAlertRule rule, double v, DateTimeOffset now, SensorAlertRuntime st)
    {
        var hyst = SensorAlertRules.Hysteresis(rule);
        var inside = (rule.High is not { } hi || v <= hi - hyst) && (rule.Low is not { } lo || v >= lo + hyst);
        if (inside)
        {
            st.ClearSince ??= now;
            if (now - st.ClearSince.Value >= TimeSpan.FromSeconds(rule.ClearAfterSeconds))
            {
                st.ResetToOk();
                return SensorAlertEffect.Cleared;
            }
            return SensorAlertEffect.None;
        }

        st.ClearSince = null;
        if (rule.RepeatMinutes > 0 && now - st.LastNotified >= TimeSpan.FromMinutes(rule.RepeatMinutes))
        {
            st.LastNotified = now;
            return SensorAlertEffect.Repeated;
        }
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

    // ---- sensor resolution ----

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
        return new SensorSample(IsUsable(found), found.Value, found.Units);
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

    // ---- tick ----

    /// <summary>Evaluates every enabled rule once. Does nothing, and reads no sensor, without one.</summary>
    public void Tick()
    {
        var settings = _store.Load();
        var rules = settings.Diagnostics.SensorAlerts ?? new List<SensorAlertRule>();
        var now = Mono();
        var changed = ReconcileCore(rules, now);

        var enabled = rules.Where(r => r.Enabled).ToList();
        var notices = new List<(DiagnosticsAlertNotice Notice, bool Notify)>();
        var heals = new List<(string RuleId, string SensorName, string SensorType, string NewId)>();
        if (enabled.Count > 0)
        {
            var lists = ReadCategories(enabled);
            lock (_lock)
            {
                foreach (var rule in enabled)
                {
                    if (!_states.TryGetValue(rule.Id, out var st))
                    {
                        continue;
                    }
                    var list = lists.TryGetValue(rule.Sensor.Category, out var l) ? l : Array.Empty<HardwareSensor>();
                    var sample = Resolve(list, rule.Sensor, out var healed);
                    if (healed is not null)
                    {
                        heals.Add((rule.Id, rule.Sensor.Name, rule.Sensor.Type, healed));
                    }
                    var effect = Step(rule, sample, now, st);
                    changed |= ApplyEffect(rule, st, effect, now, settings.Units.MonitoringTempUnit == "f", notices);
                }
            }
        }

        if (heals.Count > 0)
        {
            HealSensorIds(heals);
        }
        foreach (var (notice, notify) in notices)
        {
            if (notify)
            {
                DeliverNotice(notice);
            }
        }
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private Dictionary<string, IReadOnlyList<HardwareSensor>> ReadCategories(List<SensorAlertRule> enabled)
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
                ServiceLog.Warn($"[sensor-alerts] extras read failed: {ex.Message}");
            }
        }
        var lists = new Dictionary<string, IReadOnlyList<HardwareSensor>>(StringComparer.Ordinal);
        foreach (var category in categories)
        {
            try
            {
                lists[category] = SensorSnapshotResolver.GetCategorySensors(_sensors, category, sources);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[sensor-alerts] {category} read failed: {ex.Message}");
                lists[category] = Array.Empty<HardwareSensor>();
            }
        }
        return lists;
    }

    private bool ApplyEffect(
        SensorAlertRule rule,
        SensorAlertRuntime st,
        SensorAlertEffect effect,
        DateTimeOffset now,
        bool fahrenheit,
        List<(DiagnosticsAlertNotice, bool)> notices)
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
        if ((effect & (SensorAlertEffect.Raised | SensorAlertEffect.Repeated)) != 0)
        {
            notices.Add((BuildNotice(rule, st, fahrenheit), rule.NotifyDesktop));
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

    private void DeliverNotice(DiagnosticsAlertNotice notice)
    {
        try
        {
            NoticeSink?.Invoke(notice, true);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[sensor-alerts] notify failed: {ex.Message}");
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
            ServiceLog.Warn($"[sensor-alerts] id self-heal failed: {ex.Message}");
        }
    }

    // ---- reconcile ----

    /// <summary>Applies rule edits and removals to the runtime state: an edited rule restarts from ok and a removed one is dropped. Call after the rule list changes.</summary>
    public void Reconcile()
    {
        var rules = _store.Load().Diagnostics.SensorAlerts ?? new List<SensorAlertRule>();
        ReconcileCore(rules, Mono());
        Changed?.Invoke();
    }

    private bool ReconcileCore(List<SensorAlertRule> rules, DateTimeOffset now)
    {
        var changed = false;
        lock (_lock)
        {
            var live = new HashSet<string>(rules.Select(r => r.Id), StringComparer.Ordinal);
            foreach (var id in _states.Keys.Where(id => !live.Contains(id)).ToList())
            {
                EndEpisode(_states[id], now);
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
                EndEpisode(st, now);
                st.ResetToOk();
                st.Episode = null;
                st.InvalidSince = null;
                st.Value = null;
                st.Reported = SensorAlertStates.Ok;
                st.Config = rule.Clone();
                changed = true;
            }
        }
        return changed;
    }

    private static void EndEpisode(SensorAlertRuntime st, DateTimeOffset now)
    {
        if (st.Phase == SensorAlertStates.Active && st.Episode is { EndedUtc: null } episode)
        {
            episode.EndedUtc = Iso(now);
        }
    }

    // ---- acknowledge, history, snapshots ----

    /// <summary>Marks an active alert as seen; its health component goes away until it clears and raises again.</summary>
    public bool Acknowledge(string ruleId)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(ruleId, out var st) || st.Phase != SensorAlertStates.Active || st.Acknowledged)
            {
                return false;
            }
            st.Acknowledged = true;
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

    public List<SensorAlertState> States()
    {
        var now = Mono();
        lock (_lock)
        {
            return _states.Select(kv => ToState(kv.Key, kv.Value, now)).ToList();
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
            SensorAlertStates.Unavailable => st.InvalidSince,
            SensorAlertStates.Ok => null,
            _ => st.PhaseSince,
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
            Acknowledged = st.Acknowledged,
        };
    }

    /// <summary>One component per active, unacknowledged rule that opted into health.</summary>
    internal List<HealthComponent> HealthComponents()
    {
        var settings = _store.Load();
        var rules = settings.Diagnostics.SensorAlerts ?? new List<SensorAlertRule>();
        var fahrenheit = settings.Units.MonitoringTempUnit == "f";
        var result = new List<HealthComponent>();
        lock (_lock)
        {
            foreach (var rule in rules)
            {
                if (!rule.ShowInHealth || !_states.TryGetValue(rule.Id, out var st)
                    || st.Phase != SensorAlertStates.Active || st.Acknowledged)
                {
                    continue;
                }
                var severity = rule.Severity == SensorAlertRules.Act ? HealthStatuses.Act : HealthStatuses.Watch;
                var text = SensorAlertText.Describe(rule, st.Direction, st.Value ?? st.Extreme ?? 0, st.Units, fahrenheit);
                result.Add(new HealthComponent
                {
                    Id = $"alert:{rule.Id}",
                    Kind = "alerts",
                    Name = SensorAlertText.DisplayName(rule.Sensor),
                    Status = severity,
                    Reasons = new List<HealthComponentReason>
                    {
                        new("alerts.sensorThreshold", severity, text, $"ruleId={rule.Id} category={rule.Sensor.Category} direction={st.Direction}"),
                    },
                });
            }
        }
        return result;
    }

    private static DiagnosticsAlertNotice BuildNotice(SensorAlertRule rule, SensorAlertRuntime st, bool fahrenheit) =>
        new(
            "Sensor alert",
            SensorAlertText.Describe(rule, st.Direction, st.Value ?? st.Extreme ?? 0, st.Units, fahrenheit),
            NoticeKind,
            Urgent: rule.Severity == SensorAlertRules.Act);

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>Toast and health wording, in the user's temperature unit.</summary>
internal static class SensorAlertText
{
    /// <summary>The sensor's name with its type appended unless the name already says it.</summary>
    internal static string DisplayName(SensorRef sensor) =>
        sensor.Name.EndsWith(sensor.Type, StringComparison.OrdinalIgnoreCase) || sensor.Type.Length == 0
            ? sensor.Name
            : $"{sensor.Name} {sensor.Type}";

    /// <summary>E.g. "Pin 3 Current above 9.5 A for 10 s (now 10.1 A)".</summary>
    internal static string Describe(SensorAlertRule rule, string? direction, double value, string units, bool fahrenheit)
    {
        var below = direction == "below";
        var threshold = (below ? rule.Low : rule.High) ?? 0;
        var delay = rule.RaiseAfterSeconds > 0 ? $" for {rule.RaiseAfterSeconds} s" : "";
        return $"{DisplayName(rule.Sensor)} {(below ? "below" : "above")} {Format(threshold, rule.Sensor.Type, units, fahrenheit)}{delay} (now {Format(value, rule.Sensor.Type, units, fahrenheit)})";
    }

    internal static string Format(double value, string type, string units, bool fahrenheit)
    {
        if (type == "Temperature")
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
