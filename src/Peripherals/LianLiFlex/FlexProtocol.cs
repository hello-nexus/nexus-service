using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiWireless;

namespace Nexus.Service.Peripherals.LianLiFlex;

/// <summary>
/// Pure byte-level builders and parsers for the wired Flex receivers (VID 0x43A8): 64-byte bulk
/// frames on EP 0x01 / 0x81, no report id, no encryption. No IO.
/// </summary>
public static class FlexProtocol
{
    public const int ReceiverVendorId = 0x43A8;
    public const int TlReceiverProductId = 0x0102;
    public const int SlInfReceiverProductId = 0x0104;
    public const int TlScreenProductId = 0xA018;
    public const int SlInfScreenProductId = 0xA019;
    public const int ScreenVendorId = 0x1CBE;

    /// <summary>Interface GUID the receivers, the Flex screens and the RF dongle all register.</summary>
    public const string InterfaceGuid = "{1D4B2365-4749-48EA-B38A-7C6FDDDD7E26}";

    public const byte WritePipeId = 0x01;
    public const byte ReadPipeId = 0x81;
    public const int FrameSize = 64;

    public const byte CmdSyncClock = 0x08;
    public const byte CmdGetVer = 0x10;
    public const byte CmdLive = 0x11;
    public const byte CmdGetInfo = 0x12;
    public const byte CmdSetPwm = 0x13;
    public const byte CmdSyncBoard = 0x14;
    public const byte CmdSave = 0x15;
    public const byte CmdRebootLcd = 0x17;
    public const byte CmdPackage = 0x18;
    public const byte CmdEnable = 0x19;

    public const byte PwmFollowMotherboard = 6;
    public const int LiveLedsPerPacket = 20;
    public const int PackageChunk = 60;
    public const int TicksPerSecond = 1600;
    public const byte UnboundRxType = 0xFE;

    public const int TlLedsPerFan = 26;
    public const int SlInfLedsPerFan = 44;

    public static bool IsReceiverProduct(int vid, int pid) =>
        vid == ReceiverVendorId && (pid == TlReceiverProductId || pid == SlInfReceiverProductId);

    public static bool IsScreenProduct(int vid, int pid) =>
        vid == ScreenVendorId && (pid == TlScreenProductId || pid == SlInfScreenProductId);

    public static FlexFamily FamilyOfReceiver(int pid) => pid == SlInfReceiverProductId ? FlexFamily.SlInfFlex : FlexFamily.TlFlex;

    public static FlexFamily FamilyOfScreen(int pid) => pid == SlInfScreenProductId ? FlexFamily.SlInfFlex : FlexFamily.TlFlex;

    public static int LedsPerFan(FlexFamily family) => family == FlexFamily.SlInfFlex ? SlInfLedsPerFan : TlLedsPerFan;

