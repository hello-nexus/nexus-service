using System;

namespace Nexus.Service.Peripherals.Hid;

/// <summary>
/// Minimal HID device abstraction: feature reports plus interrupt read/write,
/// the transports the first-party device protocols are built on.
/// </summary>
public interface IHidDevice : IDisposable
{
    int VendorId { get; }
    int ProductId { get; }
    string Path { get; }
    string? Serial { get; }
    int UsagePage { get; }
    int Usage { get; }

    /// <summary>Sends a feature report (typically used for vendor protocols). Byte 0 is report ID.</summary>
    bool SetFeature(ReadOnlySpan<byte> report);

    /// <summary>Reads a feature report into the provided buffer. Byte 0 is report ID on input.</summary>
    bool GetFeature(Span<byte> buffer);

    /// <summary>
    /// Reads an input report via the control path (IOCTL_HID_GET_INPUT_REPORT on Windows).
    /// Byte 0 is report ID on input. Use after a feature-report primer that triggers the device
    /// to latch telemetry into the input report; differs from <see cref="Read"/> which is interrupt-IN.
    /// </summary>
    bool GetInputReport(Span<byte> buffer);

    /// <summary>Writes an output report (interrupt OUT). Byte 0 is report ID.</summary>
    bool Write(ReadOnlySpan<byte> report);

    /// <summary>OS error code of the most recent failed <see cref="Write"/> (Win32 on Windows); 0 when none is recorded; -1 handle closing, -2 zero bytes transferred.</summary>
    int LastWriteError => 0;

    /// <summary>
    /// Sends an output report via the control path (HidD_SetOutputReport /
    /// SET_REPORT(Output)), not the interrupt OUT pipe. Byte 0 is report ID.
    /// For vendor reports a device rejects on the interrupt-OUT pipe.
    /// </summary>
    bool SetOutputReport(ReadOnlySpan<byte> report);

    /// <summary>
    /// Reads an input report (interrupt IN), waiting up to <paramref name="timeoutMs"/>.
    /// Returns the bytes read (&gt;0), 0 on idle/timeout, or a negative value when the
    /// device is gone or the read failed - the caller must close and reopen rather
    /// than retrying in a tight loop (a non-blocking failure otherwise pegs a core).
    /// </summary>
    int Read(Span<byte> buffer, int timeoutMs);
}
