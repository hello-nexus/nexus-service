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

    private const long FlexLcdStepSpacingMs = 1200;

    private sealed class FlexLcdChainState
    {
        public readonly string?[] ColorSig = new string?[Slv3FlexLcd.MaxFans];
        public readonly Queue<FlexLcdStep> Steps = new();
        public long NextMs;
        public bool Enabled;
        public bool SaveNoted;
        public byte Marker;
        public int FanMask;
        public int FanCount;
    }

    private readonly record struct FlexLcdStep(byte Cmd, byte Arg, byte[]? Data);

    private readonly Dictionary<string, FlexLcdChainState> _flexLcdStates = new(StringComparer.Ordinal);

    private Slv3FlexLcdFrame? ReadFlexLcd()
    {
        try
        {
            return FlexLcd?.Invoke();
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[lianli-wireless] wireless theme readings failed: {ex.Message}");
            return null;
        }
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
                if (!IsBoundToUsLocked(record) || record.FansType.Length == 0 || !Slv3FlexLcd.IsLcdFanType(record.FansType[0])
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
            if (active.Contains(key))
            {
                continue;
            }
            ReleaseFlexLcdChainLocked(key, state, nowMs);
            if (state.Steps.Count == 0 && !state.Enabled)
            {
                _flexLcdStates.Remove(key);
            }
        }
        return rows.Count == 0 || frame is null ? null : Slv3FlexLcd.BuildLcdBlock(rows, frame.Sensors, DateTime.Now);
    }

    private Slv3FlexLcd.Row DriveFlexLcdChainLocked(Slv3DeviceRecord record, Slv3FlexLcdChain chain, FlexLcdChainState state, long nowMs)
    {
        var key = Convert.ToHexString(record.Mac);
        var isSlInf = Slv3Protocol.ClassifyFanFamily(record.FansType[0]) == Slv3FanFamily.SlInfFlex;
        var fans = Math.Min(Slv3FlexLcd.MaxFans, record.FanCount);
        var themes = new byte[Slv3FlexLcd.MaxFans];
        var sources = new byte[Slv3FlexLcd.MaxFans];
        state.Enabled = true;
        state.FanCount = fans;
        state.FanMask = (1 << fans) - 1;
        var wantMask = 0;
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
            if (state.ColorSig[c] != sig)
            {
                state.ColorSig[c] = sig;
                state.Marker = state.Marker >= 253 ? (byte)1 : (byte)(state.Marker + 1);
                var data = Slv3FlexLcd.BuildColorBuffer(c, theme, fan.Colors, state.Marker);
                state.Steps.Enqueue(new FlexLcdStep(Slv3Protocol.RfSendColors, 0, data));
            }
        }
        if ((record.WirelessThemeSwitch & wantMask) != wantMask)
        {
            if (!state.Steps.Any(s => s.Cmd == Slv3Protocol.RfSwitchWirelessTheme))
            {
                state.Steps.Enqueue(new FlexLcdStep(Slv3Protocol.RfSwitchWirelessTheme, (byte)(record.WirelessThemeSwitch | wantMask), null));
            }
            state.SaveNoted = false;
        }
        else if (!state.SaveNoted)
        {
            state.SaveNoted = true;
            NoteConfigChangedLocked();
        }
        RunFlexLcdStepLocked(record, key, state, nowMs);
        return new Slv3FlexLcd.Row(record.RxType, themes, sources, Slv3FlexLcd.Direction(chain.Rotation), (byte)(chain.Brightness > 0 ? Math.Clamp(chain.Brightness, 1, 100) : 60), fans);
    }

    // A chain the user switched off, unbound or removed: its screens go back to USB content.
    private void ReleaseFlexLcdChainLocked(string key, FlexLcdChainState state, long nowMs)
    {
        if (state.Enabled)
        {
            state.Enabled = false;
            Array.Clear(state.ColorSig);
            state.Steps.Clear();
            if (TryFindRecordLocked(Convert.FromHexString(key), out var record) && IsBoundToUsLocked(record)
                && (record.WirelessThemeSwitch & state.FanMask) != 0)
            {
                state.Steps.Enqueue(new FlexLcdStep(Slv3Protocol.RfSwitchWirelessTheme, (byte)(record.WirelessThemeSwitch & ~state.FanMask), null));
                state.SaveNoted = false;
            }
        }
        if (state.Steps.Count > 0 && TryFindRecordLocked(Convert.FromHexString(key), out var rec) && IsBoundToUsLocked(rec))
        {
            RunFlexLcdStepLocked(rec, key, state, nowMs);
            if (state.Steps.Count == 0)
            {
                NoteConfigChangedLocked();
            }
        }
        else
        {
            state.Steps.Clear();
        }
    }

    private void RunFlexLcdStepLocked(Slv3DeviceRecord record, string key, FlexLcdChainState state, long nowMs)
    {
        if (state.Steps.Count == 0 || nowMs < state.NextMs || _pendingCommands.ContainsKey(key) || _tx is null)
        {
            return;
        }
        var step = state.Steps.Dequeue();
        if (QueueSequencedCommandLocked(record, step.Cmd, step.Arg, step.Data) is { } seq)
        {
            ServiceLog.Info($"[lianli-wireless] wireless theme {(step.Cmd == Slv3Protocol.RfSendColors ? "colors" : "switch")} sent to {key} (seq {seq})");
            state.NextMs = nowMs + FlexLcdStepSpacingMs;
        }
    }
}
