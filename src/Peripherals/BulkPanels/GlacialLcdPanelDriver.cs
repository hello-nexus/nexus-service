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

    public void Clear(string key) => _store.Update(s => s.LcdUploadHashes.Remove(key));
}

/// <summary>
/// The ASUS ROG Crosshair X870E Glacial motherboard LCD. A picture upload is far too slow to
/// stream, so this driver pushes a still: a worker thread owned by the driver encodes the
/// newest rendered frame as JPEG and uploads it once the content has settled, after a long
/// cooldown, and only when the panel does not already hold that exact picture. Live content
/// belongs to the panel's own hardware-monitor mode, not to this path.
/// </summary>
public sealed class GlacialLcdPanelDriver : IBulkPanelDriver
{
    /// <summary>
    /// Minimum gap between two upload starts. Long on purpose: the vendor app uploads on
    /// demand, never on a timer, and the transfer may be a flash write. The cost is a stale
    /// panel when the page changes more often than this.
    /// </summary>
    public const int DefaultRefreshIntervalMs = 30 * 60 * 1000;

    /// <summary>How long the content must stay unchanged before it is uploaded.</summary>
    public const int DefaultSettleMs = 4000;

    /// <summary>The first upload after a connect goes up after this wait even if the content never settles, so something shows.</summary>
    public const int DefaultMaxSettleMs = 60_000;

    /// <summary>First retry delay after a failed upload; doubles each time, capped at the refresh interval.</summary>
    public const int DefaultBackoffBaseMs = 10_000;

    /// <summary>Delay before the picture is uploaded again after brightness zero interrupted it.</summary>
    public const int DefaultCancelRetryMs = 60_000;

    private const int PollMs = 200;
    private const int JoinOnDisconnectMs = 5000;
    private const int WarnEveryMs = 60_000;
    private const int StateReplyAttempts = 3;
    private const int StateReplyWaitMs = 200;
    private const int DrainLimit = 64;
    private const int BrightnessRetryMs = 10_000;

    private readonly int _chunkIntervalMs;
    private readonly int _refreshIntervalMs;
    private readonly int _settleMs;
    private readonly int _maxSettleMs;
    private readonly int _backoffBaseMs;
    private readonly int _cancelRetryMs;
    private readonly Func<long> _clock;

    private readonly object _hidLock = new();
    private readonly object _frameLock = new();
    private readonly object _warnLock = new();
    private readonly ManualResetEventSlim _wake = new(false);
    // Cuts a chunk wait short, so a cancel or a disconnect never sits out the pacing.
    private readonly ManualResetEventSlim _abort = new(false);

    private ILcdUploadMemory? _memory;

    // Guarded by _hidLock.
    private WorkerState? _current;
    private IBulkUsbPipe? _pipe;
    private IHidDevice? _hid;
    private bool _uploading;
    private bool _cancelUpload;
    private int _wantBrightness = -1;
    private long _brightnessRetryAtMs;

    // Guarded by _frameLock.
    private byte[]? _latest;
    private bool _pending;
    private long _lastChangeMs;
    private long _firstPendingMs;

    // Guarded by _warnLock.
    private long _lastWarnMs = long.MinValue;

    // Written by the worker, so they carry over a reconnect with the driver.
    private string? _lastHash;
    private string _memoryKey = "";
    private long _nextAllowedMs;
    private int _failures;
    private bool _firstSpent;
    private int _uploadsCompleted;
    private int _uploadAttempts;
    private int _ticks;

    private sealed class WorkerState
    {
        public volatile bool Stop;
        public Thread? Thread;
    }

