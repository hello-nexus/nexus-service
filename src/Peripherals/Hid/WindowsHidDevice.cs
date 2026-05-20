#if WINDOWS
using System;
using System.Runtime.InteropServices;

namespace Qos.Service.Peripherals.Hid;

/// <summary>Windows HID device handle wrapper. Non-overlapped feature-report API.</summary>
public sealed class WindowsHidDevice : IHidDevice
{
    private IntPtr _handle;
    public int VendorId { get; }
    public int ProductId { get; }
    public string Path { get; }
    public string? Serial { get; }
    public int UsagePage { get; }
    public int Usage { get; }

    internal WindowsHidDevice(IntPtr handle, string path, int vid, int pid, string? serial, int usagePage, int usage)
    {
        _handle = handle;
        Path = path;
        VendorId = vid;
        ProductId = pid;
        Serial = serial;
        UsagePage = usagePage;
        Usage = usage;
    }

    public bool SetFeature(ReadOnlySpan<byte> report)
    {
        if (_handle == IntPtr.Zero) return false;
        var buf = report.ToArray();
        return WindowsHidEnumerator.Native.HidD_SetFeature(_handle, buf, (uint)buf.Length);
    }

    public bool GetFeature(Span<byte> buffer)
    {
        if (_handle == IntPtr.Zero) return false;
        var buf = new byte[buffer.Length];
        buf[0] = buffer[0]; // report id must be preset on input
        var ok = WindowsHidEnumerator.Native.HidD_GetFeature(_handle, buf, (uint)buf.Length);
        if (ok)
        {
            buf.AsSpan().CopyTo(buffer);
        }
        return ok;
    }

    public bool Write(ReadOnlySpan<byte> report)
    {
        if (_handle == IntPtr.Zero) return false;
        var buf = report.ToArray();
        return WindowsHidEnumerator.Native.WriteFile(_handle, buf, (uint)buf.Length, out _, IntPtr.Zero);
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (_handle == IntPtr.Zero) return 0;

        // The handle is opened in synchronous mode (no FILE_FLAG_OVERLAPPED),
        // so ReadFile blocks until data arrives or the device is removed. For
        // real timeouts we kick a watchdog on another thread that calls
        // CancelIoEx after timeoutMs - that aborts the synchronous read with
        // ERROR_OPERATION_ABORTED and ReadFile returns false. Without this
        // path, a wrong-interface read (or a device that just didn't respond)
        // hangs forever - surfaced as /keeb/state requests sitting on the
        // wire indefinitely. timeoutMs <= 0 keeps the legacy blocking
        // behaviour for callers that don't want a deadline.
        var buf = new byte[buffer.Length];
        System.Threading.CancellationTokenSource? cts = null;
        var capturedHandle = _handle;

        if (timeoutMs > 0)
        {
            cts = new System.Threading.CancellationTokenSource();
            var token = cts.Token;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try { await System.Threading.Tasks.Task.Delay(timeoutMs, token); }
                catch (System.OperationCanceledException) { return; }
                if (capturedHandle != IntPtr.Zero && capturedHandle != (IntPtr)(-1))
                {
                    WindowsHidEnumerator.Native.CancelIoEx(capturedHandle, IntPtr.Zero);
                }
            });
        }

        var ok = WindowsHidEnumerator.Native.ReadFile(capturedHandle, buf, (uint)buf.Length, out var read, IntPtr.Zero);
        cts?.Cancel();
        cts?.Dispose();

        if (!ok) return 0;
        buf.AsSpan(0, (int)read).CopyTo(buffer);
        return (int)read;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero && _handle != (IntPtr)(-1))
        {
            WindowsHidEnumerator.Native.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
#endif
