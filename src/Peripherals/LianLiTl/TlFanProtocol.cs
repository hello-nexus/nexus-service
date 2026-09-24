using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiCp;

namespace Nexus.Service.Peripherals.LianLiTl;

// Byte facts from L-Connect / FanControl.LianLi (TLFanDevice).
internal static class TlFanProtocol
{
    public const int VendorId = 0x0416;
    public const int ProductId = 0x7372;

    // Firmware accepts duty 12-100; 0% idles at wire value 1.
    public const int PwmMin = 12;
    public const int PwmMax = 100;
    public const byte PwmIdle = 1;

    // Timeout for the handshake reply (64-byte interrupt-IN response).
    public const int ReadTimeoutMs = 1000;

    private const byte CmdSetFanSpeed = 0xAA;
    private const byte CmdHandshake = 0xA1;
    private const byte CmdMotherboardSync = 0xB1;
    private const byte CmdSetFanLight = 0xA3;
    private const byte CmdSetFanGroup = 0xAD;
    private const byte CmdSetFanGroupLight = 0xB0;

    // Both light commands carry the same 20-byte payload; only bytes 0-1 differ.
    public const int LightPayloadLength = 20;
    public const int MaxLightColors = 4;
    private const int LightColorOffset = 5;

    public const int PortCount = 4;

    // Each port owns a block of group numbers; the first addresses the fans' top
    // halves and the second their bottom halves.
    private const int GroupsPerPortBlock = 8;

    public static int TopGroup(int port) => port * GroupsPerPortBlock;

    public static int BottomGroup(int port) => TopGroup(port) + 1;

    public static byte[] EncodeSetFanSpeed(int port, int fanIndex, int dutyPercent)
    {
        byte pwm = dutyPercent <= 0
            ? PwmIdle
            : (byte)Math.Clamp(dutyPercent, PwmMin, PwmMax);
        return CommandPacket.Build(CmdSetFanSpeed, Address(port, fanIndex), pwm);
    }

    public static byte[] EncodeHandshakeRequest() =>
        CommandPacket.Build(CmdHandshake);

    // High bit of the address byte enables mobo sync; host-control = sync off.
    public static byte[] EncodeMotherboardSync(int port, int fanIndex, bool sync)
    {
        byte address = (byte)((sync ? 0x80 : 0x00) | Address(port, fanIndex));
        return CommandPacket.Build(CmdMotherboardSync, address);
    }

    /// <summary>
    /// Per-fan light command. The payload carries a whole look - mode, brightness,
    /// speed, direction and up to four colours - which the controller then animates
    /// on-chip; there is no per-LED path and no separate commit.
    /// </summary>
    public static byte[] EncodeSetFanLight(
        int port, int fanIndex, byte mode, int brightness, int speed, int direction,
        ReadOnlySpan<byte> colorBytes, int colorCount, bool disabled, bool motherboardSync)
    {
        var payload = BuildLightPayload(mode, brightness, speed, direction, colorBytes, colorCount, disabled);
        payload[0] = (byte)(((port & 0x0F) << 4) | (motherboardSync ? 1 : 0));
        payload[1] = Address(port, fanIndex);
        return CommandPacket.Build(CmdSetFanLight, payload);
    }

    /// <summary>
    /// Group light command, addressing a group declared by <see cref="EncodeSetFanGroup"/>.
    /// Same payload as the per-fan command with the group number in place of the address.
    /// </summary>
    public static byte[] EncodeSetFanGroupLight(
        int group, byte mode, int brightness, int speed, int direction,
        ReadOnlySpan<byte> colorBytes, int colorCount, bool disabled)
    {
        var payload = BuildLightPayload(mode, brightness, speed, direction, colorBytes, colorCount, disabled);
        payload[0] = 0x00;
        payload[1] = (byte)group;
        return CommandPacket.Build(CmdSetFanGroupLight, payload);
    }

    /// <summary>
    /// Declares one group's membership: the group number, the member count, then one
    /// byte per fan. Bit 7 selects the fans' top halves, bit 6 the bottom halves, so
    /// a port needs two declarations before <see cref="EncodeSetFanGroupLight"/> can
    /// drive it.
    /// </summary>
    public static byte[] EncodeSetFanGroup(int group, int port, int fanCount, bool topHalf)
    {
        var payload = new byte[2 + fanCount];
        payload[0] = (byte)group;
        payload[1] = (byte)fanCount;
        byte halfBit = topHalf ? (byte)0x80 : (byte)0x40;
        for (int fan = 0; fan < fanCount; fan++)
        {
            payload[2 + fan] = (byte)(halfBit | Address(port, fan));
        }
        return CommandPacket.Build(CmdSetFanGroup, payload);
    }

    private static byte[] BuildLightPayload(
        byte mode, int brightness, int speed, int direction,
        ReadOnlySpan<byte> colorBytes, int colorCount, bool disabled)
    {
        var payload = new byte[LightPayloadLength];
        payload[2] = mode;
        payload[3] = (byte)Math.Clamp(brightness, 0, 4);
        payload[4] = (byte)Math.Clamp(speed, 0, 4);

        int count = Math.Clamp(colorCount, 0, MaxLightColors);
        if (count * 3 > colorBytes.Length)
        {
            count = colorBytes.Length / 3;
        }
        for (int i = 0; i < count * 3; i++)
        {
            payload[LightColorOffset + i] = colorBytes[i];
        }

        payload[17] = (byte)Math.Clamp(direction, 0, 5);
        payload[18] = disabled ? (byte)1 : (byte)0;
        payload[19] = (byte)count;
        return payload;
    }

    // Decodes the 3-byte records in a handshake reply payload.
    // No LINQ; safe to call in the per-poll path.
    public static List<TlFanReading> DecodeHandshake(ReadOnlySpan<byte> packet)
    {
        int payloadLen = CommandPacket.PayloadLengthOf(packet);
        int recordCount = payloadLen / 3;
        int payloadStart = CommandPacket.PayloadOffset;
        var readings = new List<TlFanReading>(recordCount);
        for (int i = 0; i < recordCount; i++)
        {
            int offset = payloadStart + i * 3;
            if (offset + 2 >= packet.Length)
            {
                break;
            }
            byte header = packet[offset];
            bool detected = (header & 0x80) != 0;
            if (!detected)
            {
                continue;
            }
            int port = (header >> 4) & 0x03;
            int fanIdx = header & 0x0F;
            int rpm = (packet[offset + 1] << 8) | packet[offset + 2];
            readings.Add(new TlFanReading(port, fanIdx, rpm));
        }
        return readings;
    }

    // Address byte: high nibble = port (0-3), low nibble = fanIdx.
    internal static byte Address(int port, int fanIndex) =>
        (byte)(((port & 0x0F) << 4) | (fanIndex & 0x0F));
}

public readonly struct TlFanReading
{
    public TlFanReading(int port, int fanIndex, int rpm)
    {
        Port = port;
        FanIndex = fanIndex;
        Rpm = rpm;
    }

    public int Port { get; }
    public int FanIndex { get; }
    public int Rpm { get; }
}
