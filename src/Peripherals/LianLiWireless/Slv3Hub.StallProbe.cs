using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Nexus.Service.Peripherals.LianLiWireless;

// Long RX "nodata" stalls end on their own after minutes. This probe applies ONE recovery
// action per stall once it reaches the threshold, alternating across stalls, and writes the
// outcome to the link log so a support bundle shows which action ends long stalls.
// Config file (optional, overrides the defaults): "<thresholdMs> <letters>" plus an optional
// "reappear=off" token, or "off" for everything.
// Letters: a = 0x15 via TX, b = pause window uploads until a good GetDev, c = reopen TX and RX,
// n = observe only.
public sealed partial class Slv3Hub
{
    private const long StallPauseCapMs = 120_000;

    private readonly Slv3StallPolicy _stallPolicy = new();
    private long _pauseUploadsUntilMs;
    private bool _stallHeardChain;
    private long _stallConfigReadMs = long.MinValue / 2;
    private readonly Slv3ReappearAb _reappear = new();
    private long _reappearStallsSeen;
    private readonly HashSet<string> _stallEverSeen = new(StringComparer.Ordinal);

    internal Func<Slv3StallConfig> StallConfigSource { get; set; } = ReadStallConfig;

    internal static string StallConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nexus", "slv3-stall.txt");

    private static Slv3StallConfig ReadStallConfig()
    {
        try
        {
            var path = StallConfigPath;
            if (File.Exists(path))
            {
                return Slv3StallConfig.Parse(File.ReadAllText(path));
            }
        }
        catch
        {
            // fall through to the defaults
        }
        return Slv3StallConfig.Default;
    }

    private bool StallUploadsPaused() => _nowMs() < Interlocked.Read(ref _pauseUploadsUntilMs);

    // Reappear mode L: this chain's window upload waits so the writer streams live frames instead.
    private bool ReappearHoldsWindow(string macHex) => _reappear.IsHeld(macHex, _nowMs());

    private void StallLog(string text) => Diag.Event("stall-probe " + text);

    private void RefreshStallConfigLocked(long nowMs)
    {
        if (!_stallPolicy.Active && nowMs - _stallConfigReadMs >= 60_000)
        {
            _stallConfigReadMs = nowMs;
            _stallPolicy.Config = StallConfigSource();
            _reappear.Enabled = _stallPolicy.Config.Enabled && _stallPolicy.Config.ReappearAb;
        }
    }

    // Caller holds _lock; a chain that was known before is back in a device list.
    private void StallOnChainReappearedLocked(string key, long nowMs)
    {
        RefreshStallConfigLocked(nowMs);
        _reappear.OnReappear(key, nowMs);
    }

    private void StallOnChainDroppedLocked(string key) => _reappear.OnDropped(key);

    // Resolves pending reappear events against the diag's stall starts; the diag owns the stall definition.
    private void ReappearResolveLocked(long nowMs)
    {
        var stalls = Diag.StallStartCount;
        if (stalls != _reappearStallsSeen)
        {
            _reappearStallsSeen = stalls;
            foreach (var r in _reappear.OnStall(Diag.LastStallStartMs))
            {
                StallLog(r.Describe());
            }
        }
        foreach (var r in _reappear.Expire(nowMs))
        {
            StallLog(r.Describe());
        }
    }

    // Caller holds _lock; called on every nodata poll.
    private void StallOnNoDataLocked(long nowMs)
    {
        RefreshStallConfigLocked(nowMs);
        ReappearResolveLocked(nowMs);
        var action = _stallPolicy.OnNoData(nowMs, _stallHeardChain, out _, out var suppressed);
        if (suppressed)
        {
            StallLog($"#{_stallPolicy.Seq} action=a skipped, 0x15 reset rate limit");
        }
        if (action == Slv3StallPolicy.NoAction)
        {
            return;
        }
        var age = nowMs - _stallPolicy.StartMs;
        StallLog($"#{_stallPolicy.Seq} action={action} age={age}ms txprobe={ProbeTxLocked()}");
        switch (action)
        {
            case 'a':
                _nextRxRecoveryMs = 0;
                BeginRxRecoveryLocked("stall probe: 0x15 via TX", resetViaTx: true);
                break;
            case 'b':
                Interlocked.Exchange(ref _pauseUploadsUntilMs, nowMs + StallPauseCapMs);
                break;
            case 'c':
                if (Volatile.Read(ref _portCycleInFlight) != 0)
                {
                    break;
                }
                ReopenTxLocked(FindPortLocked(Slv3DongleRole.Tx), nowMs);
                _nextRxRecoveryMs = 0;
                BeginRxRecoveryLocked("stall probe: reopen TX and RX", resetViaTx: false);
                break;
        }
    }

