#if WINDOWS
using System;
using System.Runtime.InteropServices;
using System.Threading;
using NativeApi = Nexus.Service.Peripherals.Hid.WindowsHidEnumerator.Native;

namespace Nexus.Service.Peripherals.Hid;

/// <summary>
/// Windows HID device handle wrapper. Feature reports use the synchronous HidD_* API.
/// Interrupt-IN reads use overlapped I/O when the handle was opened <c>forInput</c>,
/// so <see cref="Read"/> honors its timeout (waits on the completion event, cancels
/// on timeout) instead of returning instantly and letting the caller spin a core.
/// </summary>
public sealed class WindowsHidDevice : IHidDevice
{
    private const int ERROR_IO_PENDING = 997;
    private const uint WAIT_OBJECT_0 = 0x0;
    private const uint WAIT_TIMEOUT = 0x102;
    private const uint INFINITE = 0xFFFFFFFF;

    private const int WriteTimeoutMs = 1000;

    private IntPtr _handle;
    private readonly bool _overlapped;
    private readonly IntPtr _readEvent;  // manual-reset completion event; only set when overlapped
    private readonly IntPtr _writeEvent; // separate completion event for overlapped writes
    private readonly byte[] _readBuf;   // reused per read, sized to the input report length
    // HIDP_CAPS report lengths; every control-path call pads to them (see HidReportPadding).
    private readonly int _inputReportLen;
    private readonly int _outputReportLen;
    private readonly int _featureReportLen;
    public int VendorId { get; }
    public int ProductId { get; }
    public string Path { get; }
    public string? Serial { get; }
    public int UsagePage { get; }
    public int Usage { get; }

    internal WindowsHidDevice(IntPtr handle, string path, int vid, int pid, string? serial,
        int usagePage, int usage, int inputReportLen, int outputReportLen, int featureReportLen, bool overlapped)
    {
        _handle = handle;
        Path = path;
        VendorId = vid;
        ProductId = pid;
        Serial = serial;
        UsagePage = usagePage;
        Usage = usage;
        _overlapped = overlapped;
        _inputReportLen = inputReportLen;
        _outputReportLen = outputReportLen;
        _featureReportLen = featureReportLen;
        // ReadFile needs a buffer >= the device's input report length or it fails
        // immediately with ERROR_INVALID_USER_BUFFER (which is what turned the input
        // read loop into a 100%-core spin). Size once, reuse; 64-byte floor covers
        // devices whose caps didn't enumerate.
        _readBuf = new byte[Math.Max(inputReportLen, 64)];
        if (overlapped)
        {
            _readEvent = NativeApi.CreateEventW(IntPtr.Zero, true, false, null);
            _writeEvent = NativeApi.CreateEventW(IntPtr.Zero, true, false, null);
        }
    }

    public bool SetFeature(ReadOnlySpan<byte> report)
    {
        if (_handle == IntPtr.Zero) return false;
        var buf = HidReportPadding.Pad(report, _featureReportLen);
        return NativeApi.HidD_SetFeature(_handle, buf, (uint)buf.Length);
    }

    public bool GetFeature(Span<byte> buffer)
    {
        if (_handle == IntPtr.Zero) return false;
        var buf = new byte[Math.Max(buffer.Length, _featureReportLen)];
        buf[0] = buffer[0]; // report id must be preset on input
        var ok = NativeApi.HidD_GetFeature(_handle, buf, (uint)buf.Length);
        if (ok)
        {
            buf.AsSpan(0, buffer.Length).CopyTo(buffer);
        }
        return ok;
    }

    public bool GetInputReport(Span<byte> buffer)
    {
        if (_handle == IntPtr.Zero) return false;
        var buf = new byte[Math.Max(buffer.Length, _inputReportLen)];
        buf[0] = buffer[0]; // report id must be preset on input
        var ok = NativeApi.HidD_GetInputReport(_handle, buf, (uint)buf.Length);
        if (ok)
        {
            buf.AsSpan(0, buffer.Length).CopyTo(buffer);
        }
        return ok;
    }

    public bool Write(ReadOnlySpan<byte> report)
    {
        if (_handle == IntPtr.Zero) return false;
        var buf = HidReportPadding.Pad(report, _outputReportLen);
        if (!_overlapped)
        {
            return NativeApi.WriteFile(_handle, buf, (uint)buf.Length, out _, IntPtr.Zero);
        }
        return WriteOverlapped(buf);
    }

