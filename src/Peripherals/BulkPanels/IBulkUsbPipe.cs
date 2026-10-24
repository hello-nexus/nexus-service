using System;
using System.Threading;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// A USB bulk endpoint pair for one panel. Kept behind an interface so the driver logic
/// and its tests build and run everywhere; the transports are WinUSB and macOS IOUSBHost.
/// </summary>
public interface IBulkUsbPipe : IDisposable
{
    /// <summary>Writes one bulk transfer on the OUT pipe. The caller decides the framing.</summary>
    bool Write(ReadOnlySpan<byte> data);

    /// <summary>Writes one bulk transfer on another OUT pipe of the same interface.</summary>
    bool Write(byte pipeId, ReadOnlySpan<byte> data);

    /// <summary>
    /// Writes one bulk transfer and returns how many bytes the device took, also after a timeout when the
    /// transport can tell; -1 when that is unknown.
    /// </summary>
    int WriteCounted(byte pipeId, ReadOnlySpan<byte> data) => Write(pipeId, data) ? data.Length : -1;

    /// <summary>
    /// As <see cref="WriteCounted(byte, ReadOnlySpan{byte})"/>, waiting up to <paramref name="timeoutMs"/> for the device to
    /// take it; <paramref name="ct"/> ends the wait, and a write it cancels before starting returns 0.
    /// </summary>
    int WriteCounted(byte pipeId, ReadOnlySpan<byte> data, int timeoutMs, CancellationToken ct) =>
        ct.IsCancellationRequested ? 0 : WriteCounted(pipeId, data);

    /// <summary>
    /// Reads one bulk transfer from the IN pipe. Returns the byte count, 0 on timeout,
    /// or -1 when the pipe is gone.
    /// </summary>
    int Read(Span<byte> buffer, int timeoutMs);

    /// <summary>Reads one bulk transfer from another IN pipe of the same interface; -1 when the transport has none.</summary>
    int Read(byte pipeId, Span<byte> buffer, int timeoutMs) => -1;

    /// <summary>Clears a halt a previous owner left on a pipe; false when the transport cannot.</summary>
    bool ClearHalt(byte pipeId) => false;

    /// <summary>USB product id of the opened device, for a driver that claims several; 0 when unknown.</summary>
    int ProductId => 0;

    /// <summary>True once the transport knows the device behind this pipe is gone; a fast re-enumeration slips between presence scans.</summary>
    bool IsGone => false;
}

/// <summary>
/// Opens the bulk pipes for a device, or returns null when it is not reachable that way.
///
/// The common reason for null on Windows is not absence: these panels only expose bulk
/// endpoints to us when WinUSB is bound to them. A cooler still driven by its vendor's own
/// kernel driver enumerates fine and cannot be opened here at all.
/// </summary>
public interface IBulkUsbPipeFactory
{
    IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId);

    /// <summary>
    /// Same as <see cref="Open(int, int, byte, byte)"/>, and reports whether a null result
    /// came from the OS refusing access to a present device.
    /// </summary>
    IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId, out bool accessDenied)
    {
        accessDenied = false;
        return Open(vendorId, productId, writePipeId, readPipeId);
    }
}

/// <summary>A bulk pipe open that failed at the OS layer, keeping the Win32 error.</summary>
public sealed class BulkPipeOpenException : IOException
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;

    public BulkPipeOpenException(string message, int errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public int ErrorCode { get; }

    public bool IsAccessDenied => IsAccessDeniedCode(ErrorCode);

    public static bool IsAccessDeniedCode(int errorCode) =>
        errorCode is ErrorAccessDenied or ErrorSharingViolation;
}

/// <summary>Stands in on Linux, which has no bulk-pipe transport, so the rest of the graph still resolves.</summary>
public sealed class NullBulkUsbPipeFactory : IBulkUsbPipeFactory
{
    public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId) => null;
}
