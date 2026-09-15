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
    private const byte CmdPerLed = 0x14;
    // Declared payload length for RGB packets; matches 0x13 from the wire.
    private const int RgbPayloadLength = 19;

    private const int PumpPerLedDataLength = 61;
    private const int PerLedHeaderLength = 11;
    private const int PumpPerLedPrefixLength = 24;

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

    // The LCD variant uses a separate 1024-byte B-report for the pump's individually
    // addressable positions. The UI order is reversed on the wire by the controller.
    public static byte[] EncodePumpPerLed(ReadOnlySpan<byte> colors)
    {
        var packet = new byte[PerLedReportLength];
        packet[0] = 0x02;
        packet[1] = CmdPerLed;
        WriteUInt32BigEndian(packet.AsSpan(2, 4), PumpPerLedDataLength);
        // bytes 6..8 are the 24-bit packet sequence; the first and only packet is zero.
        packet[9] = (byte)(PumpPerLedDataLength >> 8);
        packet[10] = (byte)PumpPerLedDataLength;

        // data[0] is the pump zone. data[1..24] is a controller-required zero prefix.
        packet[PerLedHeaderLength] = 0x00;
        var colorBytes = Math.Min(colors.Length, PumpLedCount * 3);
        for (var position = 0; position < PumpLedCount; position++)
        {
            var source = position * 3;
            var destination = PerLedHeaderLength + 1 + PumpPerLedPrefixLength
                + (PumpLedCount - 1 - position) * 3;
            if (source + 2 >= colorBytes)
            {
                continue;
            }
            packet[destination] = colors[source];
            packet[destination + 1] = colors[source + 1];
            packet[destination + 2] = colors[source + 2];
        }
        return packet;
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

    private static void WriteUInt32BigEndian(Span<byte> destination, int value)
    {
        destination[0] = (byte)((value >> 24) & 0xFF);
        destination[1] = (byte)((value >> 16) & 0xFF);
        destination[2] = (byte)((value >> 8) & 0xFF);
        destination[3] = (byte)(value & 0xFF);
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
