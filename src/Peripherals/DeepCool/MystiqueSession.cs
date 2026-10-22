using System;
using Nexus.Service.Persistence;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.DeepCool;

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

    // Recovery writes stop this long after a failure, so a stalled screen frees the worker; a recorded
    // picture frame streamed in about half of it, the screen's stall included.
    private const int RecoveryBudgetMs = 2000;

    private readonly IBulkUsbPipe _pipe;
    private readonly Func<long> _monotonicMs;
    private readonly Func<DateTime, TimeSpan> _utcOffset;
    private DeepCoolMystiqueSettings? _applied;
    private long _clockSentMs;
    private TimeSpan _clockOffset;
    private int _unanswered;

    /// <param name="monotonicMs">Clock for the resend schedule; wall-clock steps must not move it.</param>
    /// <param name="utcOffset">Local UTC offset at a given local time, to resend when a time zone or DST change shifts it.</param>
    public MystiqueSession(IBulkUsbPipe pipe, Func<long>? monotonicMs = null, Func<DateTime, TimeSpan>? utcOffset = null)
    {
        _pipe = pipe;
        _monotonicMs = monotonicMs ?? (() => Environment.TickCount64);
        _utcOffset = utcOffset ?? (t => TimeZoneInfo.Local.GetUtcOffset(t));
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
        if (s.SourceEdited && (last is null || !last.SourceEdited || last.MediaSource != s.MediaSource)
            && !Settings(MystiqueProtocol.BuildMediaSource(s.MediaSource)))
        {
            return false;
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
    /// Streams a picture or GIF as the screen's own software does: the announce command, every frame as
    /// 64-byte writes, the finish packet, then the source command. <paramref name="source"/> runs once the
    /// stream is finished and names the source to send, null for none; without it the uploaded kind is sent.
    /// Nothing streams unless the screen answered the announce. True once a clean stream is finished,
    /// whether or not the source command is answered; an unanswered one is re-sent by the next <see cref="Apply"/>.
    /// </summary>
    public bool Upload(MystiqueMediaPackage package, Func<string?>? source = null)
    {
        string kind = package.Kind == MystiqueMedia.KindGif ? DeepCoolMystiqueSettings.SourceGif : DeepCoolMystiqueSettings.SourceImages;
        ServiceLog.Info($"[deepcool-mystique] upload {kind} {package.Id}, {package.Frames.Count} frames");
        if (!Exchange(MystiqueProtocol.SettingsWritePipe, MystiqueProtocol.SettingsReadPipe, MystiqueProtocol.BuildUploadStart(), out _, out bool announced)
            || !announced)
        {
            ServiceLog.Warn("[deepcool-mystique] upload refused: the screen did not answer the announce");
            return false;
        }
        for (int f = 0; f < package.Frames.Count; f++)
        {
            var frame = package.Frames[f];
            int sent = 0;
            foreach (var packet in MystiqueMedia.Packets(frame))
            {
                int landed = WritePacket(packet);
                if (landed != packet.Length)
                {
                    AbandonStream(f, frame.Length - sent - Math.Max(landed, 0));
                    return false;
                }
                sent += landed;
            }
        }
        var finish = MystiqueMedia.FinishPacket();
        if (_pipe.WriteCounted(MystiqueProtocol.SettingsWritePipe, finish) != finish.Length)
        {
            return false;
        }
        string? chosen = source is null ? kind : source();
        if (chosen is null)
        {
            return true;
        }
        var command = MystiqueProtocol.BuildMediaSource(chosen);
        bool delivered = Exchange(MystiqueProtocol.SettingsWritePipe, MystiqueProtocol.SettingsReadPipe, command,
            out _, out bool answered, SourceReplyTimeoutMs);
        LogSetting(command, delivered && answered);
        if (_applied is not null)
        {
            _applied.MediaSource = chosen;
            _applied.SourceEdited = delivered && answered;
        }
        return true;
    }

    /// <summary>
    /// One stream packet, resent once only when the transport knows none of it went out, so the stream
    /// keeps matching its headers. Returns the bytes the screen took, or -1 when unknown.
    /// </summary>
    private int WritePacket(byte[] packet)
    {
        int landed = _pipe.WriteCounted(MystiqueProtocol.SettingsWritePipe, packet);
        if (landed == 0)
        {
            landed = _pipe.WriteCounted(MystiqueProtocol.SettingsWritePipe, packet);
        }
        return landed;
    }

    /// <summary>
    /// Completes the failed frame with zeros and sends the finish packet, so the screen meets a failing byte
    /// sum (assumed, unverified, to drop the item and return to commands) instead of reading the reopen's
    /// commands as stream. Later GIF frames are skipped: only their own headers carry their lengths. A halt
    /// the failed transfer left would refuse every fill write, so it is cleared first.
    /// </summary>
    private void AbandonStream(int frameIndex, int remaining)
    {
        long deadline = _monotonicMs() + RecoveryBudgetMs;
        _pipe.ClearHalt(MystiqueProtocol.SettingsWritePipe);
        bool finished = false;
        bool filled = true;
        foreach (var zeros in MystiqueMedia.Packets(new byte[Math.Max(remaining, 0)]))
        {
            if (_monotonicMs() >= deadline || _pipe.WriteCounted(MystiqueProtocol.SettingsWritePipe, zeros) != zeros.Length)
            {
                filled = false;
                break;
            }
        }
        if (filled && _monotonicMs() < deadline)
        {
            var finish = MystiqueMedia.FinishPacket();
            finished = _pipe.WriteCounted(MystiqueProtocol.SettingsWritePipe, finish) == finish.Length;
        }
        ServiceLog.Warn(finished
            ? $"[deepcool-mystique] upload failed in frame {frameIndex}; filled {remaining} bytes with zeros and sent the finish packet"
            : $"[deepcool-mystique] upload failed in frame {frameIndex}; the pipe stopped taking writes, so the screen may stay in upload mode until power cycled");
    }

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
