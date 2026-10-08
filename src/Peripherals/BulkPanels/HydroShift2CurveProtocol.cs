using System;
using System.Text;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// Lian Li HydroShift II OLED Curved: the glass (1CBE:A068) speaks the LCD-S command set
/// (<see cref="HydroShift2Protocol"/>), and a second USB function (0416:8051) carries the
/// edge LEDs, pump, coolant probe and the two head motors. Facts from L-Connect 3's lcd207
/// <c>HS2Controller</c> / <c>WinUsbHS2</c>, checked on the Y70's unit (plans/lianli-hydroshift2-curve.md).
/// </summary>
public static class HydroShift2CurveProtocol
{
    public const int GlassProductId = 0xA068;
    public const int BoardVendorId = 0x0416;
    public const int BoardProductId = 0x8051;

    /// <summary>Panel size Nexus renders. The firmware framebuffer is the same glass held portrait (1080x2288), so frames are turned a quarter clockwise.</summary>
    public const int Width = 2288;
    public const int Height = 1080;

    public const byte CommandWarnSwitch = 0x2E;
    public const byte CommandClearPng = 0x67;
    public const byte CommandHideShow = 0x68;
    public const byte CommandGetH264Block = 0x11;
    /// <summary>Restarts the glass; it comes back on its own coolant and pump screen.</summary>
    public const byte CommandReboot = 0x0B;
    public const byte CommandStartPlay = 0x79;
    public const byte CommandQueryBlock = 0x7A;

    /// <summary>L-Connect's video chunk size when a GetH264Block reply carries none.</summary>
    public const int DefaultH264Block = 202752;

    /// <summary>A video chunk is answered with the device's buffered block count at [8]; above this, wait for it to drain.</summary>
    public const int H264BufferHigh = 3;
    public const int H264BufferLow = 2;

    public const byte BoardVersion = 0x10;
    public const byte BoardLedFrame = 0x11;
    public const byte BoardMove = 0x50;
    public const byte BoardStatus = 0x60;
    public const byte BoardPumpOutput = 0x61;
    public const byte BoardPumpRpm = 0x62;
    public const byte BoardHeaderFollow = 0x64;

    public const int LedCount = 35;

    /// <summary>LEDs per frame chunk; each chunk names the LED it starts at.</summary>
    public const int LedsPerChunk = 15;

    /// <summary>Pump output register range, near-linear in speed (measured).</summary>
    public const int PumpOutputMin = 250;
    public const int PumpOutputMax = 2300;

    /// <summary>Pump speed read back at the lowest and highest output (measured, settled).</summary>
    public const int PumpRpmAtMinOutput = 1630;
    public const int PumpRpmAtMaxOutput = 2765;

    /// <summary>Output for the ~2173 rpm the pump ran at from power-on before any host drove it (measured); handed back on release.</summary>
    public const int DefaultPumpOutput = 1274;

    public const byte TiltMotor = 1;
    public const byte SlideMotor = 2;
    public const int TiltMax = 45;
    public const int SlideMin = -10;
    public const int SlideMax = 8;
    private const int StepsPerDegree = 6;
    private const double StepsPerSlideUnit = 98.89;

    /// <summary>L-Connect's speed byte for moves; homing runs slower at <see cref="HomeSpeed"/>. 0 is fastest.</summary>
    public const byte MoveSpeed = 1;
    public const byte HomeSpeed = 2;

    /// <summary>
    /// L-Connect's homing: tilt down past its range onto the 0-degree stop, slide down onto
    /// the bottom stop, then up to the middle, which is (0, 0).
    /// </summary>
    public static readonly (byte Motor, byte Direction, int Steps)[] HomingSteps =
    {
        (TiltMotor, 1, 315),
        (SlideMotor, 2, 1800),
        (SlideMotor, 1, 980),
    };

