using System;
using Nexus.Service.Peripherals.LianLiCp;

namespace Nexus.Service.Peripherals.Galahad2;

// Byte facts from L-Connect / FanControl.LianLi (Galahad2Protocol, Galahad2Controller).
internal static class Galahad2Protocol
{
    public const int VendorId = 0x0416;
    public const int ProductIdPerformance = 0x7371;
    public const int ProductIdRegular = 0x7373;
    public const int ProductIdLcd = 0x7395;

    // The LCD variant's pump is one 12-LED ring (not the wired Trinity's separate
    // inner/outer rings) - confirmed against SignalRGB's Lian_Li_Galahad_II_LCD.js.
    public const int PumpLedCount = 12;
    public const int PerLedReportLength = 1024;

    // Pump duty floored to keep coolant circulating; never command below this.
    public const int PumpDutyFloor = 50;

    private const byte CmdSetFan = 0x8B;
    private const byte CmdSetPump = 0x8A;
    private const byte CmdHandshake = 0x81;

    // syncFlag byte 0x00 = host control (not sync-to-mobo).
    public static byte[] EncodeSetFan(int dutyPercent)
    {
        byte duty = (byte)Math.Clamp(dutyPercent, 0, 100);
        return CommandPacket.Build(CmdSetFan, 0x00, duty);
    }

    // Encoder floors pump at PumpDutyFloor; the provider also clamps before calling this.
    public static byte[] EncodeSetPump(int dutyPercent)
    {
        byte duty = (byte)Math.Clamp(dutyPercent, PumpDutyFloor, 100);
        return CommandPacket.Build(CmdSetPump, 0x00, duty);
    }

    public static byte[] EncodeHandshakeRequest() =>
        CommandPacket.Build(CmdHandshake);

    private const byte CmdRgbControl = 0x83;
    // Declared payload length for RGB packets; matches 0x13 from the wire.
    private const int RgbPayloadLength = 19;

    // Payload layout from OpenRGB LianLiGAIITrinityController.cpp:
    // [ring, mode, brightness, speed, R0,G0,B0, R1,G1,B1, R2,G2,B2, R3,G3,B3, direction].
    // Color order is R,G,B (no swap). Ring: inner=0, outer=1, both=2.
    // colors span carries up to 4*(R,G,B) = 12 bytes; extra slots stay zero.
    public static byte[] EncodeLighting(byte ring, byte mode, byte brightness, byte speed, byte direction, ReadOnlySpan<byte> colors)
    {
        var payload = new byte[RgbPayloadLength];
        payload[0] = ring;
        payload[1] = mode;
        payload[2] = brightness;
        payload[3] = speed;
        var colorBytes = Math.Min(colors.Length, 12);
        for (var i = 0; i < colorBytes; i++)
        {
            payload[4 + i] = colors[i];
        }
        payload[16] = direction;
        return CommandPacket.Build(CmdRgbControl, payload);
    }

    private const byte CmdPerLed = 0x14;

    // Layout from SignalRGB's Lian_Li_Galahad_II_LCD.js (WhirlwindFX plugin): sendLargePacket
    // header (report 0x02, command, BE32 total length, 24-bit sequence, BE16 packet length),
    // then setPumpPerLED's payload - a zone byte, a 24-byte zero prefix (the first 8 of the
    // array's 12 RGB slots are never written), then the 12 LEDs in reverse (vPumpLeds =
    // [11..0]) order, R,G,B each.
    public static void EncodePumpPerLed(ReadOnlySpan<byte> colors, Span<byte> destination)
    {
        destination[..PerLedReportLength].Clear();
        destination[0] = 0x02;
        destination[1] = CmdPerLed;
        const int packetLength = 1 + 24 + PumpLedCount * 3; // zone byte + 24-byte prefix + 12 LEDs
        destination[2] = (byte)((packetLength >> 24) & 0xFF);
        destination[3] = (byte)((packetLength >> 16) & 0xFF);
        destination[4] = (byte)((packetLength >> 8) & 0xFF);
        destination[5] = (byte)(packetLength & 0xFF);
        // bytes 6..8 are the 24-bit sequence; a single packet is always sequence 0.
        destination[9] = (byte)((packetLength >> 8) & 0xFF);
        destination[10] = (byte)(packetLength & 0xFF);

        // data[0] is the zone byte (0 = pump). data[1..24] is the unwritten prefix.
        destination[11] = 0x00;
        var colorBytes = Math.Min(colors.Length, PumpLedCount * 3);
        for (var position = 0; position < PumpLedCount; position++)
        {
            var source = position * 3;
            var wireLed = PumpLedCount - 1 - position;
            var target = 11 + 1 + 24 + wireLed * 3;
            if (source + 2 >= colorBytes)
            {
                continue;
            }
            destination[target] = colors[source];
            destination[target + 1] = colors[source + 1];
            destination[target + 2] = colors[source + 2];
        }
    }

    // Reply payload (4 bytes): [fanRpm_hi, fanRpm_lo, pumpRpm_hi, pumpRpm_lo] (BE16 each).
    // Returns null when the payload is too short to decode.
    public static Galahad2Reading? DecodeHandshake(ReadOnlySpan<byte> packet)
    {
        if (CommandPacket.PayloadLengthOf(packet) < 4)
        {
            return null;
        }
        int offset = CommandPacket.PayloadOffset;
        int fanRpm = (packet[offset] << 8) | packet[offset + 1];
        int pumpRpm = (packet[offset + 2] << 8) | packet[offset + 3];
        return new Galahad2Reading(fanRpm, pumpRpm);
    }
}

public readonly struct Galahad2Reading
{
    public Galahad2Reading(int fanRpm, int pumpRpm)
    {
        FanRpm = fanRpm;
        PumpRpm = pumpRpm;
    }

    public int FanRpm { get; }
    public int PumpRpm { get; }
}
