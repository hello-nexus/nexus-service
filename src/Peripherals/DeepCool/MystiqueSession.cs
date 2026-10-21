using System;
using Nexus.Service.Persistence;
using Nexus.Service.Peripherals.BulkPanels;

namespace Nexus.Service.Peripherals.DeepCool;

/// <summary>
/// One open pipe to a Mystique: the opening exchange, the settings groups the user has
/// changed, the clock, and the poll plus sensor push that keeps the screen on live data.
/// Every write is answered on the matching IN pipe; replies are drained so the device never
/// stalls on a full queue, and a run of unanswered writes fails the session.
/// </summary>
public sealed class MystiqueSession
{
    private const int ReplyTimeoutMs = 300;
    private const int MaxUnanswered = 5;
    private static readonly TimeSpan ClockEvery = TimeSpan.FromHours(1);

    private readonly IBulkUsbPipe _pipe;
    private DeepCoolMystiqueSettings? _applied;
    private DateTime _clockSent = DateTime.MinValue;
    private int _unanswered;

    public MystiqueSession(IBulkUsbPipe pipe)
    {
        _pipe = pipe;
    }

    /// <summary>The identity string the device returned to the opening query, or empty.</summary>
    public string Info { get; private set; } = "";

    /// <summary>Opening exchange, then the groups the user has set, then the clock. False when the link is unusable.</summary>
    public bool Open(DeepCoolMystiqueSettings settings, DateTime localNow)
    {
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
        if (s.DeviceEdited && (last is null || !last.DeviceEdited || last.Gyroscope != s.Gyroscope || last.RgbSync != s.RgbSync || last.Brightness != s.Brightness)
            && !Settings(MystiqueProtocol.BuildSystem(s.Gyroscope, s.RgbSync, s.Brightness)))
        {
            return false;
        }
        if (s.ModeEdited && (last is null || !last.ModeEdited || last.Mode != s.Mode)
            && !Settings(MystiqueProtocol.BuildMode(s.Mode)))
        {
            return false;
        }
        if (s.ReadoutEdited && (last is null || !last.ReadoutEdited || last.Readout != s.Readout)
            && !Settings(MystiqueProtocol.BuildReadout(s.Readout)))
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

    public bool SendClock(DateTime localNow)
    {
        if (!Exchange(MystiqueProtocol.LiveWritePipe, MystiqueProtocol.LiveReadPipe, MystiqueProtocol.BuildClock(localNow), out _))
        {
            return false;
        }
        _clockSent = localNow;
        return true;
    }

    /// <summary>One cycle: status poll, then the live values; the clock again once an hour.</summary>
    public bool Tick(MystiqueReading reading, DateTime localNow)
    {
        if (!Exchange(MystiqueProtocol.LiveWritePipe, MystiqueProtocol.LiveReadPipe, MystiqueProtocol.BuildPoll(), out _)
            || !Exchange(MystiqueProtocol.LiveWritePipe, MystiqueProtocol.LiveReadPipe, MystiqueProtocol.BuildSensors(reading), out _))
        {
            return false;
        }
        return localNow - _clockSent < ClockEvery || SendClock(localNow);
    }

    private bool Settings(byte[] frame) =>
        Exchange(MystiqueProtocol.SettingsWritePipe, MystiqueProtocol.SettingsReadPipe, frame, out _);

    private bool Exchange(byte writePipe, byte readPipe, byte[] frame, out byte[] reply)
    {
        reply = new byte[64];
        if (!_pipe.Write(writePipe, frame))
        {
            return false;
        }
        int n = _pipe.Read(readPipe, reply, ReplyTimeoutMs);
        if (n < 0)
        {
            return false;
        }
        if (n >= MystiqueProtocol.FrameLength && MystiqueProtocol.IsReply(reply, frame[2]))
        {
            _unanswered = 0;
            return true;
        }
        return ++_unanswered < MaxUnanswered;
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
