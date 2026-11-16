using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace Nexus.Service.Peripherals.Ryujin;

/// <summary>What the uploader needs from the cooler: its HID channel, its bulk pipe and a way to wait.</summary>
internal interface IRyujinUploadLink
{
    bool WriteReport(byte[] report);
    bool WriteChunk(ReadOnlySpan<byte> chunk);

    /// <summary>Bytes of one inbound HID report, 0 on timeout, negative when the device is gone.</summary>
    int ReadReport(Span<byte> buffer, int timeoutMs);

    void Pause(int ms);

    /// <summary>Drops any inbound report held over from an earlier attempt.</summary>
    void ClearInbound()
    {
    }
}

internal enum RyujinUploadAbort { None, Cancelled, ControlOff, DeviceFailure }

internal sealed record RyujinUploadResult(bool Ok, string Message, int Chunks, double TotalMs, RyujinUploadAbort Abort = RyujinUploadAbort.None);

/// <summary>
/// Sends one AVI as fast as the device acknowledges it, and repeats it paced like Armoury Crate when that
/// fails. Every upload writes one [ryujin-upload] summary line: it is the measurement the test is for.
/// </summary>
internal sealed class RyujinUploader
{
    public const int DefaultAckTimeoutMs = 6000;
    public const string AbortedMessage = "aborted";
    private const byte AnyReport = 0xFF;
    private const int MaxLoggedReports = 300;
    private bool _logChunkAcks;
    private long _nextKeepalive;

    /// <summary>Runs the owner's keepalive when one is due; called wherever the upload waits so the pump is never left longer than the interval.</summary>
    private void MaybeKeepalive()
    {
        if (Keepalive is null || Environment.TickCount64 < _nextKeepalive)
        {
            return;
        }
        if (_keepalives++ == 0)
        {
            _info("[ryujin-upload] keepalive poll during the upload (Armoury Crate sent none)");
        }
        Keepalive();
        _nextKeepalive = Environment.TickCount64 + KeepaliveMs;
    }

    private const int ReadSliceMs = 250;
    private Stopwatch _attemptClock = Stopwatch.StartNew();

    private double ElapsedMs() => _attemptClock.Elapsed.TotalMilliseconds;

    private readonly IRyujinUploadLink _link;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private int _logged;
    private int _keepalives;

    /// <summary>Longer than Armoury Crate's longest gap between chunks.</summary>
    public int AckTimeoutMs { get; init; } = DefaultAckTimeoutMs;

    /// <summary>Called between chunks about every <see cref="KeepaliveMs"/>; the owner keeps duty and status flowing. Armoury Crate sent no HID during an upload.</summary>
    public Action? Keepalive { get; init; }

    public int Slot { get; init; } = RyujinUploadProtocol.DefaultSlot;

    public int KeepaliveMs { get; init; } = RyujinCoolingProtocol.KeepaliveMs;

    /// <summary>Checked before each chunk; anything but None aborts the upload with that reason, decided at that moment.</summary>
    public Func<RyujinUploadAbort>? AbortCheck { get; init; }

    /// <summary>How long to listen for the device's answer to the end command.</summary>
    public int EndWaitMs { get; init; } = 5000;

    public RyujinUploader(IRyujinUploadLink link, Action<string> info, Action<string> warn)
    {
        _link = link;
        _info = info;
        _warn = warn;
    }

    public RyujinUploadResult Upload(byte[] avi, Action<int, int>? progress, CancellationToken ct)
    {
        _logged = 0;
        var fast = Attempt(avi, paced: false, progress, ct);
        if (fast.Ok || fast.Abort != RyujinUploadAbort.DeviceFailure)
        {
            return fast;
        }
        var stop = ct.IsCancellationRequested ? RyujinUploadAbort.Cancelled : AbortCheck?.Invoke() ?? RyujinUploadAbort.None;
        if (stop != RyujinUploadAbort.None)
        {
            _warn($"[ryujin-upload] fast attempt failed ({fast.Message}); no paced retry, the upload was stopped");
            return fast;
        }
        _warn($"[ryujin-upload] fast attempt failed ({fast.Message}); retrying paced like Armoury Crate");
        var paced = Attempt(avi, paced: true, progress, ct);
        return paced with { Message = paced.Ok ? "ok after paced fallback" : paced.Message };
    }

