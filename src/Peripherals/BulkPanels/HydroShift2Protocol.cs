using System;
using System.IO;
using Nexus.Service.Peripherals.LianLiWireless;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// Lian Li HydroShift II LCD-S (1CBE:A034) over its USB link: the Universal Screen's
/// DES-encrypted 512-byte commands plus the AIO opcodes, from L-Connect 3's lcd207
/// WinUsbH2S. Every command is answered by a 512-byte reply whose first byte echoes it.
/// </summary>
public static class HydroShift2Protocol
{
    public const int ProductIdSquare = 0xA034;
    public const int Width = 480;
    public const int Height = 480;

    public const byte CommandGetVersion = 0x0A;
    public const byte CommandBrightness = 0x0E;
    public const byte CommandFrameRate = 0x0F;
    public const byte CommandSetClock = 0x33;
    public const byte CommandStopClock = 0x34;
    public const byte CommandPushJpeg = 0x65;
    public const byte CommandPushPng = 0x66;
    public const byte CommandStopPlay = 0x7B;
    public const byte CommandGetParams = 0xFA;
    public const byte CommandSyncPumpFan = 0xFB;
    public const byte CommandPushRgb = 0xFC;

    public const int RingLedCount = 24;
    public const int FanSlots = 3;
    public const int PumpMinRpm = 1600;
    public const int PumpMaxRpm = 3200;

    /// <summary>
    /// L-Connect's own pump/fan values. SyncPumpFan always sets both halves, so these fill
    /// the half Nexus is not driving, and go out once when Nexus lets go: the firmware held
    /// the last target it was sent after Nexus stopped resending (measured at full speed).
    /// </summary>
    public const int DefaultPumpRpm = 2080;
    public const byte DefaultFanByte = 80;

    /// <summary>Lowest pump duty that holds steady: at 0% the pump never settled, swinging between its minimum and maximum speed (measured).</summary>
    public const int PumpDutyFloor = 25;

    /// <summary>SetClock mode that syncs the clock without showing the firmware clock face.</summary>
    private const byte ClockSyncOnly = 2;

    /// <summary>Builds one command; <paramref name="timestampMs"/> must strictly increase within a session.</summary>
    public static byte[] EncodeCommand(byte command, ReadOnlySpan<byte> parameters, uint timestampMs) =>
        UniversalScreen88Protocol.EncodeCommand(command, parameters, timestampMs);

