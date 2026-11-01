#if WINDOWS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Nexus.Service.Peripherals.IoSafety;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>
/// <see cref="ITryxPanoramaTransport"/> for the RK-firmware Panorama (VID 0x391A),
/// which binds to the Windows usbprint device class rather than CDC serial. Opened
/// with CreateFileW against the usbprint device interface path.
/// The panel replies on its IN endpoint after writes; if the host never reads that
/// endpoint the pipe backs up and the usbprint stack resets the interface, killing
/// the write handle every few tens of seconds (observed: a write-only loop dies at
/// 20-70s, a loop that also drains the reads survives indefinitely). So the handle
/// is opened overlapped and a background loop continuously reads the IN endpoint, which
/// also carries the replies (file list, device info, file pull chunks).
/// The handle is a <see cref="SafeFileHandle"/> owned by the <see cref="FileStream"/>
/// so an in-flight write can't race a concurrent Dispose onto a recycled handle.
/// </summary>
public sealed class WindowsTryxRkTransport : ITryxPanoramaTransport
{
    private readonly FileStream _stream;
    private readonly object _writeLock = new();
    private readonly CancellationTokenSource _drainCts = new();
    private readonly Thread _drainThread;
    private bool _disposed;
    private volatile IReadOnlyList<string> _availableMediaIds = Array.Empty<string>();
    private volatile IReadOnlyList<string> _availableMediaFilenames = Array.Empty<string>();
    private volatile IReadOnlyList<string> _availableCustomMediaFilenames = Array.Empty<string>();
    private volatile IReadOnlyDictionary<string, long> _mediaFileSizes = EmptyMediaFileSizes;
    private int _mediaListVersion;
    private volatile string _panelSerial = "";
    private volatile string _panelFirmwareVersion = "";
    private readonly TryxRkFrameReader _frames = new();
    private long _pullSession;
    private volatile PendingPull? _pendingPull;
    private volatile PendingReply? _pendingReply;
    private static readonly IReadOnlyDictionary<string, long> EmptyMediaFileSizes = new Dictionary<string, long>();