    private RyujinUploadResult Attempt(byte[] avi, bool paced, Action<int, int>? progress, CancellationToken ct)
    {
        string mode = paced ? "paced" : "fast";
        int total = RyujinUploadProtocol.ChunkCount(avi.Length);
        var clock = Stopwatch.StartNew();
        _attemptClock = clock;
        _keepalives = 0;
        _link.ClearInbound();
        _nextKeepalive = Environment.TickCount64 + KeepaliveMs;
        Drain();
        _info($"[ryujin-upload] start mode={mode} bytes={avi.Length} chunks={total}");

        var begin = RyujinUploadProtocol.EncodeBegin(avi.Length, Slot);
        for (int i = 0; i < begin.Length; i++)
        {
            if (!_link.WriteReport(begin[i]))
            {
                return Fail(mode, "command write failed", 0, clock);
            }
            bool last = i == begin.Length - 1;
            if (paced || !last)
            {
                _link.Pause(RyujinUploadProtocol.BeginGapsMs[i]);
            }
            else
            {
                WaitFor(RyujinUploadProtocol.AckErase, RyujinUploadProtocol.BeginGapsMs[i], out _);
            }
        }

        double ackMin = double.MaxValue, ackMax = 0, ackSum = 0;
        int acks = 0, sent = 0;
        foreach (var chunk in RyujinUploadProtocol.Chunks(avi))
        {
            var abort = ct.IsCancellationRequested ? RyujinUploadAbort.Cancelled : AbortCheck?.Invoke() ?? RyujinUploadAbort.None;
            if (abort != RyujinUploadAbort.None)
            {
                return Fail(mode, AbortedMessage, sent, clock, abort);
            }
            MaybeKeepalive();
            long wrote = Stopwatch.GetTimestamp();
            if (!_link.WriteChunk(chunk))
            {
                return Fail(mode, $"bulk write failed at chunk {sent}", sent, clock);
            }
            sent++;
            if (paced)
            {
                Drain();
                _link.Pause(sent % RyujinUploadProtocol.PacedGroupChunks == 0 ? RyujinUploadProtocol.PacedGroupGapMs : RyujinUploadProtocol.PacedChunkGapMs);
            }
            else if (WaitChunkAck(sent))
            {
                double ms = Stopwatch.GetElapsedTime(wrote).TotalMilliseconds;
                ackMin = Math.Min(ackMin, ms);
                ackMax = Math.Max(ackMax, ms);
                ackSum += ms;
                acks++;
            }
            else
            {
                return Fail(mode, $"no chunk ack within {AckTimeoutMs} ms at chunk {sent - 1}", sent, clock);
            }
            progress?.Invoke(sent, total);
        }

        if (!_link.WriteReport(RyujinUploadProtocol.EncodeEnd()))
        {
            return Fail(mode, "end command write failed", sent, clock);
        }
        // Armoury Crate followed the end command with DC and 99 shortly after; every reply in the window is logged.
        Listen(Math.Min(600, EndWaitMs));
        _link.WriteReport(RyujinMonitorProtocol.EncodeDc());
        _link.WriteReport(RyujinCoolingProtocol.EncodeStatusRequest());
        Listen(EndWaitMs);

        double totalMs = clock.Elapsed.TotalMilliseconds;
        double kbps = avi.Length / 1024.0 / (totalMs / 1000.0);
        string ack = acks > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{ackMin:0.0}/{ackSum / acks:0.0}/{ackMax:0.0}")
            : "none";
        _info(string.Create(CultureInfo.InvariantCulture,
            $"[ryujin-upload] done mode={mode} bytes={avi.Length} chunks={sent} total_ms={totalMs:0} kb_per_s={kbps:0.0} ack_ms_min_avg_max={ack} acks={acks} keepalives={_keepalives}"));
        return new RyujinUploadResult(true, "ok", sent, totalMs);
    }

    private bool WaitChunkAck(int sent)
    {
        _logChunkAcks = sent <= 3 || sent % RyujinUploadProtocol.PacedGroupChunks == 0;
        return WaitFor(RyujinUploadProtocol.AckChunk, AckTimeoutMs, out _);
    }

