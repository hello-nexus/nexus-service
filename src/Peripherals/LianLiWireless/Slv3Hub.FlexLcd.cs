using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiWireless;

// Wireless themes of TL / SL-INF LCD Flex fans: ClockSync carries the host readings and the per-slot
// theme table; SendColors and SwitchWirelessTheme are sequenced commands. Nothing is sent for a chain
// the user has not switched on.
public sealed partial class Slv3Hub
{
    /// <summary>Settings and host readings for the wireless themes; null while no chain uses them. Read once per <see cref="DriveTick"/>, outside the hub lock.</summary>
    public Func<Slv3FlexLcdFrame?>? FlexLcd { get; set; }

    internal const long FlexLcdStepSpacingMs = 1200;
    internal const long FlexLcdSwitchBackoffMs = 2000;
    internal const int FlexLcdSwitchAttempts = 5;

    private sealed class FlexLcdChainState
    {
        public readonly string?[] Desired = new string?[Slv3FlexLcd.MaxFans];
        public readonly string?[] Acked = new string?[Slv3FlexLcd.MaxFans];
        public readonly (int Theme, uint[]? Colors)[] Work = new (int, uint[]?)[Slv3FlexLcd.MaxFans];
        /// <summary>The color command in flight: its sequence and the fan and signature it carries; recorded as done only when the chain echoes the sequence.</summary>
        public (byte Seq, int Fan, string Sig)? InFlight;
        /// <summary>Sends per fan for the current colors; a chain that never answers is not asked forever.</summary>
        public readonly int[] ColorAttempts = new int[Slv3FlexLcd.MaxFans];
        public long NextMs;
        public bool Enabled;
        public bool SaveNoted;
        public byte Marker;
        public int FanMask;
        public int SwitchAttempts;
        public long SwitchNextMs;
        public int SwitchWantMask = -1;
        public bool SwitchGaveUp;
        public bool WasStale;
    }

    private readonly Dictionary<string, FlexLcdChainState> _flexLcdStates = new(StringComparer.Ordinal);
    private Slv3FlexLcdFrame? _lastFlexLcdFrame;

    // A failed read keeps the previous settings: a chain is never released because a read threw.
    private Slv3FlexLcdFrame? ReadFlexLcd()
    {
        try
        {
            _lastFlexLcdFrame = FlexLcd?.Invoke();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[lianli-wireless] wireless theme readings failed, keeping the last settings: {ex.Message}");
        }
        return _lastFlexLcdFrame;
    }

    // L-Connect's SyncControlInfo counts the bound devices ahead of a chain in the dongle's report order at [16]; the
    // capture lists the TL fan, the SL-INF fan, then the Strimer, with 0 and 1 on the two fans. Strimers and HydroShift
    // IIs count like any other bound chain. L-Connect also skips a device mid effect change (changingEffect), a transient
    // this hub does not track. The order is the confirmed chains in the last report, with chains a partial reply missed kept after them.
    private byte FlexLcdOrdinalLocked(Slv3DeviceRecord record)
    {
        var self = Convert.ToHexString(record.Mac);
        var ordinal = 0;
        foreach (var key in _reportOrder)
        {
            if (string.Equals(key, self, StringComparison.Ordinal))
            {
                return (byte)ordinal;
            }
            if (_knownChains.TryGetValue(key, out var other) && IsBoundToUsLocked(other.Record))
            {
                ordinal++;
            }
        }
        return 0;
    }

