using System;
using System.Diagnostics;
using System.Text;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// The ASRock LCD's connect / real-time-display / disconnect exchange.
///
/// Two things make this panel unlike the rest of the family: it answers control messages,
/// so a write alone is not evidence, and it reports a boot state - frames sent before
/// <c>bootFinish</c> are accepted and dropped. Real-time display is re-asserted every ten
/// seconds, which the documented behaviour calls for and this does not assume unnecessary.
/// </summary>
public sealed class AsRockLcdHandshake : IJpegPanelHandshake
{
    /// <summary>Documented cadence for re-enabling the real-time display.</summary>
    private const int RealtimeReassertMs = 10_000;

    private const int ReplyTimeoutMs = 300;

    // Every exchange of the first few is logged in full; afterwards only failures, rate limited.
    private const int VerboseExchanges = 4;

    private readonly RepeatLogLimiter _anomalyLog = new(burst: 5, intervalMs: 60_000);
    private int _verboseLeft = VerboseExchanges;
    private int _sequence;
    private bool _booted;
    private long _lastRealtimeMs;

    public bool OnAttach(IHidDevice device, int reportLength)
    {
        _booted = false;
        _lastRealtimeMs = 0;

        var reply = Exchange(device, reportLength, "POST", "conn", null);
        if (reply is null)
        {
            ServiceLog.Warn("[asrock-lcd] no reply to connect; dropping the handle");
            return false;
        }
        if (AsRockLcdProtocol.ParseStatus(reply) != "200")
        {
            ServiceLog.Warn("[asrock-lcd] connect refused by the panel");
            return false;
        }

        // A healthy connect re-arms full logging; a panel that never answers keeps the limiter across retries.
        FlushSuppressed();
        _anomalyLog.Reset();
        _verboseLeft = VerboseExchanges;

        _booted = AsRockLcdProtocol.ParseBootFinished(reply);
        if (!_booted)
        {
            // Not a failure: the panel answers before it has finished booting, and
            // BeforeFrame retries until it says otherwise.
            ServiceLog.Info("[asrock-lcd] connected, waiting for the panel to finish booting");
        }
        return true;
    }

    public void OnDetach(IHidDevice device, int reportLength)
    {
        Exchange(device, reportLength, "POST", "realtimeDisplay", "{\"enable\":false}");
        Exchange(device, reportLength, "POST", "disconn", null);
        FlushSuppressed();
    }

    public bool BeforeFrame(IHidDevice device, int reportLength, long nowMs)
    {
        if (!_booted)
        {
            // Re-ask at the keepalive cadence rather than every frame; the panel takes
            // seconds to boot and the exchange costs a read timeout each time.
            if (nowMs - _lastRealtimeMs < RealtimeReassertMs)
            {
                return false;
            }
            _lastRealtimeMs = nowMs;
            var reply = Exchange(device, reportLength, "POST", "conn", null);
            _booted = reply is not null && AsRockLcdProtocol.ParseBootFinished(reply);
            if (!_booted)
            {
                return false;
            }
            _lastRealtimeMs = 0;
        }

        if (nowMs - _lastRealtimeMs < RealtimeReassertMs)
        {
            return true;
        }
        _lastRealtimeMs = nowMs;
        var enabled = Exchange(device, reportLength, "POST", "realtimeDisplay", "{\"enable\":true}");
        if (enabled is null || AsRockLcdProtocol.ParseStatus(enabled) != "200")
        {
            ServiceLog.Warn("[asrock-lcd] real-time display was not accepted; frames may be ignored");
        }
        return true;
    }

    private byte[]? Exchange(IHidDevice device, int reportLength, string method, string command, string? jsonBody)
    {
        var text = AsRockLcdProtocol.BuildMessage(
            method, command, _sequence++, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), jsonBody);
        var framed = AsRockLcdProtocol.Encode(Encoding.ASCII.GetBytes(text));

        var report = new byte[reportLength];
        if (framed.Length > report.Length)
        {
            ServiceLog.Warn($"[asrock-lcd] {command} message does not fit one report");
            return null;
        }
        framed.CopyTo(report, 0);

        long started = Stopwatch.GetTimestamp();
        int read = 0;
        byte[]? decoded = null;
        bool wrote = device.Write(report);
        if (wrote)
        {
            var buffer = new byte[reportLength];
            read = device.Read(buffer, ReplyTimeoutMs);
            if (read > 0)
            {
                decoded = AsRockLcdProtocol.Decode(buffer.AsSpan(0, read));
            }
        }
        long elapsedMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        var outcome = AsRockLcdExchangeLog.Classify(wrote, read, decoded, out var status);
        LogExchange(device, command, outcome, status, elapsedMs, decoded);
        return decoded is { Length: > 0 } ? decoded : null;
    }

    private void FlushSuppressed()
    {
        int pending = _anomalyLog.TakeSuppressed();
        if (pending > 0)
        {
            ServiceLog.Warn(AsRockLcdExchangeLog.FormatSuppressed(pending));
        }
    }

    private void LogExchange(
        IHidDevice device, string command, AsRockExchangeOutcome outcome, string? status, long elapsedMs, byte[]? decoded)
    {
        bool verbose = _verboseLeft > 0;
        if (verbose)
        {
            _verboseLeft--;
        }
        if (outcome == AsRockExchangeOutcome.Ok)
        {
            FlushSuppressed();
            if (!verbose)
            {
                return;
            }
        }
        else if (!verbose)
        {
            if (!_anomalyLog.TryAcquire(Environment.TickCount64, out int suppressed))
            {
                return;
            }
            if (suppressed > 0)
            {
                ServiceLog.Warn(AsRockLcdExchangeLog.FormatSuppressed(suppressed));
            }
        }
        var line = AsRockLcdExchangeLog.Format(
            command, outcome, status, elapsedMs, decoded, device.LastWriteError, ReplyTimeoutMs);
        if (outcome == AsRockExchangeOutcome.Ok)
        {
            ServiceLog.Info(line);
        }
        else
        {
            ServiceLog.Warn(line);
        }
    }
}