    /// <summary>Reads and logs every inbound report for the given time.</summary>
    private void Listen(int ms)
    {
        var buffer = new byte[RyujinCoolingProtocol.ReportLength];
        _logChunkAcks = true;
        long deadline = Stopwatch.GetTimestamp() + (long)(ms * (Stopwatch.Frequency / 1000.0));
        while (true)
        {
            int left = (int)((deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
            if (left <= 0)
            {
                return;
            }
            MaybeKeepalive();
            int n = _link.ReadReport(buffer, Math.Min(left, ReadSliceMs));
            if (n < 0)
            {
                return;
            }
            if (n > 0)
            {
                LogInbound(buffer.AsSpan(0, n));
            }
        }
    }

    /// <summary>Reads inbound reports until one carries the wanted event (or any report for <see cref="AnyReport"/>).</summary>
    private bool WaitFor(byte kind, int timeoutMs, out int length)
    {
        length = 0;
        var buffer = new byte[RyujinCoolingProtocol.ReportLength];
        long deadline = Stopwatch.GetTimestamp() + (long)(timeoutMs * (Stopwatch.Frequency / 1000.0));
        while (true)
        {
            int left = (int)((deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
            if (left <= 0)
            {
                return false;
            }
            MaybeKeepalive();
            int n = _link.ReadReport(buffer, Math.Min(left, ReadSliceMs));
            if (n < 0)
            {
                return false;
            }
            if (n == 0)
            {
                continue;
            }
            if (_logChunkAcks || RyujinUploadProtocol.AckKind(buffer.AsSpan(0, n)) != RyujinUploadProtocol.AckChunk)
            {
                LogInbound(buffer.AsSpan(0, n));
            }
            if (kind == AnyReport || RyujinUploadProtocol.AckKind(buffer.AsSpan(0, n)) == kind)
            {
                length = n;
                return true;
            }
        }
    }

    private void Drain()
    {
        var buffer = new byte[RyujinCoolingProtocol.ReportLength];
        for (int i = 0; i < 16; i++)
        {
            int n = _link.ReadReport(buffer, 0);
            if (n <= 0)
            {
                return;
            }
            if (RyujinUploadProtocol.AckKind(buffer.AsSpan(0, n)) != RyujinUploadProtocol.AckChunk)
            {
                LogInbound(buffer.AsSpan(0, n));
            }
        }
    }

    private void LogInbound(ReadOnlySpan<byte> report)
    {
        if (_logged < MaxLoggedReports)
        {
            _logged++;
            _info($"[ryujin-upload] inbound {Convert.ToHexString(report[..Math.Min(report.Length, 24)])} t_ms={ElapsedMs():0}");
        }
    }

    private RyujinUploadResult Fail(string mode, string message, int sent, Stopwatch clock, RyujinUploadAbort abort = RyujinUploadAbort.DeviceFailure)
    {
        _warn($"[ryujin-upload] failed mode={mode} chunks_sent={sent} total_ms={clock.Elapsed.TotalMilliseconds:0} reason={message}");
        return new RyujinUploadResult(false, message, sent, clock.Elapsed.TotalMilliseconds, abort);
    }
}

/// <summary>The real link: HID writes go through the shared write gate, chunks go out on the bulk pipe.</summary>
internal sealed class RyujinHidBulkLink : IRyujinUploadLink
{
    private readonly Nexus.Service.Peripherals.Hid.IHidDevice _hid;
    private readonly Nexus.Service.Peripherals.BulkPanels.IBulkUsbPipe _pipe;
    private readonly Func<byte[]?>? _stashed;

    public RyujinHidBulkLink(Nexus.Service.Peripherals.Hid.IHidDevice hid, Nexus.Service.Peripherals.BulkPanels.IBulkUsbPipe pipe, Func<byte[]?>? stashed = null)
    {
        _hid = hid;
        _pipe = pipe;
        _stashed = stashed;
    }

    public bool WriteReport(byte[] report)
    {
        lock (Nexus.Service.Peripherals.BulkPanels.RyujinProtocol.HidWriteGate)
        {
            return _hid.Write(report);
        }
    }

    public bool WriteChunk(ReadOnlySpan<byte> chunk) =>
        _pipe.WriteCounted(RyujinUploadProtocol.BulkWritePipe, chunk) == chunk.Length;

    public int ReadReport(Span<byte> buffer, int timeoutMs)
    {
        if (_stashed?.Invoke() is { } ack)
        {
            ack.AsSpan(0, Math.Min(ack.Length, buffer.Length)).CopyTo(buffer);
            return Math.Min(ack.Length, buffer.Length);
        }
        return _hid.Read(buffer, timeoutMs);
    }

    public void Pause(int ms) => Thread.Sleep(ms);

    public void ClearInbound()
    {
        while (_stashed?.Invoke() is not null)
        {
        }
    }
}