    /// <summary>The ClockSync LCD block for this tick, or null when no chain shows a wireless theme; drives each chain's color and switch steps.</summary>
    private byte[]? SyncFlexLcdLocked(Slv3FlexLcdFrame? frame, long nowMs)
    {
        var rows = new List<Slv3FlexLcd.Row>();
        var active = new HashSet<string>(StringComparer.Ordinal);
        if (frame is not null)
        {
            foreach (var record in _lastFanRecords)
            {
                var key = Convert.ToHexString(record.Mac);
                if (!IsBoundToUsLocked(record) || IsLeavingLocked(key) || record.FansType.Length == 0 || !Slv3FlexLcd.IsLcdFanType(record.FansType[0])
                    || !frame.Chains.TryGetValue(key, out var chain) || !chain.Enabled || record.FanCount <= 0)
                {
                    continue;
                }
                active.Add(key);
                if (!_flexLcdStates.TryGetValue(key, out var state))
                {
                    state = new FlexLcdChainState();
                    _flexLcdStates[key] = state;
                }
                rows.Add(DriveFlexLcdChainLocked(record, chain, state, nowMs));
            }
        }
        foreach (var (key, state) in _flexLcdStates.ToList())
        {
            if (!active.Contains(key))
            {
                ReleaseFlexLcdChainLocked(key, state, nowMs);
            }
        }
        return rows.Count == 0 || frame is null ? null : Slv3FlexLcd.BuildLcdBlock(rows, frame.Sensors, DateTime.Now);
    }

    // A chain the user is unbinding, or whose release is already on the air.
    private bool IsLeavingLocked(string key) =>
        _userUnbound.Contains(key) || _deferredUnbinds.ContainsKey(key) || (_intents.TryGetValue(key, out var intent) && intent.Unbinding);

    private Slv3FlexLcd.Row DriveFlexLcdChainLocked(Slv3DeviceRecord record, Slv3FlexLcdChain chain, FlexLcdChainState state, long nowMs)
    {
        var key = Convert.ToHexString(record.Mac);
        var isSlInf = Slv3Protocol.ClassifyFanFamily(record.FansType[0]) == Slv3FanFamily.SlInfFlex;
        var fans = Math.Min(Slv3FlexLcd.MaxFans, record.FanCount);
        var themes = new byte[Slv3FlexLcd.MaxFans];
        var sources = new byte[Slv3FlexLcd.MaxFans];
        state.Enabled = true;
        state.FanMask = (1 << fans) - 1;
        var wantMask = 0;
        var stale = _knownChains.TryGetValue(key, out var known) && nowMs - known.LastSeenMs > ChainStaleMs;
        if (!stale && state.WasStale)
        {
            // Heard again: the silence did not count against it.
            state.SwitchAttempts = 0;
            state.SwitchGaveUp = false;
            state.SwitchNextMs = 0;
            Array.Clear(state.ColorAttempts);
            state.InFlight = null;
        }
        state.WasStale = stale;
        for (var i = 0; i < fans; i++)
        {
            var c = Slv3FlexLcd.CacheIndex(isSlInf, record.RightAttach, record.FanCount, i);
            if (c < 0)
            {
                continue;
            }
            var fan = i < chain.Fans.Count ? chain.Fans[i] : new Slv3FlexLcdFan(0, 0, null);
            var theme = Math.Clamp(fan.Theme, 0, Slv3FlexLcd.ThemeCount - 1);
            themes[c] = (byte)theme;
            sources[c] = (byte)Math.Clamp(fan.Source, 0, Slv3FlexLcd.DataSourceCount - 1);
            wantMask |= 1 << c;
            var sig = $"{theme}:{(fan.Colors is null ? "" : string.Join(',', fan.Colors))}";
            if (state.Desired[c] != sig)
            {
                state.Desired[c] = sig;
                state.Acked[c] = null;
                state.ColorAttempts[c] = 0;
                state.Work[c] = (theme, fan.Colors);
            }
        }
        if (!stale)
        {
            TrackFlexColorAckLocked(record, key, state);
            RunFlexColorStepLocked(record, key, state, nowMs);
            DriveFlexSwitchLocked(record, key, state, wantMask, nowMs);
        }
        return new Slv3FlexLcd.Row(record.RxType, themes, sources, Slv3FlexLcd.Direction(chain.Rotation), (byte)(chain.Brightness > 0 ? Math.Clamp(chain.Brightness, 1, 100) : 60), fans);
    }

    // The colors count as delivered only once the chain echoes the command sequence; a command that was dropped or overwritten is sent again.
    private void TrackFlexColorAckLocked(Slv3DeviceRecord record, string key, FlexLcdChainState state)
    {
        if (state.InFlight is not { } flight)
        {
            return;
        }
        if (record.CmdSeq == flight.Seq)
        {
            state.Acked[flight.Fan] = flight.Sig;
            state.ColorAttempts[flight.Fan] = 0;
            state.InFlight = null;
        }
        else if (!_pendingCommands.TryGetValue(key, out var pending) || pending.TargetSeq != flight.Seq)
        {
            state.InFlight = null;
        }
    }