    // A FILE_FLAG_OVERLAPPED handle (opened forInput for interrupt-IN reads) rejects a
    // synchronous WriteFile, so the output report must be issued overlapped and waited on.
    private unsafe bool WriteOverlapped(byte[] buf)
    {
        fixed (byte* pbuf = buf)
        {
            var ov = default(NativeOverlapped);
            ov.EventHandle = _writeEvent;
            NativeApi.ResetEvent(_writeEvent);

            if (!NativeApi.WriteFileOverlapped(_handle, pbuf, (uint)buf.Length, IntPtr.Zero, &ov))
            {
                var err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING) return false;

                var wait = NativeApi.WaitForSingleObject(_writeEvent, WriteTimeoutMs);
                if (wait != WAIT_OBJECT_0)
                {
                    NativeApi.CancelIo(_handle);
                    NativeApi.GetOverlappedResult(_handle, &ov, out _, true);
                    return false;
                }
            }

            if (!NativeApi.GetOverlappedResult(_handle, &ov, out var transferred, false)) return false;
            return transferred > 0;
        }
    }

    public bool SetOutputReport(ReadOnlySpan<byte> report)
    {
        if (_handle == IntPtr.Zero) return false;
        var buf = HidReportPadding.Pad(report, _outputReportLen);
        return NativeApi.HidD_SetOutputReport(_handle, buf, (uint)buf.Length);
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (_handle == IntPtr.Zero) return -1;
        return _overlapped ? ReadOverlapped(buffer, timeoutMs) : ReadBlocking(buffer);
    }

    // Overlapped interrupt-IN read: issue the read, wait up to timeoutMs on the
    // completion event, cancel on timeout. >0 = bytes read; 0 = idle/timeout;
    // -1 = device gone (caller tears the handle down instead of spinning).
    private unsafe int ReadOverlapped(Span<byte> buffer, int timeoutMs)
    {
        fixed (byte* pbuf = _readBuf)
        {
            var ov = default(NativeOverlapped);
            ov.EventHandle = _readEvent;
            NativeApi.ResetEvent(_readEvent);

            if (!NativeApi.ReadFileOverlapped(_handle, pbuf, (uint)_readBuf.Length, IntPtr.Zero, &ov))
            {
                var err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING) return -1; // immediate failure → device gone

                var wait = NativeApi.WaitForSingleObject(_readEvent, timeoutMs < 0 ? INFINITE : (uint)timeoutMs);
                if (wait == WAIT_TIMEOUT)
                {
                    // Nothing arrived in the window. Cancel and drain so the
                    // OVERLAPPED/buffer are free before the next iteration.
                    NativeApi.CancelIo(_handle);
                    NativeApi.GetOverlappedResult(_handle, &ov, out _, true);
                    return 0;
                }
                if (wait != WAIT_OBJECT_0)
                {
                    NativeApi.CancelIo(_handle);
                    NativeApi.GetOverlappedResult(_handle, &ov, out _, true);
                    return -1;
                }
            }

            if (!NativeApi.GetOverlappedResult(_handle, &ov, out var transferred, false)) return -1;
            var n = (int)transferred;
            if (n <= 0) return 0;
            var copy = Math.Min(n, buffer.Length);
            _readBuf.AsSpan(0, copy).CopyTo(buffer);
            return copy;
        }
    }

    // Synchronous read on a feature-report (non-overlapped) handle. Used only for the
    // settings/device-info read-back, which is always preceded by a feature trigger so
    // a report is already pending; ReadFile blocks until it arrives. -1 on failure.
    private int ReadBlocking(Span<byte> buffer)
    {
        if (!NativeApi.ReadFile(_handle, _readBuf, (uint)_readBuf.Length, out var read, IntPtr.Zero)) return -1;
        var n = (int)read;
        if (n <= 0) return 0;
        var copy = Math.Min(n, buffer.Length);
        _readBuf.AsSpan(0, copy).CopyTo(buffer);
        return copy;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero && _handle != (IntPtr)(-1))
        {
            NativeApi.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
        if (_readEvent != IntPtr.Zero)
        {
            NativeApi.CloseHandle(_readEvent);
        }
        if (_writeEvent != IntPtr.Zero)
        {
            NativeApi.CloseHandle(_writeEvent);
        }
    }
}
#endif