    /// <summary>Header carrying the payload length big-endian at params[0..4], then the payload, as one transfer.</summary>
    public static byte[] EncodeImage(byte command, ReadOnlySpan<byte> image, uint timestampMs)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, image.Length);
        var header = EncodeCommand(command, length, timestampMs);
        var packet = new byte[header.Length + image.Length];
        header.CopyTo(packet, 0);
        image.CopyTo(packet.AsSpan(header.Length));
        return packet;
    }

    public static byte[] EncodeSetClock(DateTime now, uint timestampMs) => EncodeCommand(CommandSetClock, new byte[]
    {
        (byte)(now.Year >> 8), (byte)now.Year, (byte)now.Month, (byte)now.Day,
        (byte)now.Hour, (byte)now.Minute, (byte)now.Second, ClockSyncOnly,
    }, timestampMs);

    /// <summary>
    /// A ring animation the firmware loops on its own: TinyUZ-compressed RGB for every LED of
    /// every frame, then frame count (u16 BE), per-frame interval in 0.625 ms ticks and LED
    /// count. The length rides params[4..8], not params[0..4].
    /// </summary>
    public static byte[] EncodeRing(ReadOnlySpan<byte> frames, int frameCount, byte intervalTicks, uint timestampMs)
    {
        if (frameCount < 1 || frameCount > ushort.MaxValue || frames.Length != frameCount * RingLedCount * 3)
        {
            throw new ArgumentException($"expected {RingLedCount * 3} bytes per frame", nameof(frames));
        }
        var compressed = TinyUz.Compress(frames);
        var payload = new byte[compressed.Length + 4];
        compressed.CopyTo(payload, 0);
        payload[compressed.Length] = (byte)(frameCount >> 8);
        payload[compressed.Length + 1] = (byte)frameCount;
        payload[compressed.Length + 2] = Math.Max((byte)1, intervalTicks);
        payload[compressed.Length + 3] = RingLedCount;

        Span<byte> parameters = stackalloc byte[8];
        WriteBigEndian(parameters[4..], payload.Length);
        var header = EncodeCommand(CommandPushRgb, parameters, timestampMs);
        var packet = new byte[header.Length + payload.Length];
        header.CopyTo(packet, 0);
        payload.CopyTo(packet.AsSpan(header.Length));
        return packet;
    }

    /// <summary>
    /// Pump target and raw fan bytes (0-255). Layout FF 0F A2 00, pump timer u16 BE, three
    /// fan bytes, then CRC-16/CCITT of the first 12 bytes at [12..13].
    /// </summary>
    public static byte[] EncodeSyncPumpFan(int pumpRpm, ReadOnlySpan<byte> fans, uint timestampMs)
    {
        var block = new byte[16];
        block[0] = 0xFF;
        block[1] = 0x0F;
        block[2] = 0xA2;
        var timer = PumpTimer(pumpRpm);
        block[4] = (byte)(timer >> 8);
        block[5] = (byte)timer;
        for (int i = 0; i < FanSlots && i < fans.Length; i++)
        {
            block[6 + i] = fans[i];
        }
        var crc = Crc16Ccitt(block.AsSpan(0, 12));
        block[12] = (byte)(crc >> 8);
        block[13] = (byte)crc;
        return EncodeCommand(CommandSyncPumpFan, block, timestampMs);
    }

    /// <summary>The pump's speed register for a target rpm: a falling piecewise curve, L-Connect's table for the square head.</summary>
    public static int PumpTimer(int rpm)
    {
        rpm = Math.Clamp(rpm, PumpMinRpm, PumpMaxRpm);
        return rpm switch
        {
            <= 1800 => 1590 - (int)((rpm - 1600) * 0.95),
            <= 2000 => 1400 - (rpm - 1800),
            <= 2200 => 1200 - (rpm - 2000),
            <= 2400 => 1000 - (rpm - 2200),
            <= 2600 => 800 - (rpm - 2400),
            <= 2800 => 580 - (int)((rpm - 2600) * 1.11),
            <= 3000 => 330 - (int)((rpm - 2800) * 1.2),
            _ => 90 - (int)((rpm - 3000) * 0.45),
        };
    }

    /// <summary>Pump duty percent mapped linearly onto the pump's rpm range.</summary>
    public static int PumpRpmForDuty(int dutyPercent) =>
        PumpMinRpm + ((PumpMaxRpm - PumpMinRpm) * Math.Clamp(dutyPercent, 0, 100) / 100);

    /// <summary>Firmware string from a GetVersion reply (ASCII at [8..40]), or null.</summary>
    public static string? DecodeVersion(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 40 || reply[0] != CommandGetVersion)
        {
            return null;
        }
        var text = reply.Slice(8, 32);
        int end = text.IndexOf((byte)0);
        var version = System.Text.Encoding.ASCII.GetString(end < 0 ? text : text[..end]);
        return version.Length > 0 ? version : null;
    }

    /// <summary>
    /// GetParams reply: coolant °C at [13], fan rpm u16 BE at [14], [16], [18], pump rpm
    /// at [20], radio MAC at [22..28].
    /// </summary>
    public static HydroShift2Params? DecodeParams(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 28 || reply[0] != CommandGetParams)
        {
            return null;
        }
        return new HydroShift2Params(
            CoolantC: reply[13],
            FanRpm: new[] { ReadBigEndian16(reply, 14), ReadBigEndian16(reply, 16), ReadBigEndian16(reply, 18) },
            PumpRpm: ReadBigEndian16(reply, 20),
            Mac: Convert.ToHexString(reply.Slice(22, 6)));
    }

    /// <summary>CRC-16/CCITT: poly 0x1021, init 0, no reflection, no final XOR.</summary>
    public static ushort Crc16Ccitt(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    /// <summary>
    /// A fully transparent panel-sized PNG. Pushed once at connect, it replaces the overlay
    /// layer where the firmware draws its own coolant and pump readout over every JPEG.
    /// </summary>
    public static byte[] EmptyOverlayPng()
    {
        using var image = new Image<Rgba32>(Width, Height);
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static void WriteBigEndian(Span<byte> target, int value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
    }

    private static int ReadBigEndian16(ReadOnlySpan<byte> data, int offset) => (data[offset] << 8) | data[offset + 1];
}

/// <summary>One GetParams reading. Fan rpm is per slot; 0 means nothing is plugged into it.</summary>
public sealed record HydroShift2Params(int CoolantC, int[] FanRpm, int PumpRpm, string Mac);
