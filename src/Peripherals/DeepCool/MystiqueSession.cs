using System;
using Nexus.Service.Persistence;
using Nexus.Service.Peripherals.BulkPanels;

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
    private const int PacketAttempts = 3;

    // The screen's own software waited this long after the finish packet before its next command
    // (0.41 s after a picture, 0.057 s after a GIF); no shorter gap is recorded.
    private const int SourceAfterFinishMs = 410;

    private readonly IBulkUsbPipe _pipe;
    private readonly Func<long> _monotonicMs;
    private readonly Func<DateTime, TimeSpan> _utcOffset;
    private readonly Action<int> _sleep;
    private DeepCoolMystiqueSettings? _applied;
    private long _clockSentMs;
    private TimeSpan _clockOffset;
    private int _unanswered;

    /// <param name="monotonicMs">Clock for the resend schedule; wall-clock steps must not move it.</param>
    /// <param name="utcOffset">Local UTC offset at a given local time, to resend when a time zone or DST change shifts it.</param>
    /// <param name="sleep">Blocking wait in ms, for the gap after an upload.</param>
    public MystiqueSession(IBulkUsbPipe pipe, Func<long>? monotonicMs = null, Func<DateTime, TimeSpan>? utcOffset = null, Action<int>? sleep = null)
    {
        _pipe = pipe;
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
    /// 64-byte writes, the finish packet, then the source command for that kind. Nothing streams unless the
    /// screen answered the announce. True once the finish packet is out, whether or not the source command
    /// is answered; an unanswered one is re-sent by the next <see cref="Apply"/>.
    /// </summary>
    public bool Upload(MystiqueMediaPackage package)
    {
        if (!Exchange(MystiqueProtocol.SettingsWritePipe, MystiqueProtocol.SettingsReadPipe, MystiqueProtocol.BuildUploadStart(), out _, out bool announced)
            || !announced)
        {
            return false;
        }
        foreach (var frame in package.Frames)
        {
            foreach (var packet in MystiqueMedia.Packets(frame))
            {
                if (!WritePacket(packet))
                {
                    return false;
                }
            }
        }
        if (!WritePacket(MystiqueMedia.FinishPacket()))
        {
            return false;
        }
        _sleep(SourceAfterFinishMs);
        var source = package.Kind == MystiqueMedia.KindGif ? DeepCoolMystiqueSettings.SourceGif : DeepCoolMystiqueSettings.SourceImages;
        bool sent = Exchange(MystiqueProtocol.SettingsWritePipe, MystiqueProtocol.SettingsReadPipe, MystiqueProtocol.BuildMediaSource(source), out _, out bool answered);
        if (_applied is not null)
        {
            _applied.MediaSource = source;
            _applied.SourceEdited = sent && answered;
        }
        return true;
    }

    /// <summary>
    /// One stream write, retried as is. Each write is a single USB packet (the endpoint's max packet
    /// size), so a refused one delivered nothing and resending it keeps the stream matching its headers.
    /// When the retries run out nothing more is sent: filler would break the header's byte sum.
    /// </summary>
    private bool WritePacket(byte[] packet)
    {
        for (int attempt = 0; attempt < PacketAttempts; attempt++)
        {
            if (_pipe.Write(MystiqueProtocol.SettingsWritePipe, packet))
            {
                return true;
            }
        }
        return false;
    }

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

    private bool Settings(byte[] frame) =>
        Exchange(MystiqueProtocol.SettingsWritePipe, MystiqueProtocol.SettingsReadPipe, frame, out _);

    private bool Exchange(byte writePipe, byte readPipe, byte[] frame, out byte[] reply) =>
        Exchange(writePipe, readPipe, frame, out reply, out _);

    /// <param name="answered">The screen replied, as opposed to a tolerated silence.</param>
    private bool Exchange(byte writePipe, byte readPipe, byte[] frame, out byte[] reply, out bool answered)
    {
        answered = false;
        reply = new byte[64];
        if (!_pipe.Write(writePipe, frame))
        {
            return false;
        }
        long deadline = _monotonicMs() + ReplyTimeoutMs;
        long remaining = ReplyTimeoutMs;
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
