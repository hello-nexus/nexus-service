using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Platform;

namespace Nexus.Service.Diagnostics.Cooling;

/// <summary>Wire-facing status values for a monitored cooling channel.</summary>
public static class CoolingStallStatuses
{
    public const string Ok = "ok";
    public const string Stalled = "stalled";
    public const string Suspect = "suspect";
    public const string Unknown = "unknown";
}

public sealed record CoolingStallDevice(
    string Id, string Name, string Type,
    double? Rpm, double? TargetDutyPercent,
    string Status, DateTime? SinceUtc);

public sealed record CoolingStallSnapshot(bool Supported, IReadOnlyList<CoolingStallDevice> Devices);

/// <summary>
/// Pure, tick-driven stall/suspect state machine for AIO pumps and fans. No I/O,
/// no hardware access - callers feed it periodic (deviceId, rpm, targetDuty)
/// observations via <see cref="Observe"/> and read back the classified state via
/// <see cref="Snapshot"/>. Kept deliberately hardware-agnostic so it is fully
/// unit-testable without a real fan controller.
///
/// Conservative: slow to flag, instant to clear. A channel only flags once a
/// low/zero rpm condition at a meaningful duty has sustained for its window
/// (fans get a lower-duty, longer-sustain "suspect" tier below the stall
/// tier); any real rpm reading clears the flag immediately. A channel with no
/// duty signal, or one that has never once reported a real rpm, stays
/// "unknown" - there is nothing to compare a stall against. See the constants
/// below for the exact thresholds.
/// </summary>
public sealed class CoolingStallDetector
{
    private static readonly TimeSpan PumpStallSustain = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FanStallSustain = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan FanSuspectSustain = TimeSpan.FromMinutes(5);

    private const double PumpStallRpm = 50;
    private const double PumpRecoveryRpm = 2 * PumpStallRpm;
    private const double PumpStallMinDuty = 20;
    private const double FanStallMinDuty = 30;
    private const double FanSuspectMinDuty = 15;

    // A channel not fed an observation for this long (an unplugged hub, a
    // removed fan) drops out of Snapshot() entirely.
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(10);

    private sealed class ChannelState
    {
        public string Name = "";
        public string Type = "";
        public double? LastRpm;
        public double? LastTargetDutyPercent;
        public bool EverReportedNonzeroRpm;
        public DateTime? PumpConditionStartUtc;
        public DateTime? FanStallConditionStartUtc;
        public DateTime? FanSuspectConditionStartUtc;
        public string Status = CoolingStallStatuses.Unknown;
        public DateTime? SinceUtc;
        public DateTime LastObservedUtc;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, ChannelState> _channels = new(StringComparer.Ordinal);

    /// <summary>Feed one observation for a channel. Safe to call at any cadence;
    /// the sustain windows above are measured from real elapsed time between calls
    /// (via <paramref name="nowUtc"/>), not call count.</summary>
    public void Observe(string deviceId, string name, string type, double? rpm, double? targetDutyPercent, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(deviceId)) return;

        lock (_gate)
        {
            PruneStale(nowUtc);

            if (!_channels.TryGetValue(deviceId, out var st))
            {
                st = new ChannelState();
                _channels[deviceId] = st;
            }
            st.LastObservedUtc = nowUtc;
            st.Name = name;
            st.Type = type;
            st.LastRpm = rpm;
            st.LastTargetDutyPercent = targetDutyPercent;

            if (rpm is > 0)
            {
                st.EverReportedNonzeroRpm = true;
            }

            // No duty signal at all: this channel is semi-passive / unknowable,
            // never flagged regardless of rpm.
            if (targetDutyPercent is not { } duty)
            {
                SetUnknown(st);
                return;
            }

            // A device that has never once reported a real rpm - a null reading
            // and a literal 0 (FanChannel.Rpm is non-nullable, so an unpopulated
            // header always reports 0) read identically here - has no baseline to
            // call a stall against.
            if (!st.EverReportedNonzeroRpm)
            {
                SetUnknown(st);
                return;
            }

            // Once a device has proven it can report rpm, a later null reading is
            // treated the same as 0 (a dead tach looks identical to a stopped fan).
            double effectiveRpm = rpm ?? 0;

            bool isPump = string.Equals(type, "pump", StringComparison.OrdinalIgnoreCase);
            if (isPump)
            {
                EvaluatePump(st, nowUtc, effectiveRpm, duty);
            }
            else
            {
                EvaluateFan(st, nowUtc, effectiveRpm, duty);
            }
        }
    }

    // Caller holds _gate. Runs relative to the caller-supplied nowUtc (never
    // DateTime.UtcNow) so the detector stays fully driven by Observe()'s clock -
    // a device absent from the fan-control provider's channel list simply stops
    // being observed, and the next Observe() call for any OTHER channel prunes
    // it once its last observation falls outside the stale window.
    private void PruneStale(DateTime nowUtc)
    {
        var cutoff = nowUtc - StaleThreshold;
        foreach (var staleId in _channels.Where(kv => kv.Value.LastObservedUtc < cutoff).Select(kv => kv.Key).ToList())
        {
            _channels.Remove(staleId);
        }
    }

