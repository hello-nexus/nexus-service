using System;
using System.Text;

namespace Nexus.Service.Peripherals.JpegPanels;

public enum AsRockExchangeOutcome
{
    Ok,
    WriteFailed,
    ReadTimeout,
    ReadFailed,
    UndecodableReply,
    Non200,
}

/// <summary>Classifies and formats one ASRock control exchange so a timeout reads differently from a non-200.</summary>
internal static class AsRockLcdExchangeLog
{
    internal const int MaxReplyChars = 200;

    /// <param name="read">Value returned by the HID read: bytes, 0 for a timeout, negative for a failed handle.</param>
    internal static AsRockExchangeOutcome Classify(bool wrote, int read, byte[]? decoded, out string? status)
    {
        status = null;
        if (!wrote)
        {
            return AsRockExchangeOutcome.WriteFailed;
        }
        if (read == 0)
        {
            return AsRockExchangeOutcome.ReadTimeout;
        }
        if (read < 0)
        {
            return AsRockExchangeOutcome.ReadFailed;
        }
        if (decoded is null || decoded.Length == 0)
        {
            return AsRockExchangeOutcome.UndecodableReply;
        }
        status = AsRockLcdProtocol.ParseStatus(decoded);
        return status == "200" ? AsRockExchangeOutcome.Ok : AsRockExchangeOutcome.Non200;
    }

    /// <summary>Printable ASCII only, line breaks shown as a space, cut to <paramref name="max"/> characters.</summary>
    internal static string Sanitize(ReadOnlySpan<byte> reply, int max = MaxReplyChars)
    {
        var sb = new StringBuilder(Math.Min(reply.Length, max + 3));
        for (int i = 0; i < reply.Length; i++)
        {
            if (sb.Length >= max)
            {
                sb.Append("...");
                break;
            }
            byte b = reply[i];
            sb.Append(b is 0x0D or 0x0A ? ' ' : b is >= 0x20 and < 0x7F ? (char)b : '.');
        }
        return sb.ToString();
    }

    internal static string FormatSuppressed(int count) =>
        $"[asrock-lcd] {count} more exchange failures suppressed";

    internal static string Format(
        string command, AsRockExchangeOutcome outcome, string? status, long elapsedMs,
        ReadOnlySpan<byte> reply, int writeError, int timeoutMs)
    {
        return outcome switch
        {
            AsRockExchangeOutcome.Ok =>
                $"[asrock-lcd] {command}: reply 200 in {elapsedMs} ms: {Sanitize(reply)}",
            AsRockExchangeOutcome.Non200 =>
                $"[asrock-lcd] {command}: non-200 reply, status {status ?? "missing"}, in {elapsedMs} ms: {Sanitize(reply)}",
            AsRockExchangeOutcome.ReadTimeout =>
                $"[asrock-lcd] {command}: read timeout, no reply within {timeoutMs} ms (waited {elapsedMs} ms)",
            AsRockExchangeOutcome.ReadFailed =>
                $"[asrock-lcd] {command}: read failed (handle gone), after {elapsedMs} ms",
            AsRockExchangeOutcome.UndecodableReply =>
                $"[asrock-lcd] {command}: reply bytes did not decode, after {elapsedMs} ms",
            _ =>
                $"[asrock-lcd] {command}: write failed, {JpegPanelDiagnostics.DescribeWriteError(writeError)}, after {elapsedMs} ms",
        };
    }
}

/// <summary>Lets the first <c>burst</c> events through, then one line per interval; the held-back count is reported via <see cref="TakeSuppressed"/>.</summary>
internal sealed class RepeatLogLimiter
{
    private readonly int _burst;
    private readonly long _intervalMs;
    private int _passed;
    private int _suppressed;
    private long _lastSummaryMs;

    public RepeatLogLimiter(int burst, long intervalMs)
    {
        _burst = burst;
        _intervalMs = intervalMs;
    }

    /// <summary>True when the caller should log; <paramref name="suppressed"/> is the count dropped since the last line.</summary>
    public void Reset()
    {
        _passed = 0;
        _suppressed = 0;
        _lastSummaryMs = 0;
    }

    /// <summary>Returns and clears the count held back since the last emitted line.</summary>
    public int TakeSuppressed()
    {
        int n = _suppressed;
        _suppressed = 0;
        return n;
    }

    public bool TryAcquire(long nowMs, out int suppressed)
    {
        suppressed = 0;
        if (_passed < _burst)
        {
            _passed++;
            _lastSummaryMs = nowMs;
            return true;
        }
        if (nowMs - _lastSummaryMs < _intervalMs)
        {
            _suppressed++;
            return false;
        }
        suppressed = _suppressed;
        _suppressed = 0;
        _lastSummaryMs = nowMs;
        return true;
    }
}
