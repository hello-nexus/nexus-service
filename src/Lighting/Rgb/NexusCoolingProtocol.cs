using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>One fan or pump port of a Commander Core as the daemon reports it.</summary>
/// <param name="AppliedDuty">The duty the daemon last wrote to the device; null until it wrote one.</param>
public readonly record struct CommanderCoreChannel(bool Connected, int Rpm, int? AppliedDuty);

public readonly record struct CommanderCoreTemperature(bool Connected, float Celsius);

/// <param name="Valid">False when the daemon has no fresh reading, which is the "no data" state.</param>
/// <param name="AgeMs">Milliseconds since the daemon's last good poll; saturates.</param>
public sealed record CommanderCoreStatus(
    bool Valid,
    int ProductId,
    int AgeMs,
    IReadOnlyList<CommanderCoreChannel> Channels,
    IReadOnlyList<CommanderCoreTemperature> Temperatures);

/// <summary>
/// Payloads of the Nexus cooling packets the bundled daemon serves for the Corsair
/// Commander Core family. Little-endian. The daemon side is
/// CorsairCommanderCoreCooling.h in the headless OpenRGB fork; change both together.
/// </summary>
public static class NexusCoolingProtocol
{
    public const byte WireVersion = 1;
    public const int MaxChannels = 8;
    public const int MaxTemperatures = 8;

    private const byte FlagValid = 0x01;
    private const byte NoDuty = 0xFF;
    private const int StatusHeaderSize = 7;
    private const int ChannelSize = 4;
    private const int TemperatureSize = 3;

    /// <summary>Status reply: version, flags, product id, age, channel list, temperature list.</summary>
    public static bool TryParseStatus(ReadOnlySpan<byte> body, out CommanderCoreStatus? status)
    {
        status = null;
        if (body.Length < StatusHeaderSize + 1 || body[0] != WireVersion)
        {
            return false;
        }
        var valid = (body[1] & FlagValid) != 0;
        var pid = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2));
        var age = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(4, 2));
        int channelCount = body[6];
        if (channelCount > MaxChannels || body.Length < StatusHeaderSize + channelCount * ChannelSize + 1)
        {
            return false;
        }

        var channels = new CommanderCoreChannel[channelCount];
        var pos = StatusHeaderSize;
        for (var i = 0; i < channelCount; i++)
        {
            var duty = body[pos + 3];
            channels[i] = new CommanderCoreChannel(
                body[pos] != 0,
                BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(pos + 1, 2)),
                duty == NoDuty ? null : duty);
            pos += ChannelSize;
        }

        int temperatureCount = body[pos++];
        if (temperatureCount > MaxTemperatures || body.Length != pos + temperatureCount * TemperatureSize)
        {
            return false;
        }
        var temperatures = new CommanderCoreTemperature[temperatureCount];
        for (var i = 0; i < temperatureCount; i++)
        {
            var tenths = BinaryPrimitives.ReadInt16LittleEndian(body.Slice(pos + 1, 2));
            temperatures[i] = new CommanderCoreTemperature(body[pos] != 0, tenths / 10f);
            pos += TemperatureSize;
        }

        status = new CommanderCoreStatus(valid, pid, age, channels, temperatures);
        return true;
    }

    /// <summary>
    /// Set-duties request: the complete duty table for the device, one entry per
    /// connected channel. The daemon replaces its commanded set and writes the table
    /// to the device only when it differs from what it wrote last.
    /// </summary>
    public static byte[] BuildSetDutiesBody(IReadOnlyList<(int Channel, int Duty)> duties)
    {
        var body = new byte[2 + duties.Count * 2];
        body[0] = WireVersion;
        body[1] = (byte)duties.Count;
        for (var i = 0; i < duties.Count; i++)
        {
            body[2 + i * 2] = (byte)duties[i].Channel;
            body[3 + i * 2] = (byte)Math.Clamp(duties[i].Duty, 0, 100);
        }
        return body;
    }
}