    /// <summary>
    /// One chunk of an Annex-B H.264 stream for the glass's own decoder: length u32 BE at
    /// params[0..4], last-chunk flag, play count, then a session tick u32 BE that stays the
    /// same for the whole video.
    /// </summary>
    public static byte[] EncodeVideoChunk(ReadOnlySpan<byte> chunk, bool last, uint sessionTick, uint timestampMs)
    {
        Span<byte> parameters = stackalloc byte[10];
        parameters[0] = (byte)(chunk.Length >> 24);
        parameters[1] = (byte)(chunk.Length >> 16);
        parameters[2] = (byte)(chunk.Length >> 8);
        parameters[3] = (byte)chunk.Length;
        parameters[4] = (byte)(last ? 1 : 0);
        parameters[5] = 1;
        parameters[6] = (byte)(sessionTick >> 24);
        parameters[7] = (byte)(sessionTick >> 16);
        parameters[8] = (byte)(sessionTick >> 8);
        parameters[9] = (byte)sessionTick;
        var header = HydroShift2Protocol.EncodeCommand(CommandStartPlay, parameters, timestampMs);
        var packet = new byte[header.Length + chunk.Length];
        header.CopyTo(packet, 0);
        chunk.CopyTo(packet.AsSpan(header.Length));
        return packet;
    }

