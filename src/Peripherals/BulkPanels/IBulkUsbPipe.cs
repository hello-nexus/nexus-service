using System;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// A USB bulk endpoint pair for one panel. Kept behind an interface so the driver logic
/// and its tests build and run everywhere, while the only real implementation is WinUSB
/// and Windows-only.
/// </summary>
public interface IBulkUsbPipe : IDisposable
{
    /// <summary>Writes one bulk transfer on the OUT pipe. The caller decides the framing.</summary>
    bool Write(ReadOnlySpan<byte> data);

    /// <summary>Writes one bulk transfer on another OUT pipe of the same interface.</summary>
    bool Write(byte pipeId, ReadOnlySpan<byte> data);

    /// <summary>
    /// Reads one bulk transfer from the IN pipe. Returns the byte count, 0 on timeout,
    /// or -1 when the pipe is gone.
    /// </summary>
    int Read(Span<byte> buffer, int timeoutMs);
}

/// <summary>
/// Opens the bulk pipes for a device, or returns null when it is not reachable that way.
///
/// The common reason for null is not absence: these panels only expose bulk endpoints to
/// us when Windows has bound WinUSB to them. A cooler still driven by its vendor's own
/// kernel driver enumerates fine and cannot be opened here at all.
/// </summary>
public interface IBulkUsbPipeFactory
{
    IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId);
}

/// <summary>Stands in on platforms with no WinUSB, so the rest of the graph still resolves.</summary>
public sealed class NullBulkUsbPipeFactory : IBulkUsbPipeFactory
{
    public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId) => null;
}