    public WindowsTryxRkTransport(string devicePath, string serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        Serial = serial ?? "";
        PortName = devicePath;
        var handle = Native.CreateFileW(
            devicePath,
            Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero,
            Native.OPEN_EXISTING,
            Native.FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"CreateFileW failed for {devicePath}: {err}");
        }
        // isAsync matches FILE_FLAG_OVERLAPPED so a concurrent write and the background
        // read complete as overlapped I/O. The stream owns and frees the handle.
        // bufferSize MUST be <= 1: a larger value wraps the stream in FileStream's
        // buffered strategy, which serializes reads and writes under one semaphore,
        // so the drain's parked read blocks every write until the panel happens to
        // push data. The starved panel then misses its keep-alives and re-enumerates
        // on its ~70s watchdog - the disconnect/reconnect loop.
        _stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: true);
        // The kernel cancels pending overlapped I/O when the issuing thread exits.
        // A pool-issued read parked between panel replies died on thread-pool
        // retirement (ERROR_OPERATION_ABORTED), the drain stopped, and the undrained
        // pipe reset the interface ~70s later - so reads are issued from a dedicated
        // thread that blocks on each completion and therefore never exits mid-read.
        _drainThread = new Thread(() =>
        {
            try { DrainReads(_drainCts.Token); }
            finally { _drainDone.TrySetResult(); }
        })
        {
            IsBackground = true,
            Name = "tryx-drain",
        };
        _drainThread.Start();
    }

    public bool IsOpen => !_disposed && _stream.SafeFileHandle is { IsInvalid: false, IsClosed: false };
    public string Serial { get; }
    public string PortName { get; }
    public IReadOnlyList<string> AvailableMediaIds => _availableMediaIds;
    public IReadOnlyList<string> AvailableMediaFilenames => _availableMediaFilenames;
    public IReadOnlyList<string> AvailableCustomMediaFilenames => _availableCustomMediaFilenames;
    public IReadOnlyDictionary<string, long> MediaFileSizes => _mediaFileSizes;
    public int MediaListVersion => Volatile.Read(ref _mediaListVersion);
    public string PanelSerial => _panelSerial;
    public string PanelFirmwareVersion => _panelFirmwareVersion;

    // A write to a panel that has stopped draining its endpoint (mid re-enumeration,
    // or firmware-wedged) parks in the usbprint stack for ~45-60s before it errors. That
    // whole time it holds _writeLock and the hub's _txGate, so the 1 Hz keep-alive can't
    // send - the panel then misses its ~10s standby / ~70s re-enum deadline and reboots,
    // which stalls the next write in turn: a self-sustaining reboot loop that only a
    // physical power-cycle broke. Bounding every write far under the keep-alive budget
    // turns that hang into a fast failure: the hub drops the transport, the heartbeat
    // resumes on the next tick, and the panel recovers on its own once it settles.
    private const int WriteTimeoutMs = 2500;
    // How long a cancelled operation gets to finish before its stream is judged stuck.
    private const int IoSettleMs = 3000;
    private volatile Task? _stuckIo;
    // Completes when the drain thread has left its read; the stream must outlive that read as well as any stuck write.
    private readonly TaskCompletionSource _drainDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsTryxRkTransport));
        }
        var copy = data.ToArray();
        lock (_writeLock)
        {
            // Async I/O on both directions: the handle is overlapped, so a sync Write
            // would collide with the background ReadAsync and stall the drain. Bound the
            // wait (the caller is a worker thread, no sync context to deadlock). A failing
            // or timed-out write throws so the hub drops and rebuilds the transport.
            AwaitBounded(ct => _stream.WriteAsync(copy.AsMemory(), ct).AsTask(), "write");
            AwaitBounded(ct => _stream.FlushAsync(ct), "flush");
        }
    }

    // Blocks up to WriteTimeoutMs for the overlapped write/flush. On timeout the operation is cancelled
    // (CancelIoEx on its overlapped) and waited out before anything can free the handle: disposing a
    // FileStream under a pending overlapped call frees the overlapped the kernel then completes into, which
    // is a native crash rather than an exception. An operation that ignores the cancel strands the stream
    // (Dispose keeps it alive instead of closing it). A TimeoutException then makes the hub rebuild the
    // transport instead of parking behind the stall.
    private void AwaitBounded(Func<CancellationToken, Task> start, string what)
    {
        using var op = CancellationTokenSource.CreateLinkedTokenSource(_drainCts.Token);
        var io = start(op.Token);
        try
        {
            if (!io.Wait(WriteTimeoutMs))
            {
                if (!PendingIo.CancelAndSettle(io, op, IoSettleMs))
                {
                    _stuckIo = io;
                }
                throw new TimeoutException($"Tryx panel {what} did not complete within {WriteTimeoutMs} ms");
            }
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException && _disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsTryxRkTransport));
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private sealed class PendingPull(ulong session)
    {
        public ulong Session { get; } = session;
        public TaskCompletionSource<TryxMediaList.FilePullChunk?> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // One pull in flight at a time: the hub issues pulls under its send gate.
    public TryxMediaList.FilePullChunk? PullFileChunk(string deviceFileName, long offset, int timeoutMs)
    {
        var pending = new PendingPull((ulong)Interlocked.Increment(ref _pullSession));
        _pendingPull = pending;
        try
        {
            Write(TryxRkProtocol.BuildFilePullRequest(deviceFileName, pending.Session, offset));
            return pending.Result.Task.Wait(timeoutMs) ? pending.Result.Task.Result : null;
        }
        finally
        {
            _pendingPull = null;
        }
    }

    private sealed class PendingReply(int field)
    {
        public int Field { get; } = field;
        public TaskCompletionSource<byte[]> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // One request in flight at a time: the hub issues requests under its send gate.
    public byte[]? Request(ReadOnlySpan<byte> frame, int replyField, int timeoutMs)
    {
        var pending = new PendingReply(replyField);
        _pendingReply = pending;
        try
        {
            Write(frame);
            return pending.Result.Task.Wait(timeoutMs) ? pending.Result.Task.Result : null;
        }
        finally
        {
            _pendingReply = null;
        }
    }

    // Continuously drain the panel's IN endpoint so its pipe never backs up. A read
    // error means the interface reset or the handle closed - stop, and the next
    // write failing lets the hub rebuild the transport (with a fresh drain loop).
    private void DrainReads(CancellationToken ct)
    {
        // Holds a whole file_pull_response (64 KiB + envelope) in one read; the frame reader
        // reassembles anything larger.
        var buffer = new byte[128 * 1024];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Blocking on the overlapped read keeps this thread (the issuer)
                // alive for the read's whole lifetime; see the constructor comment.
                var read = _stream.ReadAsync(buffer, 0, buffer.Length, ct).GetAwaiter().GetResult();
                if (read == 0)
                {
                    Thread.Sleep(50);
                    continue;
                }
                foreach (var payload in _frames.Append(buffer.AsSpan(0, read)))
                {
                    HandlePayload(payload);
                }
            }
            catch (Exception ex)
            {
                // A silent drain death leaves the transport write-only and the panel
                // resets its interface ~70s later, so any abnormal exit must be loud.
                // A throw from the log write itself would be unhandled on this
                // dedicated thread and kill the process - swallow it.
                if (!ct.IsCancellationRequested)
                {
                    try { ServiceLog.Warn($"[tryx] drain loop exited: {ex.GetType().Name}: {ex.Message}"); }
                    catch { /* logging failure must not end the process */ }
                }
                return;
            }
        }
    }

    private void HandlePayload(ReadOnlySpan<byte> span)
    {
        var reply = _pendingReply;
        if (reply is not null && TryxMediaList.HasLenField(span, reply.Field))
        {
            reply.Result.TrySetResult(span.ToArray());
            return;
        }
        var pending = _pendingPull;
        if (pending is not null)
        {
            var chunk = TryxMediaList.ParseFilePullResponse(span);
            if (chunk is { } c)
            {
                if (c.SessionId == pending.Session) pending.Result.TrySetResult(c);
                return;
            }
            // A firmware without file_pull answers BodyCaseNotSupported.
            if (TryxMediaList.ParseErrorCode(span) is not null)
            {
                pending.Result.TrySetResult(new TryxMediaList.FilePullChunk(false, pending.Session, 0, 0, []));
                return;
            }
        }
        // file_list (f503) push - device truth. ParseMediaEntries is the authoritative
        // source (all files under /userdata/*, with sizes); the basename is what the rest
        // of the code keys on. A non-list read (heartbeat ack, device_info) returns null,
        // so a stale list is never wiped by an unrelated read.
        var entries = TryxMediaList.ParseMediaEntries(span);
        if (entries is not null)
        {
            var names = new List<string>(entries.Count);
            var customNames = new List<string>();
            var sizes = new Dictionary<string, long>(entries.Count, StringComparer.Ordinal);
            var usedBytes = 0L;
            foreach (var e in entries)
            {
                names.Add(e.Name);
                if (e.IsCustom) customNames.Add(e.Name);
                sizes[e.Name] = e.SizeBytes;
                usedBytes += e.SizeBytes;
            }
            var changed = !names.SequenceEqual(_availableMediaFilenames);
            _availableMediaIds = TryxMediaList.ParsePresetIds(span);
            _availableMediaFilenames = names;
            _availableCustomMediaFilenames = customNames;
            _mediaFileSizes = sizes;
            Interlocked.Increment(ref _mediaListVersion);
            if (changed) ServiceLog.Info($"[tryx] panel media list ({names.Count}, {usedBytes} bytes): {string.Join(", ", names)}");
        }
        // device_info (f500) reply carries the panel serial the list/file commands require.
        var serial = TryxMediaList.ParseSerialNumber(span);
        if (!string.IsNullOrEmpty(serial))
        {
            _panelSerial = serial;
            ServiceLog.Info($"[tryx] panel serial {serial}");
        }
        var firmware = TryxMediaList.ParseFirmwareVersion(span);
        if (!string.IsNullOrEmpty(firmware))
        {
            _panelFirmwareVersion = firmware;
            ServiceLog.Info($"[tryx] panel firmware {firmware}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Cancel first so the parked read (CancelIoEx) and any write end, then let the drain thread finish its
        // read before the stream goes: the stream is only closed with no operation pending on it.
        _drainCts.Cancel();
        var drained = _drainThread.Join(IoSettleMs);
        // Serialize against an in-flight Write so the stream can't close mid-write.
        lock (_writeLock)
        {
            // Released only once the stuck write (if any) and the drain thread's read have both finished.
            var pending = drained && _stuckIo is null ? null : Task.WhenAll(_stuckIo ?? Task.CompletedTask, _drainDone.Task);
            if (!PendingIo.Retire(_stream, pending is null, pending))
            {
                ServiceLog.Warn($"[tryx] {PortName}: I/O still pending after cancel; stream left open instead of freed under it");
            }
        }
        _drainCts.Dispose();
    }

    private static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        public static extern SafeFileHandle CreateFileW(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flags, IntPtr templateFile);
    }
}
#endif
