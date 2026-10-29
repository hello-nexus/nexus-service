using System;
using System.Buffers.Binary;

namespace Nexus.Service.Lighting.Rgb;

/// <param name="Failures">Cumulative failed hardware writes the daemon made for the controller since it started.</param>
/// <param name="Successes">Cumulative successful hardware writes.</param>
/// <param name="MsSinceLastFailure"><see cref="NexusWriteHealthProtocol.NeverFailed"/> when the controller never failed a write.</param>
public readonly record struct OpenRgbWriteHealth(uint Failures, uint Successes, uint MsSinceLastFailure);

public enum OpenRgbWriteHealthOutcome
{
    Ok,
    /// <summary>The daemon ACKed the packet as unsupported: an upstream build.</summary>
    Unsupported,
    /// <summary>The daemon does not know this controller id (it left in a rescan); other controllers are unaffected.</summary>
    UnknownController,
    /// <summary>No answer, a refusal other than unsupported, or an unreadable reply.</summary>
    Failed,
}

public readonly record struct OpenRgbWriteHealthResult(OpenRgbWriteHealthOutcome Outcome, OpenRgbWriteHealth Health);

/// <summary>The Nexus write-health packet of the bundled daemon, addressed by controller address.</summary>
public interface IOpenRgbWriteHealthTransport
{
    Task<OpenRgbWriteHealthResult> GetWriteHealthAsync(int address, CancellationToken ct = default);
}

/// <summary>Stands in when the RGB controller has no write-health packet, so the poller reports nothing.</summary>
public sealed class NoOpenRgbWriteHealthTransport : IOpenRgbWriteHealthTransport
{
    public Task<OpenRgbWriteHealthResult> GetWriteHealthAsync(int address, CancellationToken ct = default) =>
        Task.FromResult(new OpenRgbWriteHealthResult(OpenRgbWriteHealthOutcome.Unsupported, default));
}

/// <summary>
/// Reply of the Nexus write-health packet: three little-endian uint32 (failures,
/// successes, ms since the last failure). The daemon side lives in the headless
/// OpenRGB fork; change both together.
/// </summary>
public static class NexusWriteHealthProtocol
{
    public const uint NeverFailed = 0xFFFFFFFFu;
    public const int ReplySize = 12;

    public static bool TryParse(ReadOnlySpan<byte> body, out OpenRgbWriteHealth health)
    {
        health = default;
        if (body.Length < ReplySize)
        {
            return false;
        }
        health = new OpenRgbWriteHealth(
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(8, 4)));
        return true;
    }
}
