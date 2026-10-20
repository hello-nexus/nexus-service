using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>Remembers the hash of the picture the panel holds, across reconnects and restarts.</summary>
public interface ILcdUploadMemory
{
    string? Load(string key);

    void Clear(string key);

    void Save(string key, string hash);
}

/// <summary>Keeps the upload hashes in the settings document.</summary>
public sealed class ConfigStoreLcdUploadMemory : ILcdUploadMemory
{
    private readonly IConfigStore _store;

    public ConfigStoreLcdUploadMemory(IConfigStore store)
    {
        _store = store;
    }

    public string? Load(string key)
    {
        string? hash = null;
        _store.Read(s => s.LcdUploadHashes.TryGetValue(key, out hash));
        return hash;
    }

    public void Save(string key, string hash) => _store.Update(s => s.LcdUploadHashes[key] = hash);

    /// <summary>Flushed at once: a crash after the announce must not leave the old hash on disk for a half-written panel.</summary>
    public void Clear(string key)
    {
        _store.Update(s => s.LcdUploadHashes.Remove(key));
        _store.FlushNow();
    }
}

/// <summary>
/// Shared by the native-mode worker and the streamed-panel driver so only one of them talks to
/// the panel at a time: a mutex both take around their HID traffic, and a flag that says a
/// Nexus Panel is streaming to it.
/// </summary>
public sealed class GlacialLcdLink
{
    private volatile bool _streaming;
    private volatile bool _cancelUpload;

    /// <summary>Held for each pass of the native worker and for the whole of an upload.</summary>
    public object Sync { get; } = new();

    /// <summary>True while a Nexus Panel is assigned to the display; the native worker then leaves the mode alone.</summary>
    public bool Streaming => _streaming;

    /// <summary>Asks a running upload to stop, so a lock-to-dark brightness does not wait out the transfer.</summary>
    public bool CancelRequested => _cancelUpload;

    /// <summary>Raised when the streaming flag flips, so the native worker looks again at once.</summary>
    public event Action? Changed;

    public void SetStreaming(bool streaming)
    {
        if (_streaming == streaming)
        {
            return;
        }
        _streaming = streaming;
        Changed?.Invoke();
    }

    public void RequestCancel() => _cancelUpload = true;

    public void ClearCancel() => _cancelUpload = false;
}

/// <summary>
/// The ASUS ROG Crosshair X870E Glacial motherboard LCD as a streamed panel. The panel only
/// takes a stored custom picture, so each frame is a full upload: a worker thread owned by the
/// driver encodes the newest rendered frame as JPEG and sends it whenever it differs from the
/// picture the panel already holds, one upload after another. The upload is slow by the
/// vendor's own pacing, so frames that arrive meanwhile collapse into the latest one.
/// </summary>
public sealed class GlacialLcdPanelDriver : IBulkPanelDriver
{
    /// <summary>How long the content must stay unchanged before it is uploaded.</summary>
    public const int DefaultSettleMs = 1000;

    /// <summary>Content that never settles is uploaded anyway after waiting this long.</summary>
    public const int DefaultMaxSettleMs = 10_000;

    /// <summary>First retry delay after a failed upload; doubles each time, capped at the cap below.</summary>
    public const int DefaultBackoffBaseMs = 10_000;

    /// <summary>Longest wait between retries of a failing upload.</summary>
    public const int BackoffCapMs = 5 * 60 * 1000;

    /// <summary>Delay before the picture is uploaded again after a brightness of zero interrupted it.</summary>
    public const int DefaultCancelRetryMs = 5000;

    private const int PollMs = 200;
    private const int JoinOnDisconnectMs = 5000;
    private const int WarnEveryMs = 60_000;

    private readonly GlacialLcdLink _link;
    private readonly int _paceScalePercent;
    private readonly int _settleMs;
    private readonly int _maxSettleMs;
    private readonly int _backoffBaseMs;
    private readonly int _cancelRetryMs;
    private readonly Func<long> _clock;

    private readonly object _hidLock = new();
    private readonly object _frameLock = new();
    private readonly object _stateLock = new();
    private readonly WarnLimiter _warn = new();
    private readonly ManualResetEventSlim _wake = new(false);
    // Cuts a pacing wait short, so a cancel or a disconnect never sits out the vendor's gaps.
    private readonly ManualResetEventSlim _abort = new(false);

    private ILcdUploadMemory? _memory;

    // Guarded by _hidLock.
    private volatile WorkerState? _current;
    private IBulkUsbPipe? _pipe;
    private IHidDevice? _hid;
    private bool _uploading;