    private static void EvaluatePump(ChannelState st, DateTime now, double rpm, double duty)
    {
        if (rpm >= PumpRecoveryRpm)
        {
            st.PumpConditionStartUtc = null;
            SetOk(st);
            return;
        }

        bool condition = rpm < PumpStallRpm && duty >= PumpStallMinDuty;
        if (!condition)
        {
            st.PumpConditionStartUtc = null;
            SetOk(st);
            return;
        }

        st.PumpConditionStartUtc ??= now;
        if (now - st.PumpConditionStartUtc.Value >= PumpStallSustain)
        {
            SetStatus(st, CoolingStallStatuses.Stalled, st.PumpConditionStartUtc.Value);
        }
        else
        {
            SetOk(st);
        }
    }

    private static void EvaluateFan(ChannelState st, DateTime now, double rpm, double duty)
    {
        if (rpm > 0)
        {
            st.FanStallConditionStartUtc = null;
            st.FanSuspectConditionStartUtc = null;
            SetOk(st);
            return;
        }

        bool stallCondition = duty >= FanStallMinDuty;
        bool suspectCondition = duty >= FanSuspectMinDuty;

        st.FanStallConditionStartUtc = stallCondition ? st.FanStallConditionStartUtc ?? now : null;
        st.FanSuspectConditionStartUtc = suspectCondition ? st.FanSuspectConditionStartUtc ?? now : null;

        bool stallSustained = stallCondition && now - st.FanStallConditionStartUtc!.Value >= FanStallSustain;
        bool suspectSustained = suspectCondition && now - st.FanSuspectConditionStartUtc!.Value >= FanSuspectSustain;

        if (stallSustained)
        {
            SetStatus(st, CoolingStallStatuses.Stalled, st.FanStallConditionStartUtc!.Value);
        }
        else if (suspectSustained)
        {
            SetStatus(st, CoolingStallStatuses.Suspect, st.FanSuspectConditionStartUtc!.Value);
        }
        else
        {
            SetOk(st);
        }
    }

    private static void SetOk(ChannelState st)
    {
        st.Status = CoolingStallStatuses.Ok;
        st.SinceUtc = null;
    }

    private static void SetUnknown(ChannelState st)
    {
        st.Status = CoolingStallStatuses.Unknown;
        st.SinceUtc = null;
    }

    private static void SetStatus(ChannelState st, string status, DateTime sinceUtc)
    {
        st.Status = status;
        st.SinceUtc = sinceUtc;
    }

    /// <summary>Current classification for every channel observed so far, minus
    /// any channel still "unknown" (no duty signal, or never once reported a
    /// real rpm - most likely not actually connected). Always Supported=true -
    /// a fan-control provider exists on every OS this service runs on (possibly
    /// reporting zero channels), there is no platform gate here. An unknown
    /// channel stays tracked internally (see <see cref="Observe"/>/<see cref="PruneStale"/>)
    /// so it appears here the moment it reports a real rpm.</summary>
    public CoolingStallSnapshot Snapshot()
    {
        lock (_gate)
        {
            var devices = _channels
                .Where(kv => kv.Value.Status != CoolingStallStatuses.Unknown)
                .Select(kv => new CoolingStallDevice(
                    kv.Key, kv.Value.Name, kv.Value.Type,
                    kv.Value.LastRpm, kv.Value.LastTargetDutyPercent,
                    kv.Value.Status, kv.Value.SinceUtc))
                .ToList();
            return new CoolingStallSnapshot(true, devices);
        }
    }
}

/// <summary>
/// Feeds <see cref="CoolingStallDetector"/> from live hardware every 5s. Reads
/// <see cref="IFanControlProvider.GetFanChannels"/> - the same provider
/// CoolingRoutes' "/cooling/status" route reads - directly, bypassing HTTP.
/// FanChannel.Kind (<see cref="FanKinds"/>) supplies the pump/fan classification
/// and FanChannel.DutyPercent the target duty; both are non-nullable on this
/// model today; the detector's null handling exists for the general contract
/// and is exercised directly in its unit tests.
/// </summary>
public sealed class CoolingStallFeeder : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    private readonly IFanControlProvider _fans;
    private readonly CoolingStallDetector _detector;
    private readonly FeatureGates _gates;

    public CoolingStallFeeder(IFanControlProvider fans, CoolingStallDetector detector, FeatureGates? gates = null)
    {
        _fans = fans;
        _detector = detector;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    public CoolingStallSnapshot Snapshot() => _detector.Snapshot();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        do
        {
            try { Tick(); }
            catch (Exception ex) { ServiceLog.Warn($"[cooling-stall] tick failed: {ex.Message}"); }
        }
        while (await WaitForNextTickSafe(timer, stoppingToken));
    }

    private static async Task<bool> WaitForNextTickSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }

    internal void Tick()
    {
        if (!_gates.Diagnostics)
        {
            return;
        }
        var now = DateTime.UtcNow;
        IReadOnlyList<FanChannel> channels;
        try { channels = _fans.GetFanChannels(); }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[cooling-stall] GetFanChannels failed: {ex.Message}");
            return;
        }

        foreach (var ch in channels)
        {
            // A port that cannot read its tach right now (MiniHub between
            // agreed polls, SLV3 chains) carries Rpm = 0 with no fan stopped;
            // leave it unobserved so it ages out instead of reading as a stall.
            if (ch.RpmUnavailable) continue;
            var type = string.Equals(ch.Kind, FanKinds.Pump, StringComparison.Ordinal) ? "pump" : "fan";
            _detector.Observe(ch.Id, ch.Name, type, ch.Rpm, ch.DutyPercent, now);
        }
    }
}
