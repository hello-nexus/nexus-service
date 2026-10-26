using System;
using System.Threading;
using Nexus.Service.Persistence;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.DeepCool;

/// <summary>The source a stream leaves the screen on, the stored GIF it picks, and whether the source command for it is sent.</summary>
public readonly record struct MystiqueSourceAfter(string Source, bool Send, int GifIndex = 0);

/// <summary>
/// One open pipe to a Mystique: the opening exchange, the settings groups the user has
/// changed, the clock, and the poll plus sensor push that keeps the screen on live data.
/// Every write is answered on the matching IN pipe. Stale input is drained when the session
/// opens, a reply that does not match the write is discarded, and a run of unanswered writes
/// fails the session.
/// </summary>
public sealed class MystiqueSession
{
    private const int ReplyTimeoutMs = 300;
    private const int MaxUnanswered = 5;
    private const int DrainTimeoutMs = 10;
    private const int MaxDrainReads = 16;
    private const long ClockEveryMs = 60L * 60 * 1000;

    // The screen may still be storing the item when the source command follows the finish packet; a
    // miss is resent by the next Apply.
    private const int SourceReplyTimeoutMs = 2000;

    // The screen sends no ready event after a picture, so the next command waits as long as the screen's own
    // software does (capture 2), which follows a GIF far sooner.
    private const int AfterPictureGapMs = 410;

    // A whole number of the endpoint's packets, which the transport splits each write into; WinUSB adds no
    // zero-length packet without SHORT_PACKET_TERMINATE. Each frame's header goes in a write of its own first, as the
    // screen's own software sends it (capture 4).
    private const int StreamChunkBytes = 16 * 1024;

    // A stream with no write taken for this long has stalled; shorter stalls have ended on their own.
    internal const int StreamStallMs = 30_000;

    // Recovery writes stop this long after a failure, so a stalled screen frees the worker; a recorded
    // picture frame streamed in about half of it, the screen's stall included.
    internal const int RecoveryBudgetMs = 2000;

    private readonly IBulkUsbPipe _pipe;
    private readonly Func<long> _monotonicMs;
    private readonly Func<DateTime, TimeSpan> _utcOffset;
    private readonly Action<int> _sleep;
    private DeepCoolMystiqueSettings? _applied;
    private long _clockSentMs;
    private TimeSpan _clockOffset;
    private int _unanswered;
    private int _gifIndex;
    private readonly CancellationToken _stop;

    /// <param name="monotonicMs">Clock for the resend schedule; wall-clock steps must not move it.</param>
    /// <param name="utcOffset">Local UTC offset at a given local time, to resend when a time zone or DST change shifts it.</param>
    /// <param name="stop">Ends a stream early, which is then closed off as a failed one is.</param>
    public MystiqueSession(
        IBulkUsbPipe pipe, Func<long>? monotonicMs = null, Func<DateTime, TimeSpan>? utcOffset = null, Action<int>? sleep = null,
        CancellationToken stop = default)
    {
        _pipe = pipe;
        _stop = stop;
        _monotonicMs = monotonicMs ?? (() => Environment.TickCount64);
        _utcOffset = utcOffset ?? (t => TimeZoneInfo.Local.GetUtcOffset(t));
        _sleep = sleep ?? System.Threading.Thread.Sleep;
    }

    /// <summary>The identity string the device returned to the opening query, or empty.</summary>
    public string Info { get; private set; } = "";

    /// <summary>Opening exchange, then the groups the user has set, then the clock. False when the link is unusable.</summary>
    public bool Open(DeepCoolMystiqueSettings settings, DateTime localNow)
    {
        // A halt the previous owner left rejects every transfer on that pipe until cleared.
        foreach (var pipe in new[] { MystiqueProtocol.SettingsWritePipe, MystiqueProtocol.SettingsReadPipe })
        {
            _pipe.ClearHalt(pipe);
        }
        Drain(MystiqueProtocol.SettingsReadPipe);
        Drain(MystiqueProtocol.LiveReadPipe);
        if (!Exchange(MystiqueProtocol.SettingsWritePipe, MystiqueProtocol.SettingsReadPipe, MystiqueProtocol.BuildInfoQuery(), out var reply))
        {
            return false;
        }
        Info = ReadInfo(reply);
        return Apply(settings) && SendClock(localNow);
    }

