using System;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.DeepCool;

/// <summary>The live values the screen shows; CPU usage is a whole percent, RAM usage is tenths of a percent, fan and pump are RPM, frequency is MHz.</summary>
public readonly record struct MystiqueReading(int TempC, int UsagePct, int RamTenthsPct, int CpuMhz, int FanRpm, int PumpRpm);

/// <summary>
/// Frames for the Mystique AIO screen (USB 3633:0009, one vendor interface). Every frame is
/// 48 bytes: AA 2E cmd payload, the ASCII tag "HIDC" at 42, then the little-endian sum of
/// bytes 0..45. Replies start 55 2E cmd and carry the same trailer.
/// Two bulk channels: 0x01 out / 0x81 in take one-shot settings, 0x02 out / 0x82 in take the
/// once-a-second status poll, the live sensor push and the clock.
/// </summary>
public static class MystiqueProtocol
{
    public const int VendorId = 0x3633;
    public const int ProductId = 0x0009;

    public const byte SettingsWritePipe = 0x01;
    public const byte SettingsReadPipe = 0x81;
    public const byte LiveWritePipe = 0x02;
    public const byte LiveReadPipe = 0x82;

    public const int FrameLength = 48;
    public const int MaxPayload = 39;

    public const byte HostMagic = 0xAA;
    public const byte DeviceMagic = 0x55;
    public const byte Marker = 0x2E;

    private const int PayloadOffset = 3;
    private const int TagOffset = 42;
    private const int ChecksumOffset = 46;

    public const byte CmdSensors = 0x01;
    public const byte CmdSystem = 0x02;
    public const byte CmdMode = 0x03;
    public const byte CmdReadout = 0x04;
    public const byte CmdRecording = 0x05;
    public const byte CmdRecordingApply = 0x06;
    public const byte CmdClock = 0x0A;
    public const byte CmdPoll = 0x10;
    public const byte CmdInfo = 0x12;

    /// <summary>Screen mode byte of <see cref="CmdMode"/>.</summary>
    public static byte ModeCode(string mode) => mode switch
    {
        DeepCoolMystiqueSettings.ModeMedia => 2,
        DeepCoolMystiqueSettings.ModeRecording => 3,
        _ => 1,
    };

    /// <summary>First byte of <see cref="CmdReadout"/>: what the monitor mode's main area shows.</summary>
    public static byte ReadoutCode(string readout) => readout switch
    {
        DeepCoolMystiqueSettings.ReadoutFrequency => 0,
        DeepCoolMystiqueSettings.ReadoutTime => 1,
        DeepCoolMystiqueSettings.ReadoutPump => 2,
        DeepCoolMystiqueSettings.ReadoutFan => 3,
        DeepCoolMystiqueSettings.ReadoutFanPump => 4,
        _ => 5,
    };

    public static byte[] BuildFrame(byte cmd, ReadOnlySpan<byte> payload, byte magic = HostMagic)
    {
        if (payload.Length > MaxPayload)
        {
            throw new ArgumentException("payload too long", nameof(payload));
        }
        var frame = new byte[FrameLength];
        frame[0] = magic;
        frame[1] = Marker;
        frame[2] = cmd;
        payload.CopyTo(frame.AsSpan(PayloadOffset));
        frame[TagOffset] = (byte)'H';
        frame[TagOffset + 1] = (byte)'I';
        frame[TagOffset + 2] = (byte)'D';
        frame[TagOffset + 3] = (byte)'C';
        int sum = 0;
        for (int i = 0; i < ChecksumOffset; i++)
        {
            sum += frame[i];
        }
        frame[ChecksumOffset] = (byte)sum;
        frame[ChecksumOffset + 1] = (byte)(sum >> 8);
        return frame;
    }

    /// <summary>True when <paramref name="frame"/> is a well-formed reply to <paramref name="cmd"/>.</summary>
    public static bool IsReply(ReadOnlySpan<byte> frame, byte cmd)
    {
        if (frame.Length < FrameLength || frame[0] != DeviceMagic || frame[1] != Marker || frame[2] != cmd)
        {
            return false;
        }
        if (frame[TagOffset] != 'H' || frame[TagOffset + 1] != 'I' || frame[TagOffset + 2] != 'D' || frame[TagOffset + 3] != 'C')
        {
            return false;
        }
        int sum = 0;
        for (int i = 0; i < ChecksumOffset; i++)
        {
            sum += frame[i];
        }
        return frame[ChecksumOffset] == (byte)sum && frame[ChecksumOffset + 1] == (byte)(sum >> 8);
    }

