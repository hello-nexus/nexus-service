using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>Drops every entry except <paramref name="keep"/>, such as ones stored under an earlier build's keys.</summary>
    void ClearOthers(string keep);

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
        _store.Read(s => s.GlacialLcdPictureHashes.TryGetValue(key, out hash));
        return hash;
    }

    public void Save(string key, string hash) => _store.Update(s => s.GlacialLcdPictureHashes[key] = hash);

    public void ClearOthers(string keep) => _store.Update(s =>
    {
        foreach (var other in s.GlacialLcdPictureHashes.Keys.Where(k => k != keep).ToList())
        {
            s.GlacialLcdPictureHashes.Remove(other);
        }
    });

    /// <summary>Flushed at once: a crash after the announce must not leave the old hash on disk for a half-written panel.</summary>
    public void Clear(string key)
    {
        _store.Update(s => s.GlacialLcdPictureHashes.Remove(key));
        _store.FlushNow();
    }
}

/// <summary>
/// Shared by the native-mode worker and the streamed-panel driver so only one of them talks to
/// the panel at a time: a mutex both take around their HID traffic, and the flags they use to
/// hand it over.
/// </summary>
public sealed class GlacialLcdLink
{
    private readonly object _streamLock = new();
    private object? _streamOwner;
    private volatile bool _streaming;
    private volatile bool _cancelUpload;

    /// <summary>Held for each pass of the native worker and for the whole of an upload.</summary>
    public object Sync { get; } = new();

    /// <summary>True while a Nexus Panel is assigned to the display; the native worker then leaves the mode alone.</summary>
    public bool Streaming => _streaming;

    /// <summary>The native worker has edits waiting, so the upload does not start another before they go out.</summary>
    public volatile bool NativePending;

    /// <summary>The display is off or at zero brightness, so there is nothing to upload to.</summary>
    public volatile bool Dark;

    /// <summary>The panel may not be showing the stored picture: set when a stream opens and when a native write may have changed what shows.</summary>
    public volatile bool ShowPending;

    /// <summary>Asks a running upload to stop, so a dark display or a sleep edit does not wait out the transfer.</summary>
    public bool CancelRequested => _cancelUpload;

    /// <summary>The user's choice still is the Nexus Panel mode; cleared the moment they pick a native one, so an upload stops before the session closes.</summary>
    public volatile bool StreamWanted = true;

    /// <summary>Raised when the streaming flag flips, so the native worker looks again at once.</summary>
    public event Action? Changed;

    /// <summary>Raised when a cancel is requested, so the upload's pacing wait ends.</summary>
    public event Action? Cancelled;

    public void StreamOpened(object stream)
    {
        lock (_streamLock)
        {
            _streamOwner = stream;
            _streaming = true;
        }
        ClearCancel();
        ShowPending = true;
        Changed?.Invoke();
    }

    /// <summary>Ends the stream only if <paramref name="stream"/> is the one that is open; a stale transport's close is ignored.</summary>
    public void StreamClosed(object stream)
    {
        lock (_streamLock)
        {
            if (!ReferenceEquals(_streamOwner, stream))
            {
                return;
            }
            _streamOwner = null;
            _streaming = false;
        }
        RequestCancel();
        Changed?.Invoke();
    }

    public void RequestCancel()
    {
        _cancelUpload = true;
        Cancelled?.Invoke();
    }

    public void ClearCancel() => _cancelUpload = false;
}

/// <summary>
/// The ASUS ROG Crosshair X870E Glacial motherboard LCD as a streamed panel. The panel only
/// takes a stored custom picture, so each frame is a full upload: a worker thread owned by the
/// driver encodes the newest rendered frame as JPEG and sends it whenever it differs from the
/// picture the panel already holds, one upload after another, so frames that arrive meanwhile
/// collapse into the latest one. Transfers go back to back; the vendor app's captured gaps
/// are kept only as a scale that a hidden setting can switch back on. It runs
/// only while the user has chosen the Nexus Panel mode, stays out of the way of native edits,
/// and sends nothing while the display is dark.
/// </summary>
public sealed class GlacialLcdPanelDriver : IBulkPanelDriver
{
    /// <summary>
    /// How long the content must stay unchanged before it is uploaded. Just long enough to
    /// merge the frames one layout change produces in a burst (they arrive within a few tens
    /// of milliseconds of each other), well under the interval of the steady frame rate, so a
    /// single frame is not held back.
    /// </summary>
    public const int DefaultSettleMs = 250;