    // Guarded by _frameLock.
    private byte[]? _latest;
    private bool _pending;
    private long _lastChangeMs;
    private long _firstPendingMs;

    // Guarded by _stateLock; outlives a worker so the remembered picture and backoff carry over a reconnect.
    private string? _lastHash;
    private long _nextAllowedMs;
    private int _failures;

    private string _memoryKey = "";
    private int _uploadsCompleted;
    private int _uploadAttempts;
    private int _ticks;

    private sealed class WorkerState
    {
        public volatile bool Stop;
        public Thread? Thread;
    }

    /// <param name="paceScalePercent">Scales every vendor pacing gap; 100 is the captured pace, 0 removes them (tests).</param>
    public GlacialLcdPanelDriver(
        GlacialLcdLink link,
        int paceScalePercent = 100,
        int settleMs = DefaultSettleMs,
        int maxSettleMs = DefaultMaxSettleMs,
        int backoffBaseMs = DefaultBackoffBaseMs,
        int cancelRetryMs = DefaultCancelRetryMs,
        Func<long>? clock = null)
    {
        _link = link;
        _paceScalePercent = paceScalePercent;
        _settleMs = settleMs;
        _maxSettleMs = maxSettleMs;
        _backoffBaseMs = backoffBaseMs;
        _cancelRetryMs = cancelRetryMs;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public string HandlerId => GlacialLcdWorker.HandlerId;
    public string Name => "ROG Crosshair X870E Glacial LCD";
    public int VendorId => 0x0B05;
    public IReadOnlyList<int> ProductIds { get; } = new[] { GlacialLcdProtocol.ProductId };
    public string Surface => Models.Panel.PanelSurfaces.LcdSquare;

    /// <summary>The overlay only needs to hand over a frame now and then; the worker decides what is uploaded.</summary>
    public int Fps => 1;

    public byte WritePipeId => 0x02;
    public byte ReadPipeId => 0x82;
    public bool NeedsHidChannel => true;

    /// <summary>The upload sequence reads nothing today, but the shared worker opens one handle per driver and this one is overlapped like the native worker's.</summary>
    public bool HidOpensForInput => true;

    public void StreamOpened() => _link.SetStreaming(true);

    public void StreamClosed() => _link.SetStreaming(false);

    /// <summary>Uploads that ran to the finish step.</summary>
    public int UploadsCompleted => Volatile.Read(ref _uploadsCompleted);

    /// <summary>Uploads started, finished or not.</summary>
    public int UploadAttempts => Volatile.Read(ref _uploadAttempts);

    /// <summary>Worker loop passes so far; lets a test wait until the worker has looked again.</summary>
    internal int Ticks => Volatile.Read(ref _ticks);

    /// <summary>True while a picture is on its way to the panel.</summary>
    public bool IsUploading
    {
        get { lock (_hidLock) { return _uploading; } }
    }

    /// <summary>Supplies the store that survives restarts; without one every connect uploads afresh.</summary>
    public void BindMemory(ILcdUploadMemory memory) => _memory = memory;

    public (int Width, int Height)? Connect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        if (hid is null)
        {
            return null;
        }
        WorkerState? start = null;
        lock (_hidLock)
        {
            _pipe = pipe;
            _hid = hid;
            if (_current is null)
            {
                start = _current = new WorkerState();
                _abort.Reset();
            }
        }
        if (start is not null)
        {
            _memoryKey = hid.Serial is { Length: > 0 } serial ? serial : HandlerId;
            var remembered = _memory?.Load(_memoryKey);
            lock (_stateLock)
            {
                if (remembered is not null)
                {
                    _lastHash = remembered;
                }
            }
            start.Thread = new Thread(() => Run(start)) { IsBackground = true, Name = "glacial-lcd-stream" };
            start.Thread.Start();
        }
        return (GlacialLcdProtocol.PanelWidth, GlacialLcdProtocol.PanelHeight);
    }