    // Caller holds _lock; called when a valid device list arrives.
    private void StallOnGoodReplyLocked(long nowMs)
    {
        Interlocked.Exchange(ref _pauseUploadsUntilMs, 0);
        ReappearResolveLocked(nowMs);
        if (_stallPolicy.OnGood(nowMs) is { } end && end.StallMs >= _stallPolicy.Config.ThresholdMs)
        {
            StallLog($"#{end.Seq} ended: stall={end.StallMs}ms action={end.ActionName} sinceAction={end.SinceActionText}");
        }
    }

    // Caller holds _lock; the link went down while a stall may have been open.
    private void StallOnLinkDownLocked(long nowMs)
    {
        Interlocked.Exchange(ref _pauseUploadsUntilMs, 0);
        // After a relink every chain is a first sighting again, not a reappear, so a bounce cannot skew the A/B.
        _stallEverSeen.Clear();
        if (_stallPolicy.OnLinkDown(nowMs) is { } end && end.StallMs >= _stallPolicy.Config.ThresholdMs)
        {
            StallLog($"#{end.Seq} ended without a good GetDev (link down): stall={end.StallMs}ms action={end.ActionName} sinceAction=none");
        }
    }

    private string ProbeTxLocked()
    {
        if (_tx is not { IsOpen: true })
        {
            return "txClosed";
        }
        var t0 = Environment.TickCount64;
        if (!_tx.RfSend(Slv3Protocol.BuildGetMac(_channel)))
        {
            return "writeFail";
        }
        var reply = _tx.RfRead(Slv3Protocol.UsbPacketSize);
        var lat = Environment.TickCount64 - t0;
        return Slv3Protocol.TryParseGetMac(reply, out _, out var clock, out _)
            ? $"ok(clock={clock},{lat}ms)"
            : $"noReply({lat}ms,len={reply.Length})";
    }
}

internal sealed record Slv3StallConfig(bool Enabled, long ThresholdMs, string Actions, bool ReappearAb = true)
{
    public static readonly Slv3StallConfig Default = new(true, 30_000, "ab");

    /// <summary>Null or blank gives the defaults; "off" or an empty action list disables the probe.</summary>
    public static Slv3StallConfig Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Default;
        }
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var reappear = !Array.Exists(tokens, t => t.Equals("reappear=off", StringComparison.OrdinalIgnoreCase));
        var parts = Array.FindAll(tokens, t => !t.StartsWith("reappear=", StringComparison.OrdinalIgnoreCase));
        if (parts.Length == 0)
        {
            return Default with { ReappearAb = reappear };
        }
        if (parts.Length == 1 && parts[0].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return new Slv3StallConfig(false, Default.ThresholdMs, "", reappear);
        }
        if (parts.Length < 2 || !long.TryParse(parts[0], out var threshold) || threshold < 0)
        {
            return parts.Length == 1 && long.TryParse(parts[0], out _) ? new Slv3StallConfig(false, Default.ThresholdMs, "", reappear) : Default with { ReappearAb = reappear };
        }
        var letters = string.Concat(Array.FindAll(parts[1].ToLowerInvariant().ToCharArray(), c => c is 'a' or 'b' or 'c' or 'n'));
        return letters.Length == 0 ? new Slv3StallConfig(false, threshold, "", reappear) : new Slv3StallConfig(true, threshold, letters, reappear);
    }
}

// Pure stall bookkeeping: when to act, which action, and the per-stall result. No I/O.
internal sealed class Slv3StallPolicy
{
    public const char NoAction = '\0';
    public const long MinResetGapMs = 5 * 60_000;

    internal readonly record struct StallEnd(int Seq, long StallMs, string ActionName, string SinceActionText);

    private bool _acted;
    private long _actionMs;
    private char _action;
    private int _rotation;
    private long _lastResetMs;
    private bool _everReset;

    public Slv3StallConfig Config { get; set; } = Slv3StallConfig.Default;
    public bool Active { get; private set; }
    public int Seq { get; private set; }
    public long StartMs { get; private set; }