    /// <summary>Sends each edited group that is new or changed since the last send, in the order the screen's own software does.</summary>
    public bool Apply(DeepCoolMystiqueSettings s)
    {
        var last = _applied;
        byte rgb = MystiqueProtocol.RgbSourceCode(s.RgbSync, s.EdgePicking);
        if (s.DeviceEdited
            && (last is null || !last.DeviceEdited || last.Gyroscope != s.Gyroscope
                || MystiqueProtocol.RgbSourceCode(last.RgbSync, last.EdgePicking) != rgb || last.Brightness != s.Brightness)
            && !Settings(MystiqueProtocol.BuildSystem(s.Gyroscope, rgb, s.Brightness)))
        {
            return false;
        }
        if (s.ModeEdited && (last is null || !last.ModeEdited || last.Mode != s.Mode)
            && !Settings(MystiqueProtocol.BuildMode(s.Mode)))
        {
            return false;
        }
        if (s.ReadoutEdited
            && (last is null || !last.ReadoutEdited || last.Readout != s.Readout || last.ReadoutStyle != s.ReadoutStyle || last.AuxArea != s.AuxArea)
            && !Settings(MystiqueProtocol.BuildReadout(s.Readout, s.ReadoutStyle, s.AuxArea)))
        {
            return false;
        }
        if (s.MediaEdited && (last is null || !last.MediaEdited || last.SwitchSeconds != s.SwitchSeconds || last.Animation != s.Animation)
            && !Settings(MystiqueProtocol.BuildMediaPlayback(s.SwitchSeconds, s.Animation)))
        {
            return false;
        }
        int gifIndex = s.ShownGif.Length > 0 && s.GifOrder.IndexOf(s.ShownGif) is >= 0 and var at ? at : _gifIndex;
        bool gifMoved = s.MediaSource == DeepCoolMystiqueSettings.SourceGif && gifIndex != _gifIndex;
        if (s.SourceEdited && (last is null || !last.SourceEdited || last.MediaSource != s.MediaSource || gifMoved))
        {
            if (!Settings(MystiqueProtocol.BuildMediaSource(s.MediaSource, gifIndex)))
            {
                return false;
            }
            SentGif(s.MediaSource, gifIndex);
        }
        if (s.RecordingEdited && (last is null || !last.RecordingEdited || last.RecordFrequency != s.RecordFrequency || last.RecordTemperature != s.RecordTemperature)
            && !(Settings(MystiqueProtocol.BuildRecording(s.RecordFrequency, s.RecordTemperature)) && Settings(MystiqueProtocol.BuildRecordingApply())))
        {
            return false;
        }
        _applied = s.Copy();
        return true;
    }

    /// <summary>
    /// Streams a picture or GIF as the screen's own software does: the announce command, every frame,
    /// the finish packet, then the source command. <paramref name="source"/> runs once the stream is
    /// finished and names the source to send, null for none; without it the uploaded kind is sent.
    /// Nothing streams unless the screen answered the announce. True once a clean stream is finished,
    /// whether or not the source command was answered.
    /// </summary>
    public bool Upload(MystiqueMediaPackage package, Func<MystiqueSourceAfter?>? source = null, Action<long>? progress = null) =>
        Upload(new[] { package }, source, progress) == 1;