    /// <summary>
    /// First bytes this module may put on a receiver. Everything else (firmware and theme-data
    /// commands) stays unconstructible; <see cref="IsAllowed"/> also refuses a
    /// save that clears the stored configuration.
    /// </summary>
    public static bool IsAllowed(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != FrameSize)
        {
            return false;
        }
        switch (frame[0])
        {
            case CmdSyncClock:
            case CmdGetVer:
            case CmdLive:
            case CmdGetInfo:
            case CmdSetPwm:
            case CmdRebootLcd:
            case CmdPackage:
            case CmdEnable:
                return true;
            case CmdSyncBoard:
                return frame[1] <= 1;
            case CmdSave:
                return frame[1] == 1;
            default:
                return false;
        }
    }

    private static byte[] NewFrame(byte cmd)
    {
        var f = new byte[FrameSize];
        f[0] = cmd;
        return f;
    }

    public static byte[] BuildGetInfo() => NewFrame(CmdGetInfo);

    public static byte[] BuildGetVer() => NewFrame(CmdGetVer);

    public static byte[] BuildRebootLcd() => NewFrame(CmdRebootLcd);

    public static byte[] BuildEnable() => NewFrame(CmdEnable);

    /// <summary>Persists the stored animation to the receiver's flash. The clear variant is never built.</summary>
    public static byte[] BuildSave()
    {
        var f = NewFrame(CmdSave);
        f[1] = 1;
        return f;
    }

    /// <summary>Receiver clock sync: seconds since start times 1600 as u32 BE.</summary>
    public static byte[] BuildSyncClock(double secondsSinceStart)
    {
        var f = NewFrame(CmdSyncClock);
        var ticks = unchecked((uint)(long)(secondsSinceStart * TicksPerSecond));
        f[1] = (byte)(ticks >> 24);
        f[2] = (byte)(ticks >> 16);
        f[3] = (byte)(ticks >> 8);
        f[4] = (byte)ticks;
        return f;
    }

    /// <summary>The duty percent actually requested: each family has a floor, and zero maps to a low stand-in that parks the fan.</summary>
    public static int ClampDutyPercent(FlexFamily family, int percent)
    {
        if (percent <= 0)
        {
            return 5;
        }
        return Math.Max(percent, family == FlexFamily.SlInfFlex ? 10 : 11);
    }

    public static byte EncodeDuty(FlexFamily family, int percent) =>
        (byte)Math.Round(Math.Min(ClampDutyPercent(family, percent), 100) / 100.0 * 255.0, MidpointRounding.AwayFromZero);

    /// <summary>Decodes a reported duty byte to a percent.</summary>
    public static int DecodeDuty(int wire) =>
        Math.Clamp((int)Math.Round(Math.Clamp(wire, 0, 255) * 100.0 / 255.0), 0, 100);

    public static byte[] BuildSetPwm(byte duty)
    {
        var f = NewFrame(CmdSetPwm);
        f[1] = duty;
        f[2] = duty;
        f[3] = duty;
        f[4] = duty;
        return f;
    }

    /// <summary>Hands the receiver's lighting to the motherboard ARGB input (on) or back to USB (off).</summary>
    public static byte[] BuildSyncBoard(bool on)
    {
        var f = NewFrame(CmdSyncBoard);
        f[1] = on ? (byte)1 : (byte)0;
        return f;
    }

    public static byte[] BuildFollowMotherboard() => BuildSetPwm(PwmFollowMotherboard);

    /// <summary>
    /// Splits one RGB frame (R,G,B per LED) into live-frame packets of up to 20 LEDs. Only the
    /// last packet carries the commit flag; the receiver shows a frame only when that flag is
    /// 1 or 2.
    /// </summary>
    public static byte[][] BuildLivePackets(ReadOnlySpan<byte> rgb, byte commitFlag = 2)
    {
        if (commitFlag is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(commitFlag));
        }
        var leds = rgb.Length / 3;
        var count = (leds + LiveLedsPerPacket - 1) / LiveLedsPerPacket;
        var packets = new byte[count][];
        for (var p = 0; p < count; p++)
        {
            var start = p * LiveLedsPerPacket;
            var n = Math.Min(LiveLedsPerPacket, leds - start);
            var f = NewFrame(CmdLive);
            f[1] = (byte)start;
            f[2] = (byte)n;
            f[3] = p == count - 1 ? commitFlag : (byte)0;
            rgb.Slice(start * 3, n * 3).CopyTo(f.AsSpan(4));
            packets[p] = f;
        }
        return packets;
    }

    /// <summary>
    /// A stored animation upload: a header packet (part 0) then 60-byte parts numbered from 1.
    /// The caller sends <see cref="BuildEnable"/> after the last part.
    /// </summary>
    public static byte[][] BuildPackagePackets(
        ReadOnlySpan<byte> effectIndex, ReadOnlySpan<byte> compressed, int totalFrames, int ledCount, double intervalMs)
    {
        var parts = (compressed.Length + PackageChunk - 1) / PackageChunk;
        var packets = new byte[parts + 1][];
        var h = NewFrame(CmdPackage);
        effectIndex.Slice(0, 4).CopyTo(h.AsSpan(16));
        h[22] = (byte)(compressed.Length >> 24);
        h[23] = (byte)(compressed.Length >> 16);
        h[24] = (byte)(compressed.Length >> 8);
        h[25] = (byte)compressed.Length;
        h[27] = (byte)(totalFrames >> 8);
        h[28] = (byte)totalFrames;
        h[29] = (byte)ledCount;
        var centi = (int)Math.Round(Math.Clamp(intervalMs, 0, ushort.MaxValue) * 100.0);
        h[34] = (byte)((centi / 100) >> 8);
        h[35] = (byte)(centi / 100);
        h[36] = (byte)(centi % 100);
        packets[0] = h;
        for (var i = 0; i < parts; i++)
        {
            var f = NewFrame(CmdPackage);
            f[1] = (byte)(i + 1);
            var n = Math.Min(PackageChunk, compressed.Length - (i * PackageChunk));
            compressed.Slice(i * PackageChunk, n).CopyTo(f.AsSpan(4));
            packets[i + 1] = f;
        }
        return packets;
    }

    /// <summary>Parses a 0x12 reply; null when the frame is not one.</summary>
    public static FlexReceiverInfo? ParseInfo(ReadOnlySpan<byte> f)
    {
        if (f.Length < 42 || f[0] != CmdGetInfo)
        {
            return null;
        }
        var rpm = new int[4];
        var flags = new int[4];
        var pwm = new int[4];
        for (var i = 0; i < 4; i++)
        {
            flags[i] = f[29 + (i * 2)] >> 4;
            rpm[i] = ((f[29 + (i * 2)] & 0xF) << 8) | f[30 + (i * 2)];
            pwm[i] = f[37 + i];
        }
        return new FlexReceiverInfo
        {
            Mac = Convert.ToHexString(f.Slice(1, 6)),
            MasterMac = Convert.ToHexString(f.Slice(7, 6)),
            Channel = f[13],
            RxType = f[14],
            DevType = f[19],
            FanNum = f[20],
            EffectIndex = Convert.ToHexString(f.Slice(21, 4)),
            FansType = f[25],
            PortFlags = flags,
            Rpm = rpm,
            Pwm = pwm,
        };
    }

    /// <summary>Reply to GetVer: the version integer replaces bytes [1..2] of the identity frame.</summary>
    public static int ParseVersion(ReadOnlySpan<byte> f) => f.Length >= 3 && f[0] == CmdGetVer ? (f[1] << 8) | f[2] : 0;

    /// <summary>Effect change id: milliseconds since local midnight as u32 BE; the firmware ignores an equal resend.</summary>
    public static byte[] BuildEffectIndex(DateTime localNow)
    {
        var ms = (uint)localNow.TimeOfDay.TotalMilliseconds;
        return new[] { (byte)(ms >> 24), (byte)(ms >> 16), (byte)(ms >> 8), (byte)ms };
    }

    /// <summary>Units per fan block of the wire buffer for a zone, in zone order (wire index per zone LED).</summary>
    public static IReadOnlyList<int> ZoneWireIndexes(FlexFamily family, string zoneId)
    {
        return (family, zoneId) switch
        {
            (FlexFamily.TlFlex, "up") => Range(0, 13),
            (FlexFamily.TlFlex, "down") => Range(13, 13),
            (FlexFamily.SlInfFlex, "center") => Range(0, 8),
            (FlexFamily.SlInfFlex, "inner") => Concat(Range(8, 10), Range(26, 10)),
            (FlexFamily.SlInfFlex, "outer") => Concat(Range(18, 8), Range(36, 8)),
            _ => Array.Empty<int>(),
        };
    }

    private static int[] Range(int start, int count)
    {
        var a = new int[count];
        for (var i = 0; i < count; i++)
        {
            a[i] = start + i;
        }
        return a;
    }

    private static int[] Concat(int[] a, int[] b)
    {
        var r = new int[a.Length + b.Length];
        a.CopyTo(r, 0);
        b.CopyTo(r, a.Length);
        return r;
    }
}

public sealed class FlexReceiverInfo
{
    public string Mac { get; init; } = "";
    public string MasterMac { get; init; } = "";
    public int Channel { get; init; }
    public int RxType { get; init; }
    public int DevType { get; init; }
    public int FanNum { get; init; }
    public string EffectIndex { get; init; } = "";
    public int FansType { get; init; }
    public int[] PortFlags { get; init; } = Array.Empty<int>();
    public int[] Rpm { get; init; } = Array.Empty<int>();
    public int[] Pwm { get; init; } = Array.Empty<int>();

    public int Fans => FanNum >= 10 ? FanNum - 10 : FanNum;

    public bool RfBound => Slv3Protocol.IsBoundSlot(RxType);

    /// <summary>The ARGB sync cable to a motherboard header is plugged in.</summary>
    public bool ArgbCableConnected => PortFlags.Length > 0 && (PortFlags[0] & 0x8) != 0;

    /// <summary>The unit is playing the motherboard ARGB input instead of the host's lighting.</summary>
    public bool PlayingMotherboardArgb => PortFlags.Length > 0 && (PortFlags[0] & 0x4) != 0;

    public bool PwmLinePresent => PortFlags.Length > 0 && (PortFlags[0] & 0x2) != 0;
}