    /// <summary>Records the newest frame; the worker uploads it when it has settled.</summary>
    public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra)
    {
        if (hid is null || _current is null)
        {
            return false;
        }
        long now = _clock();
        bool changed;
        lock (_frameLock)
        {
            if (_latest is null || _latest.Length != bgra.Length)
            {
                _latest = new byte[bgra.Length];
                changed = true;
            }
            else
            {
                changed = !bgra.SequenceEqual(_latest);
            }
            if (changed)
            {
                bgra.CopyTo(_latest);
                if (!_pending)
                {
                    _firstPendingMs = now;
                }
                _pending = true;
                _lastChangeMs = now;
            }
        }
        if (changed)
        {
            _wake.Set();
        }
        return true;
    }

    private void Run(WorkerState state)
    {
        BgraJpegEncoder? encoder = null;
        byte[]? snapshot = null;
        try
        {
            while (!state.Stop)
            {
                _wake.Wait(PollMs);
                _wake.Reset();
                if (state.Stop)
                {
                    break;
                }
                try
                {
                    if (Ready())
                    {
                        UploadPending(state, ref encoder, ref snapshot);
                    }
                }
                catch (Exception ex)
                {
                    Failed($"worker error: {ex.GetType().Name}: {ex.Message}");
                }
                Interlocked.Increment(ref _ticks);
            }
        }
        finally
        {
            encoder?.Dispose();
        }
    }

    private bool Ready()
    {
        long now = _clock();
        lock (_stateLock)
        {
            if (now < _nextAllowedMs)
            {
                return false;
            }
        }
        lock (_frameLock)
        {
            return _pending && (now - _lastChangeMs >= _settleMs || now - _firstPendingMs >= _maxSettleMs);
        }
    }

    private void UploadPending(WorkerState state, ref BgraJpegEncoder? encoder, ref byte[]? snapshot)
    {
        lock (_frameLock)
        {
            if (_latest is null)
            {
                return;
            }
            if (snapshot is null || snapshot.Length != _latest.Length)
            {
                snapshot = new byte[_latest.Length];
            }
            _latest.CopyTo(snapshot, 0);
            _pending = false;
        }
        encoder ??= new BgraJpegEncoder(GlacialLcdProtocol.PanelWidth, GlacialLcdProtocol.PanelHeight, GlacialLcdProtocol.JpegQuality);
        byte[] jpeg = encoder.Encode(snapshot).ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(jpeg));
        lock (_stateLock)
        {
            if (hash == _lastHash)
            {
                return;
            }
        }

        IBulkUsbPipe? pipe;
        IHidDevice? hid;
        lock (_hidLock)
        {
            pipe = _pipe;
            hid = _hid;
            if (state.Stop)
            {
                // A stopped worker leaves all shared state alone.
                return;
            }
            if (pipe is null || hid is null)
            {
                MarkPending();
                return;
            }
            _uploading = true;
        }
        _abort.Reset();
        _link.ClearCancel();
        Interlocked.Increment(ref _uploadAttempts);
        // From the first report on, the panel may hold a partial picture, so it holds nothing known.
        lock (_stateLock)
        {
            _lastHash = null;
        }
        _memory?.Clear(_memoryKey);

        var outcome = UploadOutcome.Failed;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // One owner of the panel at a time: the native worker waits out the whole upload.
            lock (_link.Sync)
            {
                outcome = UploadJpeg(state, pipe, hid, jpeg);
            }
        }
        catch (Exception ex)
        {
            // A write that failed because the link was torn down is a cancel, not a fault.
            outcome = state.Stop ? UploadOutcome.Cancelled : UploadOutcome.Failed;
            if (!state.Stop)
            {
                Failed($"upload failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        watch.Stop();

        lock (_hidLock)
        {
            // A worker that outlived its disconnect must not clear the flag of the one that replaced it.
            if (_current is null || ReferenceEquals(_current, state))
            {
                _uploading = false;
            }
        }
        long now = _clock();
        if (outcome == UploadOutcome.Done)
        {
            lock (_stateLock)
            {
                _lastHash = hash;
                _failures = 0;
                _nextAllowedMs = 0;
            }
            _memory?.Save(_memoryKey, hash);
            Interlocked.Increment(ref _uploadsCompleted);
            ServiceLog.Info($"[{HandlerId}] picture uploaded, {jpeg.Length} bytes in {watch.ElapsedMilliseconds} ms");
        }
        else if (state.Stop)
        {
            // Torn down mid-upload: no delay, no backoff, and the frame state belongs to the next worker.
            return;
        }
        else if (outcome == UploadOutcome.Cancelled)
        {
            MarkPending();
            lock (_stateLock)
            {
                _nextAllowedMs = now + _cancelRetryMs;
            }
        }
        else
        {
            MarkPending();
            int delay;
            lock (_stateLock)
            {
                _failures++;
                delay = Backoff(_failures);
                _nextAllowedMs = now + delay;
            }
            Failed($"upload failed after {watch.ElapsedMilliseconds} ms; retrying in {delay / 1000} s");
        }
    }

    private void MarkPending()
    {
        lock (_frameLock)
        {
            // Keeps a newer frame's timestamps; a restored picture is uploaded as soon as backoff allows.
            if (!_pending)
            {
                _pending = true;
                _lastChangeMs = 0;
                _firstPendingMs = 0;
            }
        }
    }

    private int Backoff(int failures)
    {
        long delay = _backoffBaseMs;
        for (int i = 1; i < failures && delay < BackoffCapMs; i++)
        {
            delay *= 2;
        }
        return (int)Math.Min(delay, BackoffCapMs);
    }

    private void Failed(string message)
    {
        if (_warn.Allow(_clock()))
        {
            ServiceLog.Warn($"[{HandlerId}] {message}");
        }
    }

    /// <summary>One warning per window, so a failing upload does not flood the log.</summary>
    private sealed class WarnLimiter
    {
        private long _last = long.MinValue;

        public bool Allow(long now)
        {
            lock (this)
            {
                if (_last != long.MinValue && now - _last < WarnEveryMs)
                {
                    return false;
                }
                _last = now;
                return true;
            }
        }
    }

    private enum UploadOutcome
    {
        Done,
        Cancelled,
        Failed,
    }

    /// <summary>A pacing wait from the capture; true when it was cut short by a cancel.</summary>
    private bool Pause(int capturedMs)
    {
        int ms = (int)((long)capturedMs * _paceScalePercent / 100);
        return ms > 0 && _abort.Wait(ms);
    }

    private bool ShouldStop(WorkerState state) => state.Stop || _link.CancelRequested;

    private UploadOutcome UploadJpeg(WorkerState state, IBulkUsbPipe pipe, IHidDevice hid, byte[] jpeg)
    {
        UploadOutcome Fail() => ShouldStop(state) ? UploadOutcome.Cancelled : UploadOutcome.Failed;

        // The reports and gaps of the captured upload, in order.
        lock (_hidLock)
        {
            if (!hid.Write(GlacialLcdProtocol.EncodeQuery()))
            {
                return Fail();
            }
        }
        foreach (var (report, gapMs) in GlacialLcdProtocol.EncodeCustomImagePrelude(jpeg.Length))
        {
            if (Pause(gapMs))
            {
                return UploadOutcome.Cancelled;
            }
            lock (_hidLock)
            {
                if (!hid.Write(report))
                {
                    return Fail();
                }
            }
        }
        if (Pause(GlacialLcdProtocol.ChunkLeadMs))
        {
            return UploadOutcome.Cancelled;
        }
        var chunk = new byte[GlacialLcdProtocol.ChunkBytes];
        for (int offset = 0; offset < jpeg.Length; offset += GlacialLcdProtocol.ChunkBytes)
        {
            if (ShouldStop(state))
            {
                return UploadOutcome.Cancelled;
            }
            // The last transfer is a full chunk, zero padded past the picture's end.
            Array.Clear(chunk);
            jpeg.AsSpan(offset, Math.Min(GlacialLcdProtocol.ChunkBytes, jpeg.Length - offset)).CopyTo(chunk);
            if (!pipe.Write(chunk))
            {
                return Fail();
            }
            if (Pause(GlacialLcdProtocol.ChunkIntervalMs))
            {
                return UploadOutcome.Cancelled;
            }
        }
        lock (_hidLock)
        {
            if (!hid.Write(GlacialLcdProtocol.EncodeUploadEnd()))
            {
                return Fail();
            }
        }
        if (Pause(GlacialLcdProtocol.FinishSettleMs))
        {
            return UploadOutcome.Cancelled;
        }
        lock (_hidLock)
        {
            // The picture is stored; showing it is the app's last step.
            if (!hid.Write(GlacialLcdProtocol.EncodeQuery()) || !hid.Write(GlacialLcdProtocol.EncodeCustomPicture()))
            {
                return Fail();
            }
        }
        return UploadOutcome.Done;
    }

    /// <summary>Nothing streams to a panel that has no backlight command of its own here; brightness belongs to the native page.</summary>
    public bool SetBrightness(IBulkUsbPipe pipe, IHidDevice? hid, int percent) => false;

    /// <summary>The panel holds its picture, so there is nothing to resend.</summary>
    public bool Resend(IBulkUsbPipe pipe, IHidDevice? hid) => true;

    public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        WorkerState? state;
        lock (_hidLock)
        {
            state = _current;
            _current = null;
            _pipe = null;
            _hid = null;
            _uploading = false;
        }
        if (state is not null)
        {
            // A worker that outlives the join stays stopped; the next connect gets its own.
            state.Stop = true;
            _abort.Set();
            _wake.Set();
            state.Thread?.Join(JoinOnDisconnectMs);
        }
        lock (_frameLock)
        {
            _latest = null;
            _pending = false;
        }
    }
}