    public static byte[] BuildPoll() => BuildFrame(CmdPoll, ReadOnlySpan<byte>.Empty);

    public static byte[] BuildInfoQuery() => BuildFrame(CmdInfo, ReadOnlySpan<byte>.Empty);

    /// <summary>Local wall-clock time: year (LE16), month, day, hour, minute, second.</summary>
    public static byte[] BuildClock(DateTime local)
    {
        Span<byte> p = stackalloc byte[7];
        p[0] = (byte)local.Year;
        p[1] = (byte)(local.Year >> 8);
        p[2] = (byte)local.Month;
        p[3] = (byte)local.Day;
        p[4] = (byte)local.Hour;
        p[5] = (byte)local.Minute;
        p[6] = (byte)local.Second;
        return BuildFrame(CmdClock, p);
    }

    /// <summary>Gyroscope rotation, RGB sync and brightness in one frame; the two constant bytes are sent as the screen's own software does.</summary>
    public static byte[] BuildSystem(bool gyroscope, bool rgbSync, int brightness)
    {
        Span<byte> p = stackalloc byte[5];
        p[0] = 0x01;
        p[1] = gyroscope ? (byte)1 : (byte)0;
        p[2] = 0x02;
        p[3] = rgbSync ? (byte)1 : (byte)0;
        p[4] = (byte)Math.Clamp(brightness, 0, 100);
        return BuildFrame(CmdSystem, p);
    }

    public static byte[] BuildMode(string mode)
    {
        Span<byte> p = stackalloc byte[1];
        p[0] = ModeCode(mode);
        return BuildFrame(CmdMode, p);
    }

    /// <summary>Monitor-mode main readout; the last byte is the auxiliary area, which only ever carried the core data value.</summary>
    public static byte[] BuildReadout(string readout)
    {
        Span<byte> p = stackalloc byte[4];
        p[0] = ReadoutCode(readout);
        p[3] = 0x02;
        return BuildFrame(CmdReadout, p);
    }

    /// <summary>Recording-tab graphs: frequency flag then temperature flag.</summary>
    public static byte[] BuildRecording(bool frequency, bool temperature)
    {
        Span<byte> p = stackalloc byte[2];
        p[0] = frequency ? (byte)1 : (byte)0;
        p[1] = temperature ? (byte)1 : (byte)0;
        return BuildFrame(CmdRecording, p);
    }

    /// <summary>Follows every <see cref="CmdRecording"/> write.</summary>
    public static byte[] BuildRecordingApply() => BuildFrame(CmdRecordingApply, new byte[] { 0x01 });

    /// <summary>
    /// Live push. Three-byte fields from payload offset 0: temperature, CPU usage, RAM usage as whole percent then tenths, three
    /// fields whose meaning is unknown (sent at fixed values seen in the recorded traffic), frequency as GHz
    /// whole part then hundredths, fan RPM and pump RPM as 24-bit little-endian.
    /// </summary>
    public static byte[] BuildSensors(MystiqueReading reading)
    {
        Span<byte> p = stackalloc byte[MaxPayload];
        p[0] = (byte)Math.Clamp(reading.TempC, 0, 255);
        p[3] = (byte)Math.Clamp(reading.UsagePct, 0, 100);
        int ramTenths = Math.Clamp(reading.RamTenthsPct, 0, 1000);
        p[6] = (byte)(ramTenths / 10);
        p[8] = (byte)(ramTenths % 10);
        p[9] = 0x03;
        p[11] = 0x13;
        p[12] = 0x04;
        p[14] = 0x5F;
        p[15] = 0x0B;
        p[17] = 0x5F;
        int hundredths = Math.Clamp((int)Math.Round(Math.Max(reading.CpuMhz, 0) / 10.0), 0, 9999);
        p[18] = (byte)(hundredths / 100);
        p[20] = (byte)(hundredths % 100);
        WriteRpm(p, 21, reading.FanRpm);
        WriteRpm(p, 24, reading.PumpRpm);
        return BuildFrame(CmdSensors, p);
    }

    private static void WriteRpm(Span<byte> p, int offset, int rpm)
    {
        int v = Math.Clamp(rpm, 0, 0xFFFFFF);
        p[offset] = (byte)v;
        p[offset + 1] = (byte)(v >> 8);
        p[offset + 2] = (byte)(v >> 16);
    }
}