    /// <summary>Content that never settles is uploaded anyway after waiting this long.</summary>
    public const int DefaultMaxSettleMs = 2000;

    /// <summary>How long to wait for the panel's report after a command; the panel answers a command at once, so this only runs out when it stays silent.</summary>
    public const int ReplyWaitMs = 30;

    /// <summary>How long to look for a report after a bulk transfer.</summary>
    public const int ChunkReplyWaitMs = 2;

    /// <summary>Longest wait for a report after the end report, before the picture is shown.</summary>
    public const int FinishReplyWaitMs = 500;

    /// <summary>The first uploads after a start log every report the panel sends; later ones log only reports that are not a plain echo.</summary>
    private const int FullReplyLogUploads = 3;

    /// <summary>First retry delay after a failed upload; doubles each time, capped at the cap below.</summary>
    public const int DefaultBackoffBaseMs = 10_000;

    /// <summary>An upload may be dropped for a newer frame this many times in a row before the next one is let through to finish.</summary>
    private const int MaxSupersedes = 2;

    /// <summary>Longest wait between retries of a failing upload.</summary>
    public const int BackoffCapMs = 5 * 60 * 1000;

    /// <summary>Delay before the picture is uploaded again after a native edit cancelled it.</summary>
    public const int DefaultCancelRetryMs = 5000;

    /// <summary>The remembered picture is written to settings only once it has stood this long, and when the stream stops.</summary>
    public const int PersistAfterMs = 10 * 60 * 1000;

    private const int PollMs = 200;
    private const int JoinOnDisconnectMs = 250;
    private const int WarnEveryMs = 60_000;
    private const int ShowRetryMs = 5000;

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
    private Func<int>? _paceSource;

    /// <summary>Test seam: sees every line this driver logs at info level.</summary>
    internal Action<string>? LogSink { get; set; }

    private void Info(string message)
    {
        ServiceLog.Info(message);
        LogSink?.Invoke(message);
    }
    private volatile int _pace;
    private int _replyLoggedUploads;

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
    private string? _persistedHash;
    private long _hashSetMs;
    private long _nextAllowedMs;
    private long _nextShowMs;
    private int _failures;

    // Serializes the write of the remembered picture against forgetting it, so a stale hash is never written back.
    private readonly object _persistLock = new();
    private int _supersedes;

    private string _memoryKey = "";
    private int _uploadsCompleted;
    private int _uploadAttempts;
    private int _ticks;

    private sealed class WorkerState
    {
        public volatile bool Stop;
        public Thread? Thread;
    }

    /// <param name="paceScalePercent">Scales every vendor pacing gap; 0 sends everything back to back, 100 is the captured pace. A bound source (see <see cref="BindPaceScale"/>) overrides it per upload.</param>
    public GlacialLcdPanelDriver(
        GlacialLcdLink link,
        int paceScalePercent = 0,
        int settleMs = DefaultSettleMs,
        int maxSettleMs = DefaultMaxSettleMs,
        int backoffBaseMs = DefaultBackoffBaseMs,
        int cancelRetryMs = DefaultCancelRetryMs,
        Func<long>? clock = null)
    {
        _link = link;
        _paceScalePercent = paceScalePercent;
        _pace = paceScalePercent;
        _settleMs = settleMs;
        _maxSettleMs = maxSettleMs;
        _backoffBaseMs = backoffBaseMs;
        _cancelRetryMs = cancelRetryMs;
        _clock = clock ?? (() => Environment.TickCount64);
        _link.Cancelled += () => _abort.Set();
    }

    public string HandlerId => GlacialLcdWorker.HandlerId;
    public string Name => "ROG Crosshair X870E Glacial LCD";
    public int VendorId => 0x0B05;
    public IReadOnlyList<int> ProductIds { get; } = new[] { GlacialLcdProtocol.ProductId };
    public string Surface => Models.Panel.PanelSurfaces.LcdSquare;

    /// <summary>The overlay hands over a frame about this often; the worker decides what is uploaded.</summary>
    public int Fps => 1;

    public byte WritePipeId => 0x02;
    public byte ReadPipeId => 0x82;
    public bool NeedsHidChannel => true;

    /// <summary>The handle is opened overlapped, like the native worker's, so any read it makes honors its timeout.</summary>
    public bool HidOpensForInput => true;

    /// <summary>A second LCD on the same surface must not share this panel's layout record.</summary>
    public bool MultiInstance => true;

    /// <summary>The panel's USB serial read as a manufacturer string early in boot, so the stream and the remembered picture are keyed on the handler id.</summary>
    public bool SingleUnitPerPc => true;