    /// <summary>
    /// Streams the packages in turn, each as <see cref="Upload(MystiqueMediaPackage, Func{string?}?, Action{long}?)"/>
    /// does, and the source command once after the last. Returns how many were streamed cleanly, stopping at the
    /// first that was not; the source command follows only when all were. <paramref name="progress"/> gets the
    /// bytes streamed across all of them.
    /// </summary>
    public int Upload(IReadOnlyList<MystiqueMediaPackage> packages, Func<MystiqueSourceAfter?>? source = null, Action<long>? progress = null)
    {
        long streamed = 0;
        long started = _monotonicMs();
        for (int p = 0; p < packages.Count; p++)
        {
            if (_stop.IsCancellationRequested)
            {
                return p;
            }
            if (p > 0 && packages[p - 1].Kind == MystiqueMedia.KindPicture)
            {
                _sleep(AfterPictureGapMs);
            }
            if (!Stream(packages[p], ref streamed, progress))
            {
                return p;
            }
        }
        ServiceLog.Info($"[deepcool-mystique] streamed {streamed} bytes in {_monotonicMs() - started} ms");
        var last = packages[^1];
        string kind = last.Kind == MystiqueMedia.KindGif ? DeepCoolMystiqueSettings.SourceGif : DeepCoolMystiqueSettings.SourceImages;
        var after = source is null ? new MystiqueSourceAfter(kind, true) : source();
        ServiceLog.Info($"[deepcool-mystique] finish sent; source after it: {(after is { } a ? a.Source + (a.Send ? "" : ", not sent") : "none")}");
        if (after is { Send: true } && last.Kind == MystiqueMedia.KindPicture)
        {
            _sleep(AfterPictureGapMs);
        }
        SendSourceAfter(after);
        return packages.Count;
    }

    /// <summary>Sends the source <paramref name="after"/> names, or counts it as applied when it is not to be sent; false when a sent one was not answered.</summary>
    private bool SendSourceAfter(MystiqueSourceAfter? after)
    {
        if (after is { Send: true } send)
        {
            return SendSource(send.Source, send.GifIndex);
        }
        if (after is { } kept && _applied is not null)
        {
            _applied.MediaSource = kept.Source;
            _applied.SourceEdited = true;
            SentGif(kept.Source, kept.GifIndex);
        }
        return true;
    }

    private void SentGif(string source, int gifIndex)
    {
        if (source == DeepCoolMystiqueSettings.SourceGif)
        {
            _gifIndex = gifIndex;
        }
    }

    /// <summary>Sends the source command; true when the screen answered it.</summary>
    internal bool SendSource(string source, int gifIndex = 0)
    {
        var command = MystiqueProtocol.BuildMediaSource(source, gifIndex);
        SentGif(source, gifIndex);
        bool delivered = Exchange(MystiqueProtocol.SettingsWritePipe, MystiqueProtocol.SettingsReadPipe, command,
            out var reply, out bool answered, SourceReplyTimeoutMs);
        LogSetting(command, delivered && answered);
        if (delivered && answered)
        {
            ServiceLog.Info($"[deepcool-mystique] source reply {Convert.ToHexString(reply, 0, 9)}");
        }
        if (_applied is not null)
        {
            _applied.MediaSource = source;
            _applied.SourceEdited = delivered && answered;
        }
        return delivered && answered;
    }

    private bool Stream(MystiqueMediaPackage package, ref long streamed, Action<long>? progress)
    {
        string kind = package.Kind == MystiqueMedia.KindGif ? DeepCoolMystiqueSettings.SourceGif : DeepCoolMystiqueSettings.SourceImages;
        ServiceLog.Info($"[deepcool-mystique] upload {kind} {package.Id}, {package.Frames.Count} frames, largest {package.Frames.Max(f => f.Length)} bytes");
        if (!Exchange(MystiqueProtocol.SettingsWritePipe, MystiqueProtocol.SettingsReadPipe, MystiqueProtocol.BuildUploadStart(), out _, out bool announced)
            || !announced)
        {
            ServiceLog.Warn("[deepcool-mystique] upload refused: the screen did not answer the announce");
            return false;
        }
        long tookAt = _monotonicMs();
        for (int f = 0; f < package.Frames.Count; f++)
        {
            var frame = package.Frames[f];
            int sent = 0;
            while (sent < frame.Length)
            {
                if (_stop.IsCancellationRequested)
                {
                    AbandonStream(f, frame.Length, streamed, frame.Length - sent, stopped: true);
                    return false;
                }
                int length = sent == 0 ? MystiqueMedia.HeaderLength : Math.Min(StreamChunkBytes, frame.Length - sent);
                int landed = WriteChunk(frame.AsSpan(sent, length), tookAt + StreamStallMs);
                if (landed != length)
                {
                    AbandonStream(f, frame.Length, streamed, frame.Length - sent - Math.Max(landed, 0), stopped: _stop.IsCancellationRequested);
                    return false;
                }
                sent += landed;
                streamed += landed;
                tookAt = _monotonicMs();
                progress?.Invoke(streamed);
            }
        }
        var finish = MystiqueMedia.FinishPacket();
        int took = _pipe.WriteCounted(MystiqueProtocol.SettingsWritePipe, finish, StreamStallMs, _stop);
        if (took == 0 && _stop.IsCancellationRequested)
        {
            took = _pipe.WriteCounted(MystiqueProtocol.SettingsWritePipe, finish);
        }
        return took == finish.Length;
    }

