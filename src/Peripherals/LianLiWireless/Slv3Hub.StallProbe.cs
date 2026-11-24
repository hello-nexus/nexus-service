using System;
using System.IO;
using System.Threading;

namespace Nexus.Service.Peripherals.LianLiWireless;

// Long RX "nodata" stalls end on their own after minutes. This probe applies ONE recovery
// action per stall once it reaches the threshold, alternating across stalls, and writes the
// outcome to the link log so a support bundle shows which action ends long stalls.
// Config file (optional, overrides the defaults): "<thresholdMs> <letters>", or "off".
// Letters: a = 0x15 via TX, b = pause window uploads until a good GetDev, c = reopen TX and RX,
// n = observe only.
public sealed partial class Slv3Hub
{
    private const long StallPauseCapMs = 120_000;

    private readonly Slv3StallPolicy _stallPolicy = new();
    private long _pauseUploadsUntilMs;
    private bool _stallHeardChain;
    private long _stallConfigReadMs = long.MinValue / 2;

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

    private void StallLog(string text) => Diag.Event("stall-probe " + text);

    // Caller holds _lock; called on every nodata poll.
    private void StallOnNoDataLocked(long nowMs)
    {
        if (!_stallPolicy.Active && nowMs - _stallConfigReadMs >= 60_000)
        {
            _stallConfigReadMs = nowMs;
            _stallPolicy.Config = StallConfigSource();
        }
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
        if (_stallPolicy.OnGood(nowMs) is { } end && end.StallMs >= _stallPolicy.Config.ThresholdMs)
        {
            StallLog($"#{end.Seq} ended: stall={end.StallMs}ms action={end.ActionName} sinceAction={end.SinceActionText}");
        }
    }

    // Caller holds _lock; the link went down while a stall may have been open.
    private void StallOnLinkDownLocked(long nowMs)
    {
        Interlocked.Exchange(ref _pauseUploadsUntilMs, 0);
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

internal sealed record Slv3StallConfig(bool Enabled, long ThresholdMs, string Actions)
{
    public static readonly Slv3StallConfig Default = new(true, 30_000, "ab");

    /// <summary>Null or blank gives the defaults; "off" or an empty action list disables the probe.</summary>
    public static Slv3StallConfig Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Default;
        }
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && parts[0].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return new Slv3StallConfig(false, Default.ThresholdMs, "");
        }
        if (parts.Length < 2 || !long.TryParse(parts[0], out var threshold) || threshold < 0)
        {
            return parts.Length == 1 && long.TryParse(parts[0], out _) ? new Slv3StallConfig(false, Default.ThresholdMs, "") : Default;
        }
        var letters = string.Concat(Array.FindAll(parts[1].ToLowerInvariant().ToCharArray(), c => c is 'a' or 'b' or 'c' or 'n'));
        return letters.Length == 0 ? new Slv3StallConfig(false, threshold, "") : new Slv3StallConfig(true, threshold, letters);
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