    public GlacialLcdPanelDriver(
        int chunkIntervalMs = GlacialLcdProtocol.ChunkIntervalMs,
        int refreshIntervalMs = DefaultRefreshIntervalMs,
        int settleMs = DefaultSettleMs,
        int maxSettleMs = DefaultMaxSettleMs,
        int backoffBaseMs = DefaultBackoffBaseMs,
        int cancelRetryMs = DefaultCancelRetryMs,
        Func<long>? clock = null)
    {
        _chunkIntervalMs = chunkIntervalMs;
        _refreshIntervalMs = refreshIntervalMs;
        _settleMs = settleMs;
        _maxSettleMs = maxSettleMs;
        _backoffBaseMs = backoffBaseMs;
        _cancelRetryMs = cancelRetryMs;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public string HandlerId => "asus-glacial-lcd";
    public string Name => "ROG Crosshair X870E Glacial LCD";
    public int VendorId => 0x0B05;
    public IReadOnlyList<int> ProductIds { get; } = new[] { GlacialLcdProtocol.ProductId };
    public string Surface => Models.Panel.PanelSurfaces.LcdSquare;

    /// <summary>The overlay only needs to hand over a frame now and then; the worker decides what is uploaded.</summary>
    public int Fps => 1;

    public byte WritePipeId => 0x02;
    public byte ReadPipeId => 0x82;
    public bool NeedsHidChannel => true;

    /// <summary>State replies are read with a timeout, which Windows honors only on an overlapped handle.</summary>
    public bool HidOpensForInput => true;

    public bool SupportsBrightness => true;

    /// <summary>Uploads that ran to the last chunk.</summary>
    public int UploadsCompleted => Volatile.Read(ref _uploadsCompleted);

    /// <summary>Uploads started, finished or not.</summary>
    public int UploadAttempts => Volatile.Read(ref _uploadAttempts);

    /// <summary>Worker loop passes so far; lets a test wait until the worker has looked again.</summary>
    public int Ticks => Volatile.Read(ref _ticks);

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
        // The vendor app opens with this pair; its replies are drained before the next query.
        if (!hid.Write(GlacialLcdProtocol.EncodeHello()) || !hid.Write(GlacialLcdProtocol.EncodeQuery()))
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
                _cancelUpload = false;
                _abort.Reset();
            }
        }
        if (start is not null)
        {
            _memoryKey = hid.Serial is { Length: > 0 } serial ? serial : HandlerId;
            if (_memory?.Load(_memoryKey) is { } remembered)
            {
                _lastHash = remembered;
            }
            _firstSpent = false;
            start.Thread = new Thread(() => Run(start)) { IsBackground = true, Name = "glacial-lcd" };
            start.Thread.Start();
        }
        return (GlacialLcdProtocol.Width, GlacialLcdProtocol.Height);
    }

    /// <summary>Records the newest frame; the worker uploads it when it has settled and the cooldown allows.</summary>
    public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra)
    {
        if (hid is null)
        {
            return false;
        }
        lock (_hidLock)
        {
            if (_current is null)
            {
                return false;
            }
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
                    RetryBrightness(state);
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
        if (now < _nextAllowedMs)
        {
            return false;
        }
        lock (_frameLock)
        {
            if (!_pending)
            {
                return false;
            }
            // Only the first upload after a connect may go out unsettled, so something shows.
            return now - _lastChangeMs >= _settleMs || (!_firstSpent && now - _firstPendingMs >= _maxSettleMs);
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
        encoder ??= new BgraJpegEncoder(GlacialLcdProtocol.Width, GlacialLcdProtocol.Height, GlacialLcdProtocol.JpegQuality);
        byte[] jpeg = encoder.Encode(snapshot).ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(jpeg));
        if (hash == _lastHash)
        {
            _firstSpent = true;
            return;
        }

        IBulkUsbPipe? pipe;
        IHidDevice? hid;
        lock (_hidLock)
        {
            pipe = _pipe;
            hid = _hid;
            if (state.Stop || pipe is null || hid is null)
            {
                MarkPending();
                return;
            }
            _uploading = true;
            _cancelUpload = false;
        }
        _abort.Reset();
        Interlocked.Increment(ref _uploadAttempts);
        // From the announce on, the panel may hold a partial picture, so it holds nothing known.
        _lastHash = null;
        _memory?.Clear(_memoryKey);

        var outcome = UploadOutcome.Failed;
        try
        {
            outcome = UploadJpeg(state, pipe, hid, jpeg);
        }
        catch (Exception ex)
        {
            Failed($"upload failed: {ex.GetType().Name}: {ex.Message}");
        }

        lock (_hidLock)
        {
            // A worker that outlived its disconnect must not clear the flag of the one that replaced it.
            if (_current is null || ReferenceEquals(_current, state))
            {
                _uploading = false;
            }
        }
        long now = _clock();
        switch (outcome)
        {
            case UploadOutcome.Done:
                _lastHash = hash;
                _memory?.Save(_memoryKey, hash);
                _failures = 0;
                _firstSpent = true;
                _nextAllowedMs = now + _refreshIntervalMs;
                Interlocked.Increment(ref _uploadsCompleted);
                ServiceLog.Info($"[{HandlerId}] picture uploaded, {jpeg.Length} bytes");
                break;
            case UploadOutcome.Cancelled:
                // Not a fault: brightness zero or a disconnect cut it short. The picture goes up later.
                MarkPending();
                _nextAllowedMs = now + _cancelRetryMs;
                break;
            default:
                MarkPending();
                _failures++;
                _nextAllowedMs = now + Backoff(_failures);
                Failed($"upload failed; retrying in {Backoff(_failures) / 1000} s");
                break;
        }
        RetryBrightness(state);
    }

    private void MarkPending()
    {
        lock (_frameLock)
        {
            // Keeps a newer frame's timestamps; a restored picture is uploaded as soon as the cooldown allows.
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
        for (int i = 1; i < failures && delay < _refreshIntervalMs; i++)
        {
            delay *= 2;
        }
        return (int)Math.Min(delay, _refreshIntervalMs);
    }

    private void Failed(string message)
    {
        long now = _clock();
        lock (_warnLock)
        {
            if (_lastWarnMs != long.MinValue && now - _lastWarnMs < WarnEveryMs)
            {
                return;
            }
            _lastWarnMs = now;
        }
        ServiceLog.Warn($"[{HandlerId}] {message}");
    }

    private enum UploadOutcome
    {
        Done,
        Cancelled,
        Failed,
    }

    private UploadOutcome UploadJpeg(WorkerState state, IBulkUsbPipe pipe, IHidDevice hid, byte[] jpeg)
    {
        lock (_hidLock)
        {
            // The app sends nothing else on HID while the bulk transfers run.
            if (!hid.Write(GlacialLcdProtocol.EncodeQuery()))
            {
                return UploadOutcome.Failed;
            }
            foreach (var report in GlacialLcdProtocol.EncodeCustomImagePrelude())
            {
                if (!hid.Write(report))
                {
                    return UploadOutcome.Failed;
                }
            }
            if (!hid.Write(GlacialLcdProtocol.EncodeJpegAnnounce(jpeg.Length)))
            {
                return UploadOutcome.Failed;
            }
        }
        for (int offset = 0; offset < jpeg.Length; offset += GlacialLcdProtocol.ChunkBytes)
        {
            if (state.Stop || Volatile.Read(ref _cancelUpload))
            {
                return UploadOutcome.Cancelled;
            }
            int length = Math.Min(GlacialLcdProtocol.ChunkBytes, jpeg.Length - offset);
            if (!pipe.Write(jpeg.AsSpan(offset, length)))
            {
                return UploadOutcome.Failed;
            }
            if (offset + length < jpeg.Length && _chunkIntervalMs > 0 && _abort.Wait(_chunkIntervalMs))
            {
                return UploadOutcome.Cancelled;
            }
        }
        return UploadOutcome.Done;
    }

    /// <summary>The glass holds its picture, so there is nothing to resend.</summary>
    public bool Resend(IBulkUsbPipe pipe, IHidDevice? hid) => true;

    /// <summary>
    /// Records the wanted level and applies it unless a picture is on its way, in which case the
    /// worker applies it afterwards and keeps retrying until the panel took it, because the
    /// caller treats a true return as applied. Dark is the exception to waiting: a lock-to-dark
    /// must not sit out the transfer, so it cancels the upload.
    /// </summary>
    public bool SetBrightness(IBulkUsbPipe pipe, IHidDevice? hid, int percent)
    {
        if (hid is null)
        {
            return false;
        }
        lock (_hidLock)
        {
            _wantBrightness = percent;
            if (_uploading)
            {
                if (percent <= 0)
                {
                    _cancelUpload = true;
                    _abort.Set();
                }
                return true;
            }
            // Checked and applied under one lock hold, so an upload cannot start in between.
            return ApplyWanted(hid);
        }
    }

    private void RetryBrightness(WorkerState state)
    {
        lock (_hidLock)
        {
            if (state.Stop || _wantBrightness < 0 || _uploading || _hid is not { } hid)
            {
                return;
            }
            ApplyWanted(hid);
        }
    }

    private bool ApplyWanted(IHidDevice hid)
    {
        if (_wantBrightness < 0)
        {
            return true;
        }
        if (!ApplyBrightness(hid, _wantBrightness))
        {
            return false;
        }
        _wantBrightness = -1;
        return true;
    }

    /// <summary>
    /// The vendor sequence: query the state, then write it back with one field changed. The
    /// state must come from a fresh reply, so older input is drained first; without one the
    /// write is skipped, because guessing the mode would switch the panel off its picture and
    /// clear the sleep setting. The app also repeats its mode select afterwards; this driver
    /// never selects a mode, so it has none to repeat. Called with the HID lock held.
    /// </summary>
    private bool ApplyBrightness(IHidDevice hid, int percent)
    {
        long now = _clock();
        if (now < _brightnessRetryAtMs)
        {
            return false;
        }
        var buffer = new byte[GlacialLcdProtocol.ReportBytes];
        for (int i = 0; i < DrainLimit && hid.Read(buffer, 0) > 0; i++)
        {
        }
        if (hid.Write(GlacialLcdProtocol.EncodeQuery()))
        {
            for (int attempt = 0; attempt < StateReplyAttempts; attempt++)
            {
                int read = hid.Read(buffer, StateReplyWaitMs);
                if (read < 0)
                {
                    break;
                }
                if (read > 0 && GlacialLcdProtocol.ParseState(buffer.AsSpan(0, read)) is { } state)
                {
                    if (hid.Write(GlacialLcdProtocol.EncodeSettings(percent, state.Mode, state.KeepOnWhileAsleep)))
                    {
                        return true;
                    }
                    break;
                }
            }
        }
        _brightnessRetryAtMs = now + BrightnessRetryMs;
        Failed("brightness not written; will retry");
        return false;
    }

    public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        WorkerState? state;
        lock (_hidLock)
        {
            state = _current;
            _current = null;
            _pipe = null;
            _hid = null;
            _wantBrightness = -1;
            _cancelUpload = true;
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
