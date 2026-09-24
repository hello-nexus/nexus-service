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
/// scheduled on a run loop, so the first <see cref="Read"/> starts a thread
/// that pumps CFRunLoopRunInMode in one-second slices; the callback queues each
/// report and Read waits on that queue. Removal (the callback, or the run
/// loop finishing because IOKit dropped the device source) marks the handle
/// gone so Read returns -1 and the caller tears down, as on Linux when hidraw
/// reports HUP.
/// </summary>
public sealed unsafe class MacHidDevice : IHidDevice
{
    private const int InputQueueCap = 64;
    private static readonly TimeSpan RunLoopStartTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RunLoopStopTimeout = TimeSpan.FromSeconds(3);

    private readonly object _lock = new();
    private readonly Queue<byte[]> _inputQueue = new();
    private readonly byte[] _inputBuffer;
    private GCHandle _inputBufferHandle;
    private GCHandle _selfHandle;
    private IntPtr _device;
    private IntPtr _runLoop;
    private IntPtr _runLoopMode;
    private Thread? _runLoopThread;
    private volatile bool _stopRequested;
    private bool _gone;
    private bool _disposed;

    public int VendorId { get; }
    public int ProductId { get; }
    public string Path { get; }
    public string? Serial { get; }
    public int UsagePage { get; }
    public int Usage { get; }

    internal MacHidDevice(IntPtr device, HidDeviceInfo info)
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
            if (_disposed)
            {
                return -1;
            }
            if (_runLoopThread is null && !_gone)
            {
                StartInputLoopLocked();
            }
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

    // Caller holds _lock.
    private void StartInputLoopLocked()
    {
        _inputBufferHandle = GCHandle.Alloc(_inputBuffer, GCHandleType.Pinned);
        _selfHandle = GCHandle.Alloc(this);
        var started = new ManualResetEventSlim(false);
        _runLoopThread = new Thread(() => RunInputLoop(started)) { IsBackground = true, Name = $"hid-mac-input:{Serial ?? Path}" };
        _runLoopThread.Start();
        if (!started.Wait(RunLoopStartTimeout))
        {
            ServiceLog.Warn($"[hid-mac] input run loop did not start ({Path})");
        }
    }

    private void RunInputLoop(ManualResetEventSlim started)
    {
        // The current thread's run loop is freed with the thread, so hold a reference for Dispose's CFRunLoopStop.
        _runLoop = CFRetain(CFRunLoopGetCurrent());
        _runLoopMode = CfString("kCFRunLoopDefaultMode");
        var context = GCHandle.ToIntPtr(_selfHandle);
        var device = _device;
        IOHIDDeviceRegisterInputReportCallback(device, (byte*)_inputBufferHandle.AddrOfPinnedObject(), _inputBuffer.Length,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, int, uint, byte*, nint, void>)&OnInputReport, context);
        IOHIDDeviceRegisterRemovalCallback(device,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, void>)&OnRemoved, context);
        IOHIDDeviceScheduleWithRunLoop(device, _runLoop, _runLoopMode);
        started.Set();
        try
        {
            while (!_stopRequested)
            {
                if (CFRunLoopRunInMode(_runLoopMode, 1.0, false) == KCfRunLoopRunFinished)
                {
                    break;
                }
            }
        }
        finally
        {
            // Nothing may reach the callbacks or the pinned buffer once this thread is gone.
            IOHIDDeviceRegisterInputReportCallback(device, (byte*)_inputBufferHandle.AddrOfPinnedObject(), _inputBuffer.Length, IntPtr.Zero, IntPtr.Zero);
            IOHIDDeviceRegisterRemovalCallback(device, IntPtr.Zero, IntPtr.Zero);
            IOHIDDeviceUnscheduleFromRunLoop(device, _runLoop, _runLoopMode);
            lock (_lock)
            {
                _gone = true;
                Monitor.PulseAll(_lock);
            }
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
        Thread? loopThread;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            loopThread = _runLoopThread;
            Monitor.PulseAll(_lock);
        }
        var loopStopped = true;
        if (loopThread is not null)
        {
            _stopRequested = true;
            if (_runLoop != IntPtr.Zero)
            {
                CFRunLoopStop(_runLoop);
            }
            loopStopped = loopThread.Join(RunLoopStopTimeout);
        }
        if (!loopStopped)
        {
            // The loop still owns the device source, the pinned buffer and the callback context: leaking them beats a use after free.
            ServiceLog.Warn($"[hid-mac] input run loop did not stop; leaking the handle ({Path})");
            return;
        }
        var device = _device;
        _device = IntPtr.Zero;
        if (device != IntPtr.Zero)
        {
            IOHIDDeviceClose(device, 0);
            CFRelease(device);
        }
        if (_runLoop != IntPtr.Zero)
        {
            CFRelease(_runLoop);
            _runLoop = IntPtr.Zero;
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
