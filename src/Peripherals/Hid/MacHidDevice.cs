using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Service.Platform;
using static Nexus.Service.Peripherals.Hid.MacHidNative;

namespace Nexus.Service.Peripherals.Hid;

/// <summary>
/// macOS <see cref="IHidDevice"/> over one opened IOHIDDeviceRef. Reports keep
/// the hidapi convention: byte 0 is the report id, and a report id of 0 is
/// not sent on the wire. IOKit delivers input reports only through a callback
/// scheduled on a run loop, so a handle opened forInput runs its own thread
/// pumping CFRunLoopRun; the callback queues each report and <see cref="Read"/>
/// waits on that queue. Removal flips the handle to gone so Read returns -1
/// and the caller tears down, as on Linux when hidraw reports HUP.
/// </summary>
public sealed unsafe class MacHidDevice : IHidDevice
{
    private const int InputQueueCap = 64;
    private static readonly TimeSpan RunLoopStartTimeout = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly Queue<byte[]> _inputQueue = new();
    private readonly byte[] _inputBuffer;
    private GCHandle _inputBufferHandle;
    private GCHandle _selfHandle;
    private IntPtr _device;
    private IntPtr _runLoop;
    private IntPtr _runLoopMode;
    private Thread? _runLoopThread;
    private bool _gone;
    private bool _disposed;

    public int VendorId { get; }
    public int ProductId { get; }
    public string Path { get; }
    public string? Serial { get; }
    public int UsagePage { get; }
    public int Usage { get; }

    internal MacHidDevice(IntPtr device, HidDeviceInfo info, bool forInput)
    {
        _device = device;
        VendorId = info.VendorId;
        ProductId = info.ProductId;
        Path = info.Path;
        Serial = info.Serial;
        UsagePage = info.UsagePage;
        Usage = info.Usage;
        // The report buffer IOKit fills must be at least the device's largest input report.
        _inputBuffer = new byte[Math.Max(info.InputReportByteLength, 64)];
        if (forInput)
        {
            StartInputLoop();
        }
    }

    public bool SetFeature(ReadOnlySpan<byte> report) => SetReport(ReportTypeFeature, report);

    public bool SetOutputReport(ReadOnlySpan<byte> report) => SetReport(ReportTypeOutput, report);

    // IOKit routes an output report through the interrupt OUT pipe when the device has one.
    public bool Write(ReadOnlySpan<byte> report) => SetReport(ReportTypeOutput, report);

    public bool GetFeature(Span<byte> buffer) => GetReport(ReportTypeFeature, buffer);

    public bool GetInputReport(Span<byte> buffer) => GetReport(ReportTypeInput, buffer);

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (buffer.Length == 0)
        {
            return -1;
        }
        lock (_lock)
        {
            if (_inputQueue.Count == 0 && !_gone && !_disposed)
            {
                Monitor.Wait(_lock, Math.Max(timeoutMs, 0));
            }
            if (_inputQueue.Count == 0)
            {
                return _gone || _disposed ? -1 : 0;
            }
            var report = _inputQueue.Dequeue();
            var n = Math.Min(report.Length, buffer.Length);
            report.AsSpan(0, n).CopyTo(buffer);
            return n;
        }
    }

    private bool SetReport(int type, ReadOnlySpan<byte> report)
    {
        if (report.Length == 0)
        {
            return false;
        }
        var device = _device;
        if (device == IntPtr.Zero || _gone)
        {
            return false;
        }
        var reportId = report[0];
        var payload = reportId == 0 ? report[1..] : report;
        if (payload.Length == 0)
        {
            return false;
        }
        fixed (byte* p = payload)
        {
            return IOHIDDeviceSetReport(device, type, reportId, p, payload.Length) == KIoReturnSuccess;
        }
    }

    private bool GetReport(int type, Span<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return false;
        }
        var device = _device;
        if (device == IntPtr.Zero || _gone)
        {
            return false;
        }
        var reportId = buffer[0];
        var payload = reportId == 0 ? buffer[1..] : buffer;
        if (payload.Length == 0)
        {
            return false;
        }
        nint length = payload.Length;
        fixed (byte* p = payload)
        {
            return IOHIDDeviceGetReport(device, type, reportId, p, &length) == KIoReturnSuccess;
        }
    }

    private void StartInputLoop()
    {
        _inputBufferHandle = GCHandle.Alloc(_inputBuffer, GCHandleType.Pinned);
        _selfHandle = GCHandle.Alloc(this);
        using var started = new ManualResetEventSlim(false);
        _runLoopThread = new Thread(() =>
        {
            _runLoop = CFRunLoopGetCurrent();
            _runLoopMode = CfString("kCFRunLoopDefaultMode");
            var context = GCHandle.ToIntPtr(_selfHandle);
            IOHIDDeviceRegisterInputReportCallback(_device, (byte*)_inputBufferHandle.AddrOfPinnedObject(), _inputBuffer.Length,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, int, uint, byte*, nint, void>)&OnInputReport, context);
            IOHIDDeviceRegisterRemovalCallback(_device,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void>)&OnRemoved, context);
            IOHIDDeviceScheduleWithRunLoop(_device, _runLoop, _runLoopMode);
            started.Set();
            CFRunLoopRun();
            IOHIDDeviceUnscheduleFromRunLoop(_device, _runLoop, _runLoopMode);
        })
        { IsBackground = true, Name = $"hid-mac-input:{Serial ?? Path}" };
        _runLoopThread.Start();
        if (!started.Wait(RunLoopStartTimeout))
        {
            ServiceLog.Warn($"[hid-mac] input run loop did not start ({Path})");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnInputReport(IntPtr context, int result, IntPtr sender, int type, uint reportId, byte* report, nint length)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not MacHidDevice self || result != KIoReturnSuccess || length <= 0)
            {
                return;
            }
            var copy = new byte[(int)length];
            new ReadOnlySpan<byte>(report, (int)length).CopyTo(copy);
            lock (self._lock)
            {
                if (self._inputQueue.Count >= InputQueueCap)
                {
                    self._inputQueue.Dequeue();
                }
                self._inputQueue.Enqueue(copy);
                Monitor.PulseAll(self._lock);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[hid-mac] input callback failed: {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnRemoved(IntPtr context, int result, IntPtr sender)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is not MacHidDevice self)
            {
                return;
            }
            lock (self._lock)
            {
                self._gone = true;
                Monitor.PulseAll(self._lock);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[hid-mac] removal callback failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Monitor.PulseAll(_lock);
        }
        if (_runLoopThread is not null)
        {
            if (_runLoop != IntPtr.Zero)
            {
                CFRunLoopStop(_runLoop);
            }
            _runLoopThread.Join(RunLoopStartTimeout);
        }
        var device = _device;
        _device = IntPtr.Zero;
        if (device != IntPtr.Zero)
        {
            IOHIDDeviceClose(device, 0);
            CFRelease(device);
        }
        if (_runLoopMode != IntPtr.Zero)
        {
            CFRelease(_runLoopMode);
            _runLoopMode = IntPtr.Zero;
        }
        if (_inputBufferHandle.IsAllocated)
        {
            _inputBufferHandle.Free();
        }
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }
}