    /// <summary>Returns the action to apply now, or <see cref="NoAction"/>. At most one action per stall.</summary>
    public char OnNoData(long nowMs, bool heardChain, out bool began, out bool suppressed)
    {
        suppressed = false;
        began = !Active;
        if (!Active)
        {
            Active = true;
            StartMs = nowMs;
            Seq++;
            _acted = false;
            _action = NoAction;
            _actionMs = 0;
        }
        if (_acted || !Config.Enabled || !heardChain || Config.Actions.Length == 0 || nowMs - StartMs < Config.ThresholdMs)
        {
            return NoAction;
        }
        _acted = true;
        var action = Config.Actions[_rotation % Config.Actions.Length];
        if (action == 'a' && _everReset && nowMs - _lastResetMs < MinResetGapMs)
        {
            suppressed = true;
            return NoAction;
        }
        _rotation++;
        if (action == 'a')
        {
            _everReset = true;
            _lastResetMs = nowMs;
        }
        _action = action;
        _actionMs = nowMs;
        return action;
    }

    public StallEnd? OnGood(long nowMs) => End(nowMs, good: true);

    public StallEnd? OnLinkDown(long nowMs) => End(nowMs, good: false);

    private StallEnd? End(long nowMs, bool good)
    {
        if (!Active)
        {
            return null;
        }
        Active = false;
        var name = _action == NoAction ? "none" : _action.ToString();
        var since = good && _action != NoAction ? $"{nowMs - _actionMs}ms" : "none";
        return new StallEnd(Seq, nowMs - StartMs, name, since);
    }
}

// A/B on a chain reappearing: alternate between W (fresh window upload, current behavior) and
// L (hold that chain's window upload so it streams live frames) and record whether a whole-dongle
// stall started shortly after. Pure bookkeeping with its own lock.
internal sealed class Slv3ReappearAb
{
    public const long HoldMs = 30_000;
    public const long ResolveWithinMs = 15_000;
    private const int MaxPending = 32;

    internal sealed record Result(string Key, char Mode, long? StallAfterMs)
    {
        public string Describe() => StallAfterMs is { } age
            ? $"reappear chain={Key} mode={Mode} stallWithin15s=yes stallAge={age}ms"
            : $"reappear chain={Key} mode={Mode} stallWithin15s=no";
    }

    private sealed record Pending(string Key, char Mode, long AtMs);

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _holdUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Pending> _pending = new();
    private int _count;

    public bool Enabled { get; set; } = true;

    /// <summary>Starts a reappear event and returns its mode; W when disabled.</summary>
    public char OnReappear(string key, long nowMs)
    {
        lock (_gate)
        {
            if (!Enabled)
            {
                return 'W';
            }
            var mode = _count++ % 2 == 0 ? 'W' : 'L';
            if (mode == 'L')
            {
                _holdUntil[key] = nowMs + HoldMs;
            }
            else
            {
                _holdUntil.Remove(key);
            }
            if (_pending.Count >= MaxPending)
            {
                _pending.RemoveAt(0);
            }
            _pending.Add(new Pending(key, mode, nowMs));
            return mode;
        }
    }

    /// <summary>A drop ends continuous hearing, so any hold on the chain ends with it.</summary>
    public void OnDropped(string key)
    {
        lock (_gate)
        {
            _holdUntil.Remove(key);
        }
    }

    public bool IsHeld(string key, long nowMs)
    {
        lock (_gate)
        {
            return _holdUntil.TryGetValue(key, out var until) && nowMs < until;
        }
    }

    /// <summary>A whole-dongle stall started: resolves the events from the last window before it.</summary>
    public List<Result> OnStall(long stallStartMs)
    {
        lock (_gate)
        {
            var done = new List<Result>();
            _pending.RemoveAll(p =>
            {
                var age = stallStartMs - p.AtMs;
                if (age < 0 || age > ResolveWithinMs)
                {
                    return false;
                }
                done.Add(new Result(p.Key, p.Mode, age));
                return true;
            });
            return done;
        }
    }

    /// <summary>Resolves events whose window passed with no stall.</summary>
    public List<Result> Expire(long nowMs)
    {
        lock (_gate)
        {
            var done = new List<Result>();
            _pending.RemoveAll(p =>
            {
                if (nowMs - p.AtMs <= ResolveWithinMs)
                {
                    return false;
                }
                done.Add(new Result(p.Key, p.Mode, null));
                return true;
            });
            return done;
        }
    }
}
