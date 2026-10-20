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
}

/// <summary>
/// The ASUS ROG Crosshair X870E Glacial motherboard LCD. A picture upload is far too slow to
/// stream, so this driver pushes a still: a worker thread owned by the driver encodes the
/// newest rendered frame as JPEG and uploads it once the content has settled, after a long
/// cooldown, and only when the panel does not already hold that exact picture.
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

    /// <summary>Content that never settles is uploaded anyway after waiting this long.</summary>
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

    private readonly object _hidLock = new();
    private readonly object _frameLock = new();
    private readonly ManualResetEventSlim _wake = new(false);
    // Cuts a chunk wait short, so a cancel or a disconnect never sits out the pacing.
    private readonly ManualResetEventSlim _abort = new(false);

    private ILcdUploadMemory? _memory;
    private IBulkUsbPipe? _pipe;
    private IHidDevice? _hid;
    private Thread? _worker;
    private volatile bool _stop;
    private volatile bool _cancelUpload;

    // Guarded by _hidLock.
    private bool _uploading;
    private int _pendingBrightness = -1;
    private long _brightnessRetryAtMs;

    // Guarded by _frameLock.
    private byte[]? _latest;
    private bool _pending;
    private long _lastChangeMs;
    private long _firstPendingMs;

    // Worker thread only.
    private BgraJpegEncoder? _encoder;
    private string? _lastHash;
    private string _memoryKey = "";
    private long _nextAllowedMs;
    private int _failures;
    private long _lastWarnMs = long.MinValue;
    private int _uploadsCompleted;
    private int _uploadAttempts;

    public GlacialLcdPanelDriver(
        int chunkIntervalMs = GlacialLcdProtocol.ChunkIntervalMs,
        int refreshIntervalMs = DefaultRefreshIntervalMs,
        int settleMs = DefaultSettleMs,
        int maxSettleMs = DefaultMaxSettleMs,
        int backoffBaseMs = DefaultBackoffBaseMs,
        int cancelRetryMs = DefaultCancelRetryMs)
    {
        _chunkIntervalMs = chunkIntervalMs;
        _refreshIntervalMs = refreshIntervalMs;
        _settleMs = settleMs;
        _maxSettleMs = maxSettleMs;
        _backoffBaseMs = backoffBaseMs;
        _cancelRetryMs = cancelRetryMs;
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
    public bool SupportsBrightness => true;

    /// <summary>Uploads that ran to the last chunk.</summary>
    public int UploadsCompleted => Volatile.Read(ref _uploadsCompleted);

    /// <summary>Uploads started, finished or not.</summary>
    public int UploadAttempts => Volatile.Read(ref _uploadAttempts);

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
        lock (_hidLock)
        {
            _pipe = pipe;
            _hid = hid;
        }
        if (_worker is null)
        {
            _stop = false;
            _cancelUpload = false;
            _abort.Reset();
            _memoryKey = hid.Serial is { Length: > 0 } serial ? serial : HandlerId;
            _lastHash = _memory?.Load(_memoryKey);
            _nextAllowedMs = 0;
            _failures = 0;
            _worker = new Thread(Run) { IsBackground = true, Name = "glacial-lcd" };
            _worker.Start();
        }
        return (GlacialLcdProtocol.Width, GlacialLcdProtocol.Height);
    }

    /// <summary>Records the newest frame; the worker uploads it when it has settled and the cooldown allows.</summary>
    public bool SendFrame(IBulkUsbPipe pipe, IHidDevice? hid, ReadOnlySpan<byte> bgra)
    {
        if (hid is null || _worker is null)
        {
            return false;
        }
        long now = Environment.TickCount64;
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

    private void Run()
    {
        while (!_stop)
        {
            _wake.Wait(PollMs);
            _wake.Reset();
            if (_stop)
            {
                break;
            }
            try
            {
                if (Ready())
                {
                    UploadPending();
                }
            }
            catch (Exception ex)
            {
                Failed($"worker error: {ex.GetType().Name}: {ex.Message}");
            }
        }
        _encoder?.Dispose();
        _encoder = null;
    }

    private bool Ready()
    {
        long now = Environment.TickCount64;
        if (now < _nextAllowedMs)
        {
            return false;
        }
        lock (_frameLock)
        {
            return _pending && (now - _lastChangeMs >= _settleMs || now - _firstPendingMs >= _maxSettleMs);
        }
    }

    private void UploadPending()
    {
        byte[] jpeg;
        lock (_frameLock)
        {
            if (_latest is null)
            {
                return;
            }
            _encoder ??= new BgraJpegEncoder(GlacialLcdProtocol.Width, GlacialLcdProtocol.Height, GlacialLcdProtocol.JpegQuality);
            jpeg = _encoder.Encode(_latest).ToArray();
            _pending = false;
        }
        string hash = Convert.ToHexString(SHA256.HashData(jpeg));
        if (hash == _lastHash)
        {
            return;
        }

        IBulkUsbPipe? pipe;
        IHidDevice? hid;
        lock (_hidLock)
        {
            pipe = _pipe;
            hid = _hid;
            if (pipe is null || hid is null)
            {
                MarkPending();
                return;
            }
            _uploading = true;
            _cancelUpload = false;
        }
        _abort.Reset();
        Interlocked.Increment(ref _uploadAttempts);

        var outcome = UploadOutcome.Failed;
        try
        {
            outcome = UploadJpeg(pipe, hid, jpeg);
        }
        catch (Exception ex)
        {
            Failed($"upload failed: {ex.GetType().Name}: {ex.Message}");
        }

        int brightness;
        lock (_hidLock)
        {
            brightness = _pendingBrightness;
            _pendingBrightness = -1;
            _uploading = false;
        }
        long now = Environment.TickCount64;
        switch (outcome)
        {
            case UploadOutcome.Done:
                _lastHash = hash;
                _memory?.Save(_memoryKey, hash);
                _failures = 0;
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
        if (brightness >= 0 && !_stop)
        {
            ApplyBrightness(hid, brightness);
        }
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
        long now = Environment.TickCount64;
        if (_lastWarnMs == long.MinValue || now - _lastWarnMs >= WarnEveryMs)
        {
            _lastWarnMs = now;
            ServiceLog.Warn($"[{HandlerId}] {message}");
        }
    }

    private enum UploadOutcome
    {
        Done,
        Cancelled,
        Failed,
    }

    private UploadOutcome UploadJpeg(IBulkUsbPipe pipe, IHidDevice hid, byte[] jpeg)
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
            if (_stop || _cancelUpload)
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

    public bool SetBrightness(IBulkUsbPipe pipe, IHidDevice? hid, int percent)
    {
        if (hid is null)
        {
            return false;
        }
        lock (_hidLock)
        {
            if (_uploading)
            {
                // Applied when the upload ends; the app sends no report mid-transfer. Dark is
                // the exception: a lock-to-dark must not wait out the transfer.
                _pendingBrightness = percent;
                if (percent <= 0)
                {
                    _cancelUpload = true;
                    _abort.Set();
                }
                return true;
            }
        }
        return ApplyBrightness(hid, percent);
    }

    /// <summary>
    /// The vendor sequence: query the state, then write it back with one field changed. The
    /// state must come from a fresh reply, so older input is drained first; without one the
    /// write is skipped, because guessing the mode would switch the panel off its picture and
    /// clear the sleep setting. The app also repeats its mode select afterwards; this driver
    /// never selects a mode, so it has none to repeat.
    /// </summary>
    private bool ApplyBrightness(IHidDevice hid, int percent)
    {
        lock (_hidLock)
        {
            long now = Environment.TickCount64;
            if (now < _brightnessRetryAtMs)
            {
                return false;
            }
            var buffer = new byte[GlacialLcdProtocol.ReportBytes];
            for (int i = 0; i < DrainLimit && hid.Read(buffer, 0) > 0; i++)
            {
            }
            if (!hid.Write(GlacialLcdProtocol.EncodeQuery()))
            {
                return false;
            }
            for (int attempt = 0; attempt < StateReplyAttempts; attempt++)
            {
                int read = hid.Read(buffer, StateReplyWaitMs);
                if (read < 0)
                {
                    return false;
                }
                if (read > 0 && GlacialLcdProtocol.ParseState(buffer.AsSpan(0, read)) is { } state)
                {
                    return hid.Write(GlacialLcdProtocol.EncodeSettings(percent, state.Mode, state.KeepOnWhileAsleep));
                }
            }
            _brightnessRetryAtMs = now + BrightnessRetryMs;
            Failed("no state reply; brightness not written");
            return false;
        }
    }

    public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        _stop = true;
        _cancelUpload = true;
        _abort.Set();
        _wake.Set();
        _worker?.Join(JoinOnDisconnectMs);
        _worker = null;
        lock (_hidLock)
        {
            _pipe = null;
            _hid = null;
            _pendingBrightness = -1;
        }
        lock (_frameLock)
        {
            _latest = null;
            _pending = false;
        }
    }
}