    public void StreamOpened(object stream) => _link.StreamOpened(stream);

    public void StreamClosed(object stream) => _link.StreamClosed(stream);

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

    /// <summary>Reads the pace scale from settings at the start of each upload, so a change applies without a restart.</summary>
    public void BindPaceScale(Func<int> source) => _paceSource = source;

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
            _memoryKey = HandlerId;
            _memory?.ClearOthers(_memoryKey);
            var remembered = _memory?.Load(_memoryKey);
            lock (_stateLock)
            {
                _persistedHash = remembered;
                if (remembered is not null)
                {
                    _lastHash = remembered;
                    _hashSetMs = _clock();
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
                    else
                    {
                        ShowIfNeeded(state);
                    }
                    PersistIfStable();
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

    /// <summary>True when an upload may start or go on: a stream the user wants, the display lit, and no native edit waiting.</summary>
    private bool CanUpload() => _link.Streaming && _link.StreamWanted && !_link.Dark && !_link.NativePending;

    private bool Ready()
    {
        if (!CanUpload())
        {
            return false;
        }
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

    /// <summary>
    /// A picture the panel is known to hold needs no upload, but the panel may have been left
    /// showing something else (a stream just opened, a native write, a display turn-on), so the
    /// show step alone is sent.
    /// </summary>
    private void ShowIfNeeded(WorkerState state)
    {
        if (!_link.ShowPending || !CanUpload())
        {
            return;
        }
        long now = _clock();
        IHidDevice? hid;
        lock (_stateLock)
        {
            if (_lastHash is null || now < _nextShowMs)
            {
                return;
            }
            _nextShowMs = now + ShowRetryMs;
        }
        lock (_hidLock)
        {
            hid = state.Stop ? null : _hid;
        }
        if (hid is null)
        {
            return;
        }
        lock (_link.Sync)
        {
            if (state.Stop || !CanUpload())
            {
                return;
            }
            _link.ShowPending = false;
            if (!hid.Write(GlacialLcdProtocol.EncodeQuery()) || !hid.Write(GlacialLcdProtocol.EncodeCustomPicture()))
            {
                _link.ShowPending = true;
            }
        }
    }

    private void PersistIfStable()
    {
        lock (_stateLock)
        {
            if (_lastHash is null || _lastHash == _persistedHash || _clock() - _hashSetMs < PersistAfterMs)
            {
                return;
            }
        }
        PersistNow();
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
        var encodeWatch = System.Diagnostics.Stopwatch.StartNew();
        encoder ??= new BgraJpegEncoder(GlacialLcdProtocol.PanelWidth, GlacialLcdProtocol.PanelHeight, GlacialLcdProtocol.StreamJpegQuality);
        byte[] jpeg = encoder.Encode(snapshot).ToArray();
        long encodeMs = encodeWatch.ElapsedMilliseconds;
        string hash = Convert.ToHexString(SHA256.HashData(jpeg));
        lock (_stateLock)
        {
            if (hash == _lastHash)
            {
                // Already stored; ShowIfNeeded sends the show step if the panel may be elsewhere.
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
        _pace = Math.Max(0, _paceSource?.Invoke() ?? _paceScalePercent);
        if (ShouldStop(state))
        {
            // A cancel that arrived before the reset is still set; honor it instead of losing it.
            FinishUploading(state);
            if (!state.Stop)
            {
                MarkPending();
            }
            return;
        }
        Interlocked.Increment(ref _uploadAttempts);

        var outcome = UploadOutcome.Failed;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // One owner of the panel at a time: the native worker waits out the whole upload.
            lock (_link.Sync)
            {
                // Waiting for the lock may have outlasted a native edit or a mode change.
                outcome = ShouldStop(state) ? UploadOutcome.Cancelled : UploadJpeg(state, pipe, hid, jpeg);
                if (outcome == UploadOutcome.Done)
                {
                    _link.ShowPending = false;
                }
            }
        }
        catch (Exception ex)
        {
            // A write that failed because the link was torn down is a cancel, not a fault.
            outcome = ShouldStop(state) ? UploadOutcome.Cancelled : UploadOutcome.Failed;
            if (!state.Stop)
            {
                Failed($"upload failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        watch.Stop();
        FinishUploading(state);

        long now = _clock();
        if (outcome == UploadOutcome.Done)
        {
            lock (_stateLock)
            {
                _lastHash = hash;
                _hashSetMs = now;
                _failures = 0;
                _nextAllowedMs = 0;
            }
            _supersedes = 0;
            Interlocked.Increment(ref _uploadsCompleted);
            Info($"[{HandlerId}] picture uploaded, {jpeg.Length} bytes in {watch.ElapsedMilliseconds} ms (encode {encodeMs} ms, pace {_pace}%)");
        }
        else if (state.Stop)
        {
            // Torn down mid-upload: no delay, no backoff, and the frame state belongs to the next worker.
            return;
        }
        else if (outcome == UploadOutcome.Superseded)
        {
            // A newer settled frame is already pending; the next pass uploads it with no delay.
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
            Failed($"upload failed after {watch.ElapsedMilliseconds} ms (encode {encodeMs} ms); retrying in {delay / 1000} s");
        }
    }

    private void FinishUploading(WorkerState state)
    {
        lock (_hidLock)
        {
            // A worker that outlived its disconnect must not clear the flag of the one that replaced it.
            if (_current is null || ReferenceEquals(_current, state))
            {
                _uploading = false;
            }
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
        Superseded,
        Failed,
    }

    /// <summary>A newer frame has settled while this upload runs, so finishing the old picture would only be replaced.</summary>
    private bool NewerFrameWaiting()
    {
        long now = _clock();
        lock (_frameLock)
        {
            return _pending && (now - _lastChangeMs >= _settleMs || now - _firstPendingMs >= _maxSettleMs);
        }
    }

    /// <summary>A pacing wait from the capture, scaled by the pace in force; true when it was cut short by a cancel.</summary>
    private bool Pause(int capturedMs)
    {
        int ms = (int)((long)capturedMs * _pace / 100);
        return ms > 0 && _abort.Wait(ms);
    }

    /// <summary>
    /// Reads one report the panel sent, waiting up to the timeout, and logs it with the time
    /// since the upload began so a support bundle shows what the panel actually says. Never
    /// waits longer than the timeout and never fails the upload.
    /// </summary>
    private byte[]? ReadReply(IHidDevice hid, int timeoutMs, string step, byte requestOpcode, System.Diagnostics.Stopwatch sinceStart)
    {
        var buffer = new byte[GlacialLcdProtocol.ReportBytes];
        int read;
        try
        {
            read = hid.Read(buffer, timeoutMs);
        }
        catch (Exception ex)
        {
            Info($"[{HandlerId}] reply read failed at {step}: {ex.GetType().Name}");
            return null;
        }
        if (read <= 0)
        {
            return null;
        }
        var report = buffer.AsSpan(0, read).ToArray();
        bool echo = report.Length > 1 && report[1] == requestOpcode;
        if (Volatile.Read(ref _replyLoggedUploads) < FullReplyLogUploads || !echo)
        {
            int last = report.Length;
            while (last > 0 && report[last - 1] == 0)
            {
                last--;
            }
            Info($"[{HandlerId}] reply +{sinceStart.ElapsedMilliseconds} ms after {step}: {Convert.ToHexString(report.AsSpan(0, last))}");
        }
        return report;
    }

    private void DrainInput(IHidDevice hid)
    {
        var buffer = new byte[GlacialLcdProtocol.ReportBytes];
        for (int i = 0; i < 64 && hid.Read(buffer, 0) > 0; i++)
        {
        }
    }

    private bool ShouldStop(WorkerState state) => state.Stop || _link.CancelRequested || !CanUpload();

    private UploadOutcome UploadJpeg(WorkerState state, IBulkUsbPipe pipe, IHidDevice hid, byte[] jpeg)
    {
        UploadOutcome Fail() => ShouldStop(state) ? UploadOutcome.Cancelled : UploadOutcome.Failed;

        // Reports from earlier traffic must not be taken for answers to this upload's commands.
        DrainInput(hid);
        var sinceStart = System.Diagnostics.Stopwatch.StartNew();

        // The reports of the captured upload, in order; each is followed by a short look at what the panel says back.
        lock (_hidLock)
        {
            if (!hid.Write(GlacialLcdProtocol.EncodeQuery()))
            {
                return Fail();
            }
            ReadReply(hid, ReplyWaitMs, "DC", 0xDC, sinceStart);
        }
        foreach (var (report, gapMs) in GlacialLcdProtocol.EncodeCustomImagePrelude(jpeg.Length))
        {
            if (Pause(gapMs) || ShouldStop(state))
            {
                return UploadOutcome.Cancelled;
            }
            if (report[1] == 0xF1)
            {
                // The last point an upload can be dropped for a newer frame: past the flush the
                // panel is erased, so the picture is always finished. A cap keeps a stream of
                // changing frames from dropping every upload before it starts.
                if (_supersedes < MaxSupersedes && NewerFrameWaiting())
                {
                    _supersedes++;
                    return UploadOutcome.Superseded;
                }
                // The flush may erase the stored picture, so from here the panel holds nothing known.
                ForgetPicture();
            }
            lock (_hidLock)
            {
                if (!hid.Write(report))
                {
                    return Fail();
                }
                ReadReply(hid, ReplyWaitMs, "prelude " + report[1].ToString("X2", System.Globalization.CultureInfo.InvariantCulture), report[1], sinceStart);
            }
        }
        if (Pause(GlacialLcdProtocol.ChunkLeadMs) || ShouldStop(state))
        {
            return UploadOutcome.Cancelled;
        }
        var chunk = new byte[GlacialLcdProtocol.ChunkBytes];
        var chunkTimer = new System.Diagnostics.Stopwatch();
        int index = 0;
        for (int offset = 0; offset < jpeg.Length; offset += GlacialLcdProtocol.ChunkBytes, index++)
        {
            if (ShouldStop(state))
            {
                return UploadOutcome.Cancelled;
            }
            // The last transfer is a full chunk, zero padded past the picture's end.
            Array.Clear(chunk);
            jpeg.AsSpan(offset, Math.Min(GlacialLcdProtocol.ChunkBytes, jpeg.Length - offset)).CopyTo(chunk);
            chunkTimer.Restart();
            if (!pipe.Write(chunk))
            {
                return Fail();
            }
            lock (_hidLock)
            {
                ReadReply(hid, ChunkReplyWaitMs, "chunk " + index, 0, sinceStart);
            }
            // Submit to submit: the gap counts from this write's start, so a slow write does not stretch it.
            int scaled = (int)((long)GlacialLcdProtocol.ChunkIntervalMs * _pace / 100);
            int remaining = scaled - (int)chunkTimer.ElapsedMilliseconds;
            if (remaining > 0 && _abort.Wait(remaining))
            {
                return UploadOutcome.Cancelled;
            }
        }
        if (ShouldStop(state))
        {
            return UploadOutcome.Cancelled;
        }
        lock (_hidLock)
        {
            if (!hid.Write(GlacialLcdProtocol.EncodeUploadEnd()))
            {
                return Fail();
            }
            // The panel's report to the end command; whether it marks the storing as done is what the logs will show.
            ReadReply(hid, FinishReplyWaitMs, "end 73FF", 0x73, sinceStart);
        }
        if (Pause(GlacialLcdProtocol.FinishSettleMs) || ShouldStop(state))
        {
            return UploadOutcome.Cancelled;
        }
        lock (_hidLock)
        {
            // The picture is stored; showing it is the app's last step.
            if (!hid.Write(GlacialLcdProtocol.EncodeQuery()))
            {
                return Fail();
            }
            ReadReply(hid, ReplyWaitMs, "DC before show", 0xDC, sinceStart);
            if (!hid.Write(GlacialLcdProtocol.EncodeCustomPicture()))
            {
                return Fail();
            }
            ReadReply(hid, ReplyWaitMs, "show 51", 0x51, sinceStart);
        }
        Interlocked.Increment(ref _replyLoggedUploads);
        return UploadOutcome.Done;
    }

    private void ForgetPicture()
    {
        lock (_persistLock)
        {
            bool stored;
            lock (_stateLock)
            {
                _lastHash = null;
                stored = _persistedHash is not null;
                _persistedHash = null;
            }
            if (stored)
            {
                _memory?.Clear(_memoryKey);
            }
        }
    }

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
            // The worker stops on its own; a short join keeps the hub's lock from waiting out a pacing gap.
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
        PersistNow();
    }

    /// <summary>
    /// Writes the remembered picture to settings now. The disconnect does it, but a service
    /// shutdown skips the disconnect, so the shutdown path calls this before the settings flush.
    /// </summary>
    public void PersistNow()
    {
        lock (_persistLock)
        {
            string? hash;
            lock (_stateLock)
            {
                hash = _lastHash is not null && _lastHash != _persistedHash ? _lastHash : null;
                if (hash is not null)
                {
                    _persistedHash = hash;
                }
            }
            if (hash is not null)
            {
                _memory?.Save(_memoryKey, hash);
            }
        }
    }

    public void PersistState() => PersistNow();
}