    private void RunFlexColorStepLocked(Slv3DeviceRecord record, string key, FlexLcdChainState state, long nowMs)
    {
        if (state.InFlight is not null || nowMs < state.NextMs || _pendingCommands.ContainsKey(key) || _tx is null)
        {
            return;
        }
        for (var c = 0; c < Slv3FlexLcd.MaxFans; c++)
        {
            if (state.Desired[c] is not { } sig || state.Acked[c] == sig || state.ColorAttempts[c] >= FlexLcdSwitchAttempts)
            {
                continue;
            }
            state.Marker = state.Marker >= 253 ? (byte)1 : (byte)(state.Marker + 1);
            var (theme, colors) = state.Work[c];
            var data = Slv3FlexLcd.BuildColorBuffer(c, theme, colors, state.Marker);
            if (QueueSequencedCommandLocked(record, Slv3Protocol.RfSendColors, 0, data) is { } seq)
            {
                state.InFlight = (seq, c, sig);
                state.NextMs = nowMs + (state.ColorAttempts[c] == 0 ? FlexLcdStepSpacingMs : FlexLcdSwitchBackoffMs << Math.Min(state.ColorAttempts[c], 6));
                if (++state.ColorAttempts[c] >= FlexLcdSwitchAttempts)
                {
                    ServiceLog.Warn($"[lianli-wireless] {key} does not acknowledge its theme colors; not asking again until they change");
                }
                ServiceLog.Info($"[lianli-wireless] wireless theme colors sent to {key} (seq {seq})");
            }
            return;
        }
    }

    // The switch is derived from what the chain reports, retried with a growing gap, and never queued while a command waits.
    private void DriveFlexSwitchLocked(Slv3DeviceRecord record, string key, FlexLcdChainState state, int wantMask, long nowMs)
    {
        if (state.SwitchWantMask != wantMask)
        {
            state.SwitchWantMask = wantMask;
            state.SwitchAttempts = 0;
            state.SwitchGaveUp = false;
        }
        if ((record.WirelessThemeSwitch & wantMask) == wantMask)
        {
            state.SwitchAttempts = 0;
            state.SwitchGaveUp = false;
            if (!state.SaveNoted)
            {
                state.SaveNoted = true;
                NoteConfigChangedLocked();
            }
            return;
        }
        state.SaveNoted = false;
        if (state.SwitchGaveUp || nowMs < state.SwitchNextMs || nowMs < state.NextMs || _pendingCommands.ContainsKey(key) || state.InFlight is not null || _tx is null)
        {
            return;
        }
        if (QueueSequencedCommandLocked(record, Slv3Protocol.RfSwitchWirelessTheme, (byte)(record.WirelessThemeSwitch | wantMask)) is { } seq)
        {
            state.SwitchAttempts++;
            state.SwitchNextMs = nowMs + (FlexLcdSwitchBackoffMs << Math.Min(state.SwitchAttempts - 1, 6));
            ServiceLog.Info($"[lianli-wireless] wireless theme switch sent to {key} (seq {seq}, attempt {state.SwitchAttempts})");
            if (state.SwitchAttempts >= FlexLcdSwitchAttempts)
            {
                state.SwitchGaveUp = true;
                ServiceLog.Warn($"[lianli-wireless] {key} does not report its theme switch; not asking again until the settings change");
            }
        }
    }

