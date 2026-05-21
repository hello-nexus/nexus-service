using System;

namespace Qos.Service.Peripherals.Hyte.Np50;

/// <summary>
/// Raw byte transport to a HYTE serial-over-USB hub (NP50, MiniHub, etc.).
/// Sits between the per-hub protocol module (pure builders/parsers) and the
/// actual OS-level serial port. Exposed as an interface so the heartbeat
/// workers and tests can swap in fakes.
/// </summary>
public interface INp50Transport : IDisposable
{
    /// <summary>True iff the underlying port is open and responsive.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Stable identifier for this transport, suitable for use as a device-id
    /// prefix (the NP50 serial reported by USB enumeration, or a fallback path).
    /// </summary>
    string Serial { get; }

    /// <summary>Send raw bytes to the hub. Blocks until the OS accepts the write.</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>
    /// Drop any unread bytes sitting in the OS input buffer. Called before
    /// each request to guard against a previous read that didn't drain — if
    /// PollHubInfo's 20-byte read happens to leave 4 bytes behind, the next
    /// command's response would otherwise be parsed off-by-4.
    /// </summary>
    void DiscardInput();

    /// <summary>
    /// Read up to <paramref name="buffer"/>.Length bytes, returning the number actually read.
    /// Returns 0 on timeout. Throws if the port has been disposed/disconnected.
    /// </summary>
    int Read(Span<byte> buffer, int timeoutMs);
}

/// <summary>
/// Discovers attached NP50 hubs at the OS layer. Windows uses SetupAPI to
/// find COM ports with hardware id <c>USB\VID_3402&amp;PID_0901</c>; non-Windows
/// returns empty for v1.
/// </summary>
public interface INp50PortDiscovery
{
    IReadOnlyList<Np50PortInfo> Discover();
}

/// <summary>One NP50 hub as seen by the OS, before we open it.</summary>
public sealed class Np50PortInfo
{
    /// <summary>Serial COM port name, e.g. "COM5" on Windows.</summary>
    public required string PortName { get; init; }

    /// <summary>USB serial number string parsed from the hardware id, or empty when unavailable.</summary>
    public string Serial { get; init; } = "";
}