    /// <summary>
    /// One stream write, resent once only when the transport knows none of it went out, so the stream
    /// keeps matching its headers, and only before <paramref name="stallAt"/>. Returns the bytes the screen took, or -1 when unknown.
    /// </summary>
    private int WriteChunk(ReadOnlySpan<byte> chunk, long stallAt)
    {
        int landed = _pipe.WriteCounted(MystiqueProtocol.SettingsWritePipe, chunk, StreamStallMs, _stop);
        long left = stallAt - _monotonicMs();
        if (landed == 0 && !_stop.IsCancellationRequested && left > 0)
        {
            landed = _pipe.WriteCounted(MystiqueProtocol.SettingsWritePipe, chunk, (int)left, _stop);
        }
        return landed;
    }

    /// <summary>
    /// Completes the failed frame with zeros and sends the finish packet, so the screen meets a failing byte
    /// sum (assumed, unverified, to drop the item and return to commands) instead of reading the reopen's
    /// commands as stream. Later GIF frames are skipped: only their own headers carry their lengths. After a transfer
    /// error or a whole <see cref="StreamStallMs"/> a halt the failed transfer left would refuse every fill write,
    /// so it is cleared first; a <paramref name="stopped"/> stream left no halt.
    /// </summary>
    private void AbandonStream(int frameIndex, int frameLength, long streamed, int remaining, bool stopped)
    {
        long deadline = _monotonicMs() + RecoveryBudgetMs;
        if (!stopped)
        {
            _pipe.ClearHalt(MystiqueProtocol.SettingsWritePipe);
        }
        bool finished = false;
        bool filled = true;
        foreach (var zeros in MystiqueMedia.Packets(new byte[Math.Max(remaining, 0)]))
        {
            if (_monotonicMs() >= deadline || RecoveryWrite(zeros, deadline) != zeros.Length)
            {
                filled = false;
                break;
            }
        }
        if (filled && _monotonicMs() < deadline)
        {
            var finish = MystiqueMedia.FinishPacket();
            finished = RecoveryWrite(finish, deadline) == finish.Length;
        }
        string where = $"in frame {frameIndex} ({frameLength} bytes, {streamed} streamed)";
        ServiceLog.Warn(finished
            ? $"[deepcool-mystique] upload {(stopped ? "stopped" : "failed")} {where}; filled {remaining} bytes with zeros and sent the finish packet"
            : $"[deepcool-mystique] upload {(stopped ? "stopped" : "failed")} {where}; the pipe stopped taking writes, so the screen may stay in upload mode until power cycled");
    }

    private int RecoveryWrite(byte[] data, long deadline) =>
        _pipe.WriteCounted(MystiqueProtocol.SettingsWritePipe, data, (int)Math.Max(1, deadline - _monotonicMs()), CancellationToken.None);