    /// <summary>Video chunk size from a GetH264Block reply (u32 BE at [8]), or the default.</summary>
    public static int DecodeH264Block(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 12 || reply[0] != CommandGetH264Block)
        {
            return DefaultH264Block;
        }
        int size = (reply[8] << 24) | (reply[9] << 16) | (reply[10] << 8) | reply[11];
        return size > 0 ? size : DefaultH264Block;
    }

    /// <summary>Blocks the glass holds queued, from a StartPlay or QueryBlock reply ([8]), or null.</summary>
    public static int? DecodeBufferedBlocks(ReadOnlySpan<byte> reply) =>
        reply.Length > 8 && (reply[0] == CommandStartPlay || reply[0] == CommandQueryBlock) ? reply[8] : null;

    /// <summary>
    /// ffmpeg filter that turns a cropped landscape frame into the glass's portrait framebuffer
    /// for the way the head is mounted: a quarter clockwise upright, counter-clockwise when
    /// flipped, mirrored before the turn.
    /// </summary>
    public static string MountFilter(bool flip180, bool mirror) =>
        $"scale={Width}:{Height}:flags=lanczos,{(mirror ? "hflip," : "")}transpose={(flip180 ? 2 : 1)}";

    /// <summary>Version query; L-Connect sends it as a full 64-byte packet.</summary>
    public static byte[] EncodeVersionQuery()
    {
        var packet = new byte[64];
        packet[0] = BoardVersion;
        return packet;
    }

    /// <summary>One edge-LED frame of packed RGB as the three chunk commands it travels in.</summary>
    public static byte[][] EncodeLedFrame(ReadOnlySpan<byte> rgb)
    {
        if (rgb.Length != LedCount * 3)
        {
            throw new ArgumentException($"expected {LedCount * 3} bytes", nameof(rgb));
        }
        var chunks = new byte[(LedCount + LedsPerChunk - 1) / LedsPerChunk][];
        for (int c = 0; c < chunks.Length; c++)
        {
            int start = c * LedsPerChunk;
            int count = Math.Min(LedsPerChunk, LedCount - start);
            var packet = new byte[64];
            packet[0] = BoardLedFrame;
            packet[1] = (byte)start;
            rgb.Slice(start * 3, count * 3).CopyTo(packet.AsSpan(4));
            chunks[c] = packet;
        }
        return chunks;
    }

    /// <summary>A relative move: <paramref name="steps"/> big-endian at [3..7].</summary>
    public static byte[] EncodeMove(byte motor, byte direction, int steps, byte speed) => new byte[]
    {
        BoardMove, motor, direction,
        (byte)(steps >> 24), (byte)(steps >> 16), (byte)(steps >> 8), (byte)steps,
        speed,
    };

    /// <summary>A zero-step move: accepted (status 0) when the motor is idle, refused with its number while it runs.</summary>
    public static byte[] EncodeBusyProbe(byte motor) => EncodeMove(motor, 1, 0, MoveSpeed);

    public static byte[] EncodeQuery(byte command) => new byte[] { command, 0, 0, 0, 0, 0, 0, 0 };

    public static byte[] EncodePumpOutput(int output)
    {
        output = Math.Clamp(output, PumpOutputMin, PumpOutputMax);
        return new byte[] { BoardPumpOutput, (byte)(output >> 8), (byte)output, 0, 0, 0, 0, 0 };
    }

    /// <summary>The pump follows the motherboard header it is wired to, or (false) the output Nexus sets.</summary>
    public static byte[] EncodeHeaderFollow(bool follow) =>
        new byte[] { BoardHeaderFollow, (byte)(follow ? 0 : 1), 0, 0, 0, 0, 0, 0 };

    public static int PumpOutputForDuty(int dutyPercent) =>
        PumpOutputMin + ((PumpOutputMax - PumpOutputMin) * Math.Clamp(dutyPercent, 0, 100) / 100);

    /// <summary>The duty a measured speed corresponds to, for showing a pump Nexus is not driving.</summary>
    public static int PumpDutyForRpm(int rpm) =>
        Math.Clamp((rpm - PumpRpmAtMinOutput) * 100 / (PumpRpmAtMaxOutput - PumpRpmAtMinOutput), 0, 100);

    // The firmware enforces no travel limits (a move past an end stop stalls against it), so
    // clamping the target to the range is the only limit.

    /// <summary>Tilt move from one angle to another, or null when there is nothing to do. dir 2 raises the angle.</summary>
    public static (byte Direction, int Steps)? TiltMove(int from, int to)
    {
        int steps = (Math.Clamp(to, 0, TiltMax) - from) * StepsPerDegree;
        if (steps == 0) return null;
        return ((byte)(steps > 0 ? 2 : 1), Math.Abs(steps));
    }

    /// <summary>
    /// Slide move from one height to another, or null when there is nothing to do. dir 1 raises
    /// the head. Steps come from both ends' absolute positions so rounding never accumulates.
    /// </summary>
    public static (byte Direction, int Steps)? SlideMove(int from, int to)
    {
        int steps = SlideSteps(Math.Clamp(to, SlideMin, SlideMax)) - SlideSteps(from);
        if (steps == 0) return null;
        return ((byte)(steps > 0 ? 1 : 2), Math.Abs(steps));
    }

    private static int SlideSteps(int position) => (int)Math.Round(position * StepsPerSlideUnit);

    /// <summary>A move reply's status: 0 accepted, otherwise the number of the motor still running.</summary>
    public static int? DecodeMoveStatus(ReadOnlySpan<byte> reply) =>
        reply.Length >= 2 && reply[0] == BoardMove ? reply[1] : null;

    // Replies reuse one firmware buffer: only the bytes each command writes are fresh, the rest is whatever the last reply left.

    /// <summary>Coolant degrees C from a status reply ([1]).</summary>
    public static int? DecodeCoolant(ReadOnlySpan<byte> reply) =>
        reply.Length >= 2 && reply[0] == BoardStatus ? reply[1] : null;

    /// <summary>Whether a status reply says the pump follows the motherboard header ([2] = 0).</summary>
    public static bool? DecodeFollowsHeader(ReadOnlySpan<byte> reply) =>
        reply.Length >= 3 && reply[0] == BoardStatus ? reply[2] == 0 : null;

    /// <summary>Pump rpm from a speed reply, big-endian at [1..3].</summary>
    public static int? DecodePumpRpm(ReadOnlySpan<byte> reply) =>
        reply.Length >= 3 && reply[0] == BoardPumpRpm ? (reply[1] << 8) | reply[2] : null;

    /// <summary>Firmware date string from a version reply ("10 05 2026041801"), or null.</summary>
    public static string? DecodeVersion(ReadOnlySpan<byte> reply)
    {
        if (reply.Length < 3 || reply[0] != BoardVersion)
        {
            return null;
        }
        var text = reply[2..];
        int end = text.IndexOf((byte)0);
        var version = Encoding.ASCII.GetString(end < 0 ? text : text[..end]);
        return version.Length > 0 ? version : null;
    }
}