    // A chain the user switched off or that left the radio: its screens go back to USB content while it is still ours.
    private void ReleaseFlexLcdChainLocked(string key, FlexLcdChainState state, long nowMs)
    {
        var found = TryFindRecordLocked(Convert.FromHexString(key), out var record) && IsBoundToUsLocked(record) && !IsLeavingLocked(key);
        if (!found)
        {
            _flexLcdStates.Remove(key);
            return;
        }
        if (state.Enabled)
        {
            state.Enabled = false;
            Array.Clear(state.Desired);
            Array.Clear(state.Acked);
            state.InFlight = null;
            state.SwitchAttempts = 0;
            state.SwitchGaveUp = false;
            state.SwitchNextMs = 0;
        }
        if ((record.WirelessThemeSwitch & state.FanMask) == 0)
        {
            _flexLcdStates.Remove(key);
            NoteConfigChangedLocked();
            return;
        }
        if (state.SwitchGaveUp || nowMs < state.SwitchNextMs || _pendingCommands.ContainsKey(key) || _tx is null)
        {
            return;
        }
        if (QueueSequencedCommandLocked(record, Slv3Protocol.RfSwitchWirelessTheme, (byte)(record.WirelessThemeSwitch & ~state.FanMask)) is not null)
        {
            state.SwitchAttempts++;
            state.SwitchNextMs = nowMs + (FlexLcdSwitchBackoffMs << Math.Min(state.SwitchAttempts - 1, 6));
            if (state.SwitchAttempts >= FlexLcdSwitchAttempts)
            {
                state.SwitchGaveUp = true;
            }
        }
    }

    internal const long FlexLcdUnbindHoldMs = 4000;
    internal const long FlexLcdUnbindSaveMs = 2000;

    private static int FlexLcdMask(Slv3DeviceRecord record) => (1 << Math.Min(Slv3FlexLcd.MaxFans, Math.Max(0, record.FanCount))) - 1;

    // Unbinding a chain whose screens show a wireless theme turns them back to USB content first; the bits live in the
    // fan's flash and the USB side cannot clear them. True while the unbind waits for that.
    private bool BeginFlexLcdReleaseForUnbindLocked(Slv3DeviceRecord record)
    {
        if (record.FansType.Length == 0 || !Slv3FlexLcd.IsLcdFanType(record.FansType[0]))
        {
            return false;
        }
        _flexLcdStates.Remove(Convert.ToHexString(record.Mac));
        var mask = FlexLcdMask(record);
        if ((record.WirelessThemeSwitch & mask) == 0)
        {
            return false;
        }
        return QueueSequencedCommandLocked(record, Slv3Protocol.RfSwitchWirelessTheme, (byte)(record.WirelessThemeSwitch & ~mask)) is not null;
    }

    // Runs the deferred unbinds: once the chain reports its switch bits cleared, or after the hold, the release frame goes out.
    private void ProcessDeferredUnbindsLocked(long nowMs)
    {
        foreach (var (key, deferred) in _deferredUnbinds.ToList())
        {
            if (_lockBound)
            {
                _deferredUnbinds.Remove(key);
                continue;
            }
            var present = TryFindRecordLocked(Convert.FromHexString(key), out var record);
            var waiting = present && IsBoundToUsLocked(record) && nowMs < deferred.DeadlineMs && (record.WirelessThemeSwitch & FlexLcdMask(record)) != 0;
            if (waiting)
            {
                if (!_pendingCommands.ContainsKey(key))
                {
                    QueueSequencedCommandLocked(record, Slv3Protocol.RfSwitchWirelessTheme, (byte)(record.WirelessThemeSwitch & ~FlexLcdMask(record)));
                }
                continue;
            }
            if (present && IsBoundToUsLocked(record) && (record.WirelessThemeSwitch & FlexLcdMask(record)) == 0)
            {
                // The cleared switch lives in RAM until a SaveCfg writes it to the fan's flash; an unbound fan boots from flash, so the save goes out before the release.
                if (deferred.ClearedAtMs < 0)
                {
                    deferred.ClearedAtMs = nowMs;
                    SendSaveCfgLocked();
                    continue;
                }
                if (nowMs - deferred.ClearedAtMs < FlexLcdUnbindSaveMs)
                {
                    if (!deferred.SavedAgain && nowMs - deferred.ClearedAtMs >= FlexLcdUnbindSaveMs / 2)
                    {
                        deferred.SavedAgain = true;
                        SendSaveCfgLocked();
                    }
                    continue;
                }
            }
            _deferredUnbinds.Remove(key);
            PerformUnbindLocked(key, deferred.MacHex, present ? record : null);
        }
    }
}