    /// <summary>Erases every stored picture and GIF; true only when the screen answered.</summary>
    public bool Format() =>
        Exchange(MystiqueProtocol.SettingsWritePipe, MystiqueProtocol.SettingsReadPipe, MystiqueProtocol.BuildFormat(), out _, out bool answered)
        && answered;

    public bool SendClock(DateTime localNow)
    {
        if (!Exchange(MystiqueProtocol.LiveWritePipe, MystiqueProtocol.LiveReadPipe, MystiqueProtocol.BuildClock(localNow), out _))
        {
            return false;
        }
        _clockSentMs = _monotonicMs();
        _clockOffset = _utcOffset(localNow);
        return true;
    }

    /// <summary>One cycle: status poll, then the live values; the clock again on a schedule or when the UTC offset changes.</summary>
    public bool Tick(MystiqueReading reading, DateTime localNow)
    {
        if (!Exchange(MystiqueProtocol.LiveWritePipe, MystiqueProtocol.LiveReadPipe, MystiqueProtocol.BuildPoll(), out _)
            || !Exchange(MystiqueProtocol.LiveWritePipe, MystiqueProtocol.LiveReadPipe, MystiqueProtocol.BuildSensors(reading), out _))
        {
            return false;
        }
        bool due = _monotonicMs() - _clockSentMs >= ClockEveryMs || _utcOffset(localNow) != _clockOffset;
        return !due || SendClock(localNow);
    }

    private bool Settings(byte[] frame)
    {
        bool ok = Exchange(MystiqueProtocol.SettingsWritePipe, MystiqueProtocol.SettingsReadPipe, frame, out _, out bool answered);
        LogSetting(frame, ok && answered);
        return ok;
    }

    /// <summary>Logs the rarely sent commands that decide what the screen shows, for support bundles.</summary>
    private static void LogSetting(byte[] frame, bool answered)
    {
        if (frame[2] is MystiqueProtocol.CmdSystem or MystiqueProtocol.CmdMode or MystiqueProtocol.CmdMediaPlayback or MystiqueProtocol.CmdMediaSource)
        {
            ServiceLog.Info($"[deepcool-mystique] sent 0x{frame[2]:X2} {Convert.ToHexString(frame, 3, 5)}, {(answered ? "answered" : "no reply")}");
        }
    }

    private bool Exchange(byte writePipe, byte readPipe, byte[] frame, out byte[] reply) =>
        Exchange(writePipe, readPipe, frame, out reply, out _);

    /// <param name="answered">The screen replied, as opposed to a tolerated silence.</param>
    private bool Exchange(byte writePipe, byte readPipe, byte[] frame, out byte[] reply, out bool answered, int replyTimeoutMs = ReplyTimeoutMs)
    {
        answered = false;
        reply = new byte[64];
        if (!_pipe.Write(writePipe, frame))
        {
            return false;
        }
        long deadline = _monotonicMs() + replyTimeoutMs;
        long remaining = replyTimeoutMs;
        while (remaining > 0)
        {
            int n = _pipe.Read(readPipe, reply, (int)remaining);
            if (n < 0)
            {
                return false;
            }
            if (n == 0)
            {
                break;
            }
            if (n >= MystiqueProtocol.FrameLength && MystiqueProtocol.IsReply(reply, frame[2]))
            {
                _unanswered = 0;
                answered = true;
                return true;
            }
            remaining = deadline - _monotonicMs();
        }
        return ++_unanswered < MaxUnanswered;
    }

    private void Drain(byte readPipe)
    {
        var scratch = new byte[64];
        for (int i = 0; i < MaxDrainReads && _pipe.Read(readPipe, scratch, DrainTimeoutMs) > 0; i++)
        {
        }
    }

    private static string ReadInfo(byte[] reply)
    {
        if (!MystiqueProtocol.IsReply(reply, MystiqueProtocol.CmdInfo))
        {
            return "";
        }
        int end = 4;
        while (end < 42 && reply[end] >= 0x20 && reply[end] < 0x7F)
        {
            end++;
        }
        return System.Text.Encoding.ASCII.GetString(reply, 4, end - 4);
    }
}
