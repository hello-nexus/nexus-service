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

    /// <summary>The host is going to sleep: no upload starts until it resumes.</summary>
    public volatile bool Suspended;

    /// <summary>False keeps the stream driver from ever uploading; the service sets it from the hidden setting at start.</summary>
    public volatile bool NexusPanelEnabled = true;

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
    /// <summary>How long the content must stay unchanged before it is uploaded: enough to merge the burst of frames one layout change produces, short of the steady frame interval.</summary>
    public const int DefaultSettleMs = 250;

    /// <summary>Content that never settles is uploaded anyway after waiting this long.</summary>
    public const int DefaultMaxSettleMs = 2000;

    /// <summary>How long to wait for the panel's report after a command; the panel answers a command at once, so this only runs out when it stays silent.</summary>
    public const int ReplyWaitMs = 30;

    /// <summary>Longest wait for a report after the end report, before the picture is shown.</summary>
    public const int FinishReplyWaitMs = 500;

    /// <summary>The wait after the end report is taken in slices of this length, so a cancel or a disconnect ends it at once.</summary>
    private const int FinishReplySliceMs = 50;

    /// <summary>The first uploads after a connect log every report the panel sends; after that a step logs only when its report changes.</summary>
    private const int FullReplyLogUploads = 3;

    private const int MaxPaceScalePercent = 1000;

    /// <summary>The slot earlier builds always wrote, taken as the one on display when nothing is remembered.</summary>
    private const int DefaultShownSlot = 2;

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

    /// <summary>Hard cap on the wait for an upload in flight before a detach; the wait itself is sized from the upload's own estimate.</summary>
    public const int DefaultDisconnectWaitMs = 120_000;

    private const int EstimateOverheadMs = 3000;

    /// <summary>Reports in the prelude before the bulk transfers.</summary>
    private const int PreludeSteps = 5;

    /// <summary>State queries tried before the panel is called stalled.</summary>
    private const int StallDcTries = 3;

    private static readonly int[] ProbeBackoffMs = { 30_000, 60_000, 120_000 };

    /// <summary>A device that sends nothing in this long after a state query is taken to be stalled.</summary>
    public const int DefaultStallReplyMs = 3000;

    internal int StallReplyMs { get; set; } = DefaultStallReplyMs;

    private const int RateLogMs = 10_000;
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
    private readonly WarnLimiter _readWarn = new();
    private readonly ManualResetEventSlim _wake = new(false);
    // Cuts a pacing wait short, so a cancel or a disconnect never sits out the vendor's gaps.
    private readonly ManualResetEventSlim _abort = new(false);

    private ILcdUploadMemory? _memory;
    private GlacialLcdTuning? _tuning;

    // Set only by a disconnect: an upload in progress runs to its last step through a cancel or a dark display.
    private readonly ManualResetEventSlim _hardAbort = new(false);
    private readonly ManualResetEventSlim _idle = new(true);
    private readonly int _disconnectWaitMs;
    private long _nextStartMs;
    private long _lastRateLogMs;
    private int _uploadSeq;
    private volatile bool _quiescing;
    private int _inflightEstimateMs;
    private int _stallStreak;
    private long _nextProbeMs;

    /// <summary>Per step type, how many uploads in a row got no reply to it; while above zero that step runs on timers, with a probe every few uploads.</summary>
    private int _silentPrelude;
    private int _silentChunk;
    private int _silentCommit;
    private bool _ackSkipLogged;
    private bool _replyDrivenSeen = true;
    private const int AckProbeEvery = 8;
    private bool _rawNeedsSync;

    private volatile bool _rawActive;
    private int _rawFailStreak;
    private bool _rawKnobSeen;
    private volatile bool _rawOffPending;
    private int _ackUploads;
    private readonly object _rawLock = new();

    /// <summary>Set while no raw exit is owed; reset together with <see cref="_rawActive"/>, so a shutdown wait can tell the panel may still be in the stream mode.</summary>
    private readonly ManualResetEventSlim _rawExitDone = new(true);

    private void SetRawActive()
    {
        lock (_rawLock)
        {
            _rawActive = true;
            _rawExitDone.Reset();
        }
        _tuning?.SetRawActive(true);
    }

    private void ClearRawActive()
    {
        lock (_rawLock)
        {
            _rawActive = false;
            _rawExitDone.Set();
        }
        _tuning?.SetRawActive(false);
    }
    private const int RawFailuresBeforeOff = 3;
    internal const int RawMinFrameIntervalMs = 500;
    private const int RawEnterSettleMs = 100;

    /// <summary>Test seam: sees every line this driver logs at info level.</summary>
    internal Action<string>? LogSink { get; set; }

    private void Info(string message)
    {
        ServiceLog.Info(message);
        LogSink?.Invoke(message);
    }
    private volatile int _pace;
    private int _uploadsSinceConnect;
    private int _pictureGeneration;
    private bool _logAllReplies;
    private readonly Dictionary<string, string> _lastLoggedReply = new();

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

    // Guarded by _stateLock; outlives a worker so the remembered picture carries over a reconnect. The backoff does not: Connect resets it.
    private string? _lastHash;
    private int _shownSlot = DefaultShownSlot;
    private string? _persistedValue;
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

    /// <param name="paceScalePercent">Scales every vendor pacing gap; 0 sends everything back to back, 100 is the captured pace, and used only while no tuning is bound.</param>
    public GlacialLcdPanelDriver(
        GlacialLcdLink link,
        int paceScalePercent = GlacialLcdTuning.DefaultPacePercent,
        int settleMs = DefaultSettleMs,
        int maxSettleMs = DefaultMaxSettleMs,
        int backoffBaseMs = DefaultBackoffBaseMs,
        int cancelRetryMs = DefaultCancelRetryMs,
        Func<long>? clock = null,
        int disconnectWaitMs = DefaultDisconnectWaitMs)
    {
        _disconnectWaitMs = disconnectWaitMs;
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

    public void StreamOpened(object stream)
    {
        GlacialLcdLog.Write("stream opened");
        _tuning?.Resume();
        _link.StreamOpened(stream);
    }

    /// <summary>Supplies the upload knobs and counters; without it the driver uses the built-in defaults.</summary>
    public void BindTuning(GlacialLcdTuning tuning) => _tuning = tuning;

    private GlacialLcdTuningValues Cfg() =>
        _tuning?.Current() ?? new GlacialLcdTuningValues(
            0, GlacialLcdTuning.MaxMaxFps, Math.Clamp(_paceScalePercent, 0, MaxPaceScalePercent), false,
            GlacialLcdTuning.DefaultScalePercent, GlacialLcdTuning.ScaleSend, GlacialLcdProtocol.StreamJpegQuality,
            GlacialLcdProtocol.ChunkBytes / 1024, true, false, false);

    public void StreamClosed(object stream)
    {
        GlacialLcdLog.Write("stream closed");
        _link.StreamClosed(stream);
    }

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
                _hardAbort.Reset();
                _quiescing = false;
            }
        }
        if (start is not null)
        {
            GlacialLcdLog.Write("stream driver connected");
            _memoryKey = HandlerId;
            lock (_stateLock)
            {
                // A backoff earned against the previous link means nothing to a new one.
                _failures = 0;
                _nextAllowedMs = 0;
            }
            Volatile.Write(ref _uploadsSinceConnect, 0);
            ResetAckSilence();
            _rawNeedsSync = false;
            ClearRawActive();
            _rawFailStreak = 0;
            _tuning?.SetRawActive(false);
            lock (_lastLoggedReply)
            {
                _lastLoggedReply.Clear();
            }
            _memory?.ClearOthers(_memoryKey);
            var remembered = _memory?.Load(_memoryKey);
            lock (_stateLock)
            {
                _persistedValue = remembered;
                if (remembered is not null)
                {
                    if (DecodeRemembered(remembered) is { } decoded)
                    {
                        (_shownSlot, _lastHash) = decoded;
                        _hashSetMs = _clock();
                    }
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

    private sealed class EncoderCache : IDisposable
    {
        public BgraJpegEncoder? Encoder;
        public (int W, int H, int Q) Key;
        public byte[]? Snapshot;
        public byte[]? Scaled;
        public byte[]? Back;

        public void Dispose() => Encoder?.Dispose();
    }

    private void Run(WorkerState state)
    {
        using var cache = new EncoderCache();
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
                var cfg = Cfg();
                if (cfg.RawStream != _rawKnobSeen)
                {
                    // A fresh start for the failure count whenever the knob is turned on.
                    _rawKnobSeen = cfg.RawStream;
                    if (cfg.RawStream)
                    {
                        _rawFailStreak = 0;
                    }
                }
                if (_rawOffPending)
                {
                    _rawOffPending = false;
                    _tuning?.Apply(new GlacialLcdTuningChange { RawStream = false });
                    cfg = Cfg();
                }
                if (cfg.RawStream || _rawActive)
                {
                    RawPass(state, cache, cfg);
                }
                else if (Ready())
                {
                    UploadPending(state, cache);
                }
                else
                {
                    ProbeStalled(state);
                    ShowIfNeeded(state);
                }
                PersistIfStable();
                LogRate();
            }
            catch (Exception ex)
            {
                GlacialLcdLog.Write($"worker exception: {ex}");
                Failed($"worker error: {ex.GetType().Name}: {ex.Message}");
            }
            Interlocked.Increment(ref _ticks);
        }
    }

    /// <summary>After a stall, sends a state query now and then; a reply resumes uploads on its own.</summary>
    private void ProbeStalled(WorkerState state)
    {
        if (_tuning is not { Stalled: true } || _stallStreak == 0 || _clock() < _nextProbeMs || !CanUpload())
        {
            return;
        }
        bool answered;
        var sinceStart = System.Diagnostics.Stopwatch.StartNew();
        var run = new UploadRun { Seq = Volatile.Read(ref _uploadSeq) };
        lock (_link.Sync)
        {
            if (!CanUpload() || !HidWrite(state, GlacialLcdProtocol.EncodeQuery()))
            {
                return;
            }
            Tx(run, sinceStart, "probe DC");
            answered = AwaitAnyReply(state, StallReplyMs, "probe DC", sinceStart);
        }
        if (answered)
        {
            GlacialLcdLog.Write("probe answered, uploads resume");
            _tuning.Resume();
            MarkPending();
            return;
        }
        int backoff = ProbeBackoffMs[Math.Min(_stallStreak, ProbeBackoffMs.Length - 1)];
        _stallStreak++;
        _nextProbeMs = _clock() + backoff;
        GlacialLcdLog.Write($"probe unanswered, next in {backoff / 1000}s");
    }

    private void LogRate()
    {
        long now = Environment.TickCount64;
        if (_lastRateLogMs == 0)
        {
            _lastRateLogMs = now;
            return;
        }
        if (now - _lastRateLogMs < RateLogMs || _tuning is null)
        {
            return;
        }
        _lastRateLogMs = now;
        var st = _tuning.Stats();
        GlacialLcdLog.Write($"rate {st.UploadsPerSec:0.00} uploads/s ok={st.UploadsOk} failed={st.UploadsFailed} lastUploadMs={st.LastUploadMs} lastBytes={st.LastFrameBytes} stalled={st.Stalled}");
    }

    /// <summary>True when an upload may start or go on: a stream the user wants, the display lit, and no native edit waiting.</summary>
    private bool CanUpload() => !_quiescing && !_link.Suspended && _link.NexusPanelEnabled && _link.Streaming && _link.StreamWanted && !_link.Dark && !_link.NativePending;

    private bool Ready()
    {
        if (!CanUpload())
        {
            return false;
        }
        if (_tuning is { Stalled: true })
        {
            return false;
        }
        long now = _clock();
        lock (_stateLock)
        {
            if (now < _nextAllowedMs || now < _nextStartMs)
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
        if (!_link.ShowPending || !CanUpload() || _tuning is { Stalled: true })
        {
            return;
        }
        long now = _clock();
        int slot;
        lock (_stateLock)
        {
            if (_lastHash is null || now < _nextShowMs)
            {
                return;
            }
            _nextShowMs = now + ShowRetryMs;
            slot = _shownSlot;
        }
        lock (_link.Sync)
        {
            if (state.Stop || !CanUpload())
            {
                return;
            }
            _link.ShowPending = false;
            if (!HidWrite(state, GlacialLcdProtocol.EncodeQuery()) || !HidWrite(state, GlacialLcdProtocol.EncodeCustomPicture(slot)))
            {
                _link.ShowPending = true;
            }
        }
    }

    private void PersistIfStable()
    {
        lock (_stateLock)
        {
            if (_lastHash is null || RememberedValue() == _persistedValue || _clock() - _hashSetMs < PersistAfterMs)
            {
                return;
            }
        }
        PersistNow();
    }

    private void UploadPending(WorkerState state, EncoderCache cache)
    {
        var cfg = Cfg();
        lock (_frameLock)
        {
            if (_latest is null)
            {
                return;
            }
            if (cache.Snapshot is null || cache.Snapshot.Length != _latest.Length)
            {
                cache.Snapshot = new byte[_latest.Length];
            }
            _latest.CopyTo(cache.Snapshot, 0);
            _pending = false;
        }
        var encodeWatch = System.Diagnostics.Stopwatch.StartNew();
        int frameW = GlacialLcdProtocol.PanelWidth;
        int frameH = GlacialLcdProtocol.PanelHeight;
        int encW = frameW;
        int encH = frameH;
        ReadOnlySpan<byte> input = cache.Snapshot;
        if (cfg.ScalePercent < 100)
        {
            var (sw, sh) = GlacialLcdScaler.ScaledSize(frameW, frameH, cfg.ScalePercent);
            if (cache.Scaled is null || cache.Scaled.Length != sw * sh * 4)
            {
                cache.Scaled = new byte[sw * sh * 4];
            }
            GlacialLcdScaler.Resize(cache.Snapshot, frameW, frameH, cache.Scaled, sw, sh);
            if (cfg.ScaleMode == GlacialLcdTuning.ScaleUpscale)
            {
                cache.Back ??= new byte[frameW * frameH * 4];
                GlacialLcdScaler.Resize(cache.Scaled, sw, sh, cache.Back, frameW, frameH);
                input = cache.Back;
            }
            else
            {
                input = cache.Scaled;
                encW = sw;
                encH = sh;
            }
        }
        if (cache.Encoder is null || cache.Key != (encW, encH, cfg.JpegQuality))
        {
            cache.Encoder?.Dispose();
            cache.Encoder = new BgraJpegEncoder(encW, encH, cfg.JpegQuality);
            cache.Key = (encW, encH, cfg.JpegQuality);
        }
        byte[] jpeg = cache.Encoder.Encode(input).ToArray();
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
            _idle.Reset();
        }
        _abort.Reset();
        _pace = cfg.PacePercent;
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

        int targetSlot;
        int generation;
        lock (_stateLock)
        {
            // The slot on display is never written when two are in use: the new picture goes to the other one and is shown when complete.
            targetSlot = !cfg.DoubleBuffer
                ? GlacialLcdProtocol.CustomSlots[^1]
                : _shownSlot == GlacialLcdProtocol.CustomSlots[0] ? GlacialLcdProtocol.CustomSlots[1] : GlacialLcdProtocol.CustomSlots[0];
            generation = _pictureGeneration;
        }
        int estimateMs = EstimateUploadMs(jpeg.Length, cfg);
        Volatile.Write(ref _inflightEstimateMs, estimateMs);
        _nextStartMs = cfg.MaxFps >= GlacialLcdTuning.MaxMaxFps ? 0 : _clock() + (long)(1000.0 / cfg.MaxFps);
        int seq = Interlocked.Increment(ref _uploadSeq);
        GlacialLcdLog.Write($"u{seq} start settings={cfg.Id} frame={encW}x{encH} jpegBytes={jpeg.Length} encodeMs={encodeMs} slot={targetSlot} pace={cfg.PacePercent} chunkKb={cfg.ChunkKb} q={cfg.JpegQuality} scale={cfg.ScalePercent}/{cfg.ScaleMode} replyDriven={cfg.ReplyDriven} closeWriteMode={cfg.CloseWriteMode} doubleBuffer={cfg.DoubleBuffer} maxFps={cfg.MaxFps} estimateMs={estimateMs}");
        var run = new UploadRun { Seq = seq };
        var outcome = UploadOutcome.Failed;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // One owner of the panel at a time: the native worker waits out the whole upload.
            lock (_link.Sync)
            {
                // Waiting for the lock may have outlasted a native edit or a mode change.
                outcome = ShouldStop(state) ? UploadOutcome.Cancelled : UploadJpeg(state, jpeg, targetSlot, run, cfg);
                if (outcome == UploadOutcome.Done)
                {
                    _link.ShowPending = false;
                }
            }
        }
        catch (Exception ex)
        {
            // A write that failed because the link was torn down is a cancel, not a fault.
            outcome = state.Stop ? UploadOutcome.Cancelled : UploadOutcome.Failed;
            GlacialLcdLog.Write($"u{seq} exception: {ex}");
            if (!state.Stop)
            {
                run.Error = $"{ex.GetType().Name}: {ex.Message}";
                Failed($"upload failed: {run.Error}");
            }
        }
        watch.Stop();
        FinishUploading(state);
        if (!state.Stop && run.AckReplies + run.AckTimeouts > 0)
        {
            // Only steps that were actually waited on count. A worker stopped by a replug skips this once the stop is visible, which narrows but does not close the window against the new link's counters.
            _silentPrelude = Silence(_silentPrelude, run.PreludeReplies, run.PreludeMisses);
            // A prelude that got no reply at all ended the reply waits, so the later steps count as silent too.
            int skipped = !run.Probe && run.PreludeReplies == 0 && run.PreludeMisses >= 2 ? 1 : 0;
            _silentChunk = Silence(_silentChunk, run.ChunkReplies, run.ChunkMisses + skipped);
            _silentCommit = Silence(_silentCommit, run.CommitReplies, run.CommitMisses + skipped);
            _tuning?.RecordAcks(run.AckReplies, run.AckTimeouts, run.AckWaitMs, run.CommitWaitMs);
        }
        GlacialLcdLog.Write($"u{seq} end outcome={outcome} totalMs={watch.ElapsedMilliseconds} acks={run.AckReplies}/{run.AckReplies + run.AckTimeouts} ackWaitMs={run.AckWaitMs} commitWaitMs={run.CommitWaitMs}{(run.Error is null ? "" : " error=" + run.Error)}");
        if (outcome != UploadOutcome.Done && run.FlushSent)
        {
            // What the flush clears is not known to stop at the slot being written, so after a
            // failed or cut-short upload nothing is assumed to be on display: the next frame is
            // uploaded again instead of being taken for the stored one. A stopped worker does
            // this only if no upload has stored or forgotten a picture since it started (the
            // check is made under the state lock), so it cannot wipe what a newer worker stored.
            ForgetPicture(state.Stop ? generation : null);
        }

        long now = _clock();
        if (outcome == UploadOutcome.Done)
        {
            lock (_stateLock)
            {
                _lastHash = hash;
                _pictureGeneration++;
                _shownSlot = targetSlot;
                _hashSetMs = now;
                _failures = 0;
                _nextAllowedMs = 0;
            }
            _supersedes = 0;
            _stallStreak = 0;
            Interlocked.Increment(ref _uploadsCompleted);
            _tuning?.UploadOk(watch.ElapsedMilliseconds, jpeg.Length, encodeMs);
            Info($"[{HandlerId}] picture uploaded, {jpeg.Length} bytes in {watch.ElapsedMilliseconds} ms (encode {encodeMs} ms, pace {_pace}%)");
        }
        else if (outcome == UploadOutcome.Stalled)
        {
            MarkPending();
            int backoff = ProbeBackoffMs[Math.Min(_stallStreak, ProbeBackoffMs.Length - 1)];
            _stallStreak++;
            _nextProbeMs = now + backoff;
            GlacialLcdLog.Write($"stall #{_stallStreak}: probing again in {backoff / 1000}s");
            _tuning?.Stall(run.Error ?? "no reply");
            Failed($"upload stalled: {run.Error}");
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
            _tuning?.UploadFailed(run.Error ?? "upload failed");
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
                _idle.Set();
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
        Stalled,
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
        return ms > 0 && _hardAbort.Wait(ms);
    }

    /// <summary>
    /// Reads one report the panel sent, waiting up to the timeout, and logs it with the time
    /// since the upload began so a support bundle shows what the panel says. Logs every report
    /// for the first uploads after a connect, then only a report that differs from the last one
    /// logged for the step. Never waits longer than the timeout and never fails the upload.
    /// </summary>
    private byte[]? ReadReply(IHidDevice hid, int timeoutMs, string step, string key, System.Diagnostics.Stopwatch sinceStart, out bool failed)
    {
        var buffer = new byte[GlacialLcdProtocol.ReportBytes];
        int read;
        failed = false;
        try
        {
            read = hid.Read(buffer, timeoutMs);
        }
        catch (Exception ex)
        {
            failed = true;
            GlacialLcdLog.Write($"u{Volatile.Read(ref _uploadSeq)} RX read failed at {step}: {ex.GetType().Name}: {ex.Message}");
            // Its own limiter, so a dead read path cannot hide the upload-failed warning.
            if (_readWarn.Allow(_clock()))
            {
                ServiceLog.Warn($"[{HandlerId}] reply read failed at {step}: {ex.GetType().Name}");
            }
            return null;
        }
        if (read < 0)
        {
            failed = true;
            return null;
        }
        if (read == 0)
        {
            return null;
        }
        var report = buffer.AsSpan(0, read).ToArray();
        int last = report.Length;
        while (last > 0 && report[last - 1] == 0)
        {
            last--;
        }
        string hex = Convert.ToHexString(report.AsSpan(0, last));
        bool changed;
        lock (_lastLoggedReply)
        {
            changed = !_lastLoggedReply.TryGetValue(key, out var previous) || previous != hex;
            _lastLoggedReply[key] = hex;
        }
        GlacialLcdLog.Write($"u{Volatile.Read(ref _uploadSeq)} RX +{sinceStart.ElapsedMilliseconds}ms {step}: {hex}");
        if (_logAllReplies || changed)
        {
            Info($"[{HandlerId}] reply +{sinceStart.ElapsedMilliseconds} ms after {step}: {hex}");
        }
        return report;
    }

    /// <summary>Drops input left over from earlier traffic; a failing read just ends the drain.</summary>
    private void DrainInput(IHidDevice hid)
    {
        var buffer = new byte[GlacialLcdProtocol.ReportBytes];
        try
        {
            for (int i = 0; i < 64 && hid.Read(buffer, 0) > 0; i++)
            {
            }
        }
        catch (Exception)
        {
        }
    }

    // The three helpers below use the driver's current handles under the HID lock, so a
    // disconnect (which takes the lock, clears them, and only then lets the hub dispose them)
    // can never have the handle disposed under a read or write in flight.
    private bool HidWrite(WorkerState state, byte[] report)
    {
        lock (_hidLock)
        {
            var hid = _hid;
            return !state.Stop && hid is not null && hid.Write(report);
        }
    }

    private bool BulkWrite(WorkerState state, byte[] chunk)
    {
        lock (_hidLock)
        {
            var pipe = _pipe;
            return !state.Stop && pipe is not null && pipe.Write(chunk);
        }
    }

    private byte[]? Reply(WorkerState state, int timeoutMs, string step, string key, System.Diagnostics.Stopwatch sinceStart) =>
        Reply(state, timeoutMs, step, key, sinceStart, out _);

    private byte[]? Reply(WorkerState state, int timeoutMs, string step, string key, System.Diagnostics.Stopwatch sinceStart, out bool failed)
    {
        lock (_hidLock)
        {
            var hid = _hid;
            if (state.Stop || hid is null)
            {
                failed = false;
                return null;
            }
            return ReadReply(hid, timeoutMs, step, key, sinceStart, out failed);
        }
    }

    private bool ShouldStop(WorkerState state) => state.Stop || _link.CancelRequested || !CanUpload();

    /// <summary>Facts about one upload that the caller needs afterwards; per upload, so another worker cannot reset them.</summary>
    private sealed class UploadRun
    {
        public bool FlushSent { get; set; }
        public int Seq { get; init; }
        public string? Error { get; set; }

        /// <summary>Per step type: whether the upload still waits for the panel's replies; cleared when they stop coming.</summary>
        public bool Probe { get; set; }
        public bool PreludeOn { get; set; }
        public bool ChunkOn { get; set; }
        public bool CommitOn { get; set; }
        public int PreludeReplies { get; set; }
        public int ChunkReplies { get; set; }
        public int ChunkMisses { get; set; }
        public int CommitReplies { get; set; }
        public int CommitMisses { get; set; }
        public int AckReplies { get; set; }
        public int AckTimeouts { get; set; }
        public long AckWaitMs { get; set; }
        public long CommitWaitMs { get; set; }
        public int PreludeMisses { get; set; }
    }

    /// <summary>One chunk gap: the captured interval per protocol-size transfer, scaled by the pace and by the transfer size.</summary>
    private static int ChunkGapMs(int pace, int chunkBytes) =>
        (int)((long)GlacialLcdProtocol.ChunkIntervalMs * pace / 100 * chunkBytes / GlacialLcdProtocol.ChunkBytes);

    /// <summary>The longest the upload should take on its timers.</summary>
    internal static int EstimateUploadMs(int jpegBytes, GlacialLcdTuningValues cfg)
    {
        int chunkBytes = cfg.ChunkKb * 1024;
        long chunks = (jpegBytes + chunkBytes - 1) / chunkBytes;
        long fixedGaps = (long)(GlacialLcdProtocol.PreludeGapTotalMs + GlacialLcdProtocol.ChunkLeadMs + GlacialLcdProtocol.FinishSettleMs) * cfg.PacePercent / 100;
        // Both state queries may run their full retries, and each prelude step waits for its report.
        long waits = 2L * StallDcTries * DefaultStallReplyMs + PreludeSteps * ReplyWaitMs;
        return (int)Math.Min(int.MaxValue / 2, fixedGaps + chunks * ChunkGapMs(cfg.PacePercent, chunkBytes) + FinishReplyWaitMs + waits);
    }

    private void Tx(UploadRun run, System.Diagnostics.Stopwatch sinceStart, string step) =>
        GlacialLcdLog.Write($"u{run.Seq} TX +{sinceStart.ElapsedMilliseconds}ms {step}");

    /// <summary>Waits up to the timeout for any report from the panel; false when it stays silent or the read path fails.</summary>
    private bool AwaitAnyReply(WorkerState state, int totalMs, string step, System.Diagnostics.Stopwatch sinceStart)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (waited.ElapsedMilliseconds < totalMs)
        {
            if (state.Stop)
            {
                return true;
            }
            if (Reply(state, FinishReplySliceMs, step, step, sinceStart, out bool failed) is not null)
            {
                return true;
            }
            if (failed)
            {
                // A read path that errors cannot show silence, so it is not taken for a stall.
                return true;
            }
        }
        return false;
    }

    /// <summary>Waits for a reply to a state query already sent, asking again up to the try limit before giving up.</summary>
    private bool AwaitDc(WorkerState state, UploadRun run, System.Diagnostics.Stopwatch sinceStart, string step)
    {
        for (int attempt = 1; attempt <= StallDcTries; attempt++)
        {
            if (AwaitAnyReply(state, StallReplyMs, step, sinceStart))
            {
                return true;
            }
            GlacialLcdLog.Write($"u{run.Seq} no reply to {step} (try {attempt}/{StallDcTries})");
            if (attempt < StallDcTries)
            {
                if (!HidWrite(state, GlacialLcdProtocol.EncodeQuery()))
                {
                    return false;
                }
                Tx(run, sinceStart, step + " retry");
            }
        }
        return false;
    }

    /// <summary>
    /// Waits for the report <paramref name="match"/> accepts, up to the timeout, logging how long it took
    /// or that it ran out. Other reports that arrive meanwhile are logged and skipped. Never throws and never
    /// fails the upload: a miss just hands back to the caller's timer path.
    /// </summary>
    private bool AwaitAck(WorkerState state, UploadRun run, int timeoutMs, string step, System.Diagnostics.Stopwatch sinceStart, AckMatch match, out long waitedMs)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        bool got = false;
        while (!state.Stop)
        {
            long left = timeoutMs - waited.ElapsedMilliseconds;
            if (left <= 0)
            {
                break;
            }
            var report = Reply(state, (int)Math.Min(FinishReplySliceMs, left), step, step, sinceStart, out bool failed);
            if (report is not null && match(report))
            {
                got = true;
                break;
            }
            if (failed)
            {
                break;
            }
        }
        waitedMs = waited.ElapsedMilliseconds;
        if (state.Stop)
        {
            return got;
        }
        if (got)
        {
            run.AckReplies++;
            GlacialLcdLog.Write($"u{run.Seq} ACK {step} replied after {waitedMs}ms");
        }
        else
        {
            run.AckTimeouts++;
            GlacialLcdLog.Write($"u{run.Seq} ACK {step} TIMEOUT after {waitedMs}ms (limit {timeoutMs}ms), continuing on timers");
        }
        return got;
    }

    private delegate bool AckMatch(ReadOnlySpan<byte> report);

    private static int Silence(int silent, int replies, int misses) =>
        replies + misses == 0 ? silent : replies > 0 ? 0 : silent + 1;

    private void ResetAckSilence()
    {
        _silentPrelude = 0;
        _silentChunk = 0;
        _silentCommit = 0;
        _ackUploads = 0;
        _ackSkipLogged = false;
    }

    /// <summary>
    /// Decides per step type whether this upload waits on the panel's replies: the knob is on and that step has not just
    /// been silent through earlier uploads (it is asked again every few uploads, counted separately from the silence).
    /// </summary>
    private void ChooseAckSteps(GlacialLcdTuningValues cfg, UploadRun run)
    {
        if (cfg.ReplyDriven != _replyDrivenSeen)
        {
            // Turning the knob off and on starts the panel afresh.
            _replyDrivenSeen = cfg.ReplyDriven;
            ResetAckSilence();
        }
        if (!cfg.ReplyDriven)
        {
            return;
        }
        // Counted here, so an upload superseded before it sends anything does not move the probe.
        bool probe = ++_ackUploads % AckProbeEvery == 0;
        run.Probe = probe;
        run.PreludeOn = _silentPrelude == 0 || probe;
        run.ChunkOn = _silentChunk == 0 || probe;
        run.CommitOn = _silentCommit == 0 || probe;
        bool skipping = !(run.PreludeOn && run.ChunkOn && run.CommitOn);
        if (skipping != _ackSkipLogged)
        {
            _ackSkipLogged = skipping;
            GlacialLcdLog.Write(skipping
                ? $"u{run.Seq} replies not awaited for silent steps (prelude={!run.PreludeOn} chunk={!run.ChunkOn} commit={!run.CommitOn}); timers there, asked again every {AckProbeEvery} uploads"
                : $"u{run.Seq} replies awaited at every step again");
        }
    }

    /// <summary>
    /// Once the first command is sent the sequence runs to its end: a cancel, a dark display or a
    /// native edit waits for the last step. Only a disconnect or a silent panel ends it early.
    /// </summary>
    private UploadOutcome UploadJpeg(WorkerState state, byte[] jpeg, int targetSlot, UploadRun run, GlacialLcdTuningValues cfg)
    {
        UploadOutcome Fail(string why)
        {
            run.Error = why;
            return state.Stop ? UploadOutcome.Cancelled : UploadOutcome.Failed;
        }

        UploadOutcome Stall(string why)
        {
            run.Error = why;
            return state.Stop ? UploadOutcome.Cancelled : UploadOutcome.Stalled;
        }

        _logAllReplies = Interlocked.Increment(ref _uploadsSinceConnect) <= FullReplyLogUploads;
        lock (_hidLock)
        {
            if (_hid is { } current)
            {
                // Reports from earlier traffic must not be taken for answers to this upload's commands.
                DrainInput(current);
            }
        }
        // A newer settled frame replaces this one before anything is sent.
        if (_supersedes < MaxSupersedes && NewerFrameWaiting())
        {
            _supersedes++;
            return UploadOutcome.Superseded;
        }
        var sinceStart = System.Diagnostics.Stopwatch.StartNew();
        int chunkBytes = cfg.ChunkKb * 1024;
        ChooseAckSteps(cfg, run);

        if (!HidWrite(state, GlacialLcdProtocol.EncodeQuery()))
        {
            return Fail("write failed at DC");
        }
        Tx(run, sinceStart, "DC");
        if (!AwaitDc(state, run, sinceStart, "DC"))
        {
            return Stall("no reply to DC");
        }
        bool gapCovered = false;
        foreach (var (report, gapMs) in GlacialLcdProtocol.EncodeCustomImagePrelude(jpeg.Length, targetSlot))
        {
            // A reply (or the wait for one) already stood in for the timer gap that follows the step before.
            if (!gapCovered && Pause(gapMs))
            {
                return UploadOutcome.Cancelled;
            }
            gapCovered = false;
            if (report[1] == 0xF1)
            {
                run.FlushSent = true;
            }
            string step = "prelude " + report[1].ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
            if (!HidWrite(state, report))
            {
                return Fail("write failed at " + step);
            }
            Tx(run, sinceStart, step);
            AckMatch? ack = report[1] == 0x71 ? GlacialLcdProtocol.IsBeginAck : report[1] == 0x73 ? GlacialLcdProtocol.IsStageAck : null;
            if (run.PreludeOn && ack is not null)
            {
                bool busy = false;
                AckMatch matcher = report[1] == 0x73
                    ? r =>
                    {
                        if (GlacialLcdProtocol.IsStageBusy(r))
                        {
                            busy = true;
                            return true;
                        }
                        return GlacialLcdProtocol.IsStageAck(r);
                    }
                    : ack;
                bool replied;
                long preludeWaitMs;
                int busyTries = 0;
                while (true)
                {
                    busy = false;
                    replied = AwaitAck(state, run, GlacialLcdProtocol.AckPreludeTimeoutMs, step, sinceStart, matcher, out preludeWaitMs);
                    run.AckWaitMs += preludeWaitMs;
                    if (!(replied && busy))
                    {
                        break;
                    }
                    if (++busyTries > GlacialLcdProtocol.BusyRetries)
                    {
                        // Streaming chunks into a transfer the panel has not opened would only be rejected; leave write mode and try the whole upload again later.
                        GlacialLcdLog.Write($"u{run.Seq} panel still busy at {step} after {busyTries - 1} retries, upload abandoned");
                        if (cfg.CloseWriteMode)
                        {
                            HidWrite(state, GlacialLcdProtocol.EncodeCloseWriteMode());
                            Tx(run, sinceStart, "close write mode 72 (abandoned)");
                        }
                        return Fail($"panel busy at {step}");
                    }
                    GlacialLcdLog.Write($"u{run.Seq} panel busy at {step} (status reply), asking again in {GlacialLcdProtocol.BusyRetryMs}ms ({busyTries}/{GlacialLcdProtocol.BusyRetries})");
                    if (_hardAbort.Wait(GlacialLcdProtocol.BusyRetryMs))
                    {
                        return UploadOutcome.Cancelled;
                    }
                    if (!HidWrite(state, report))
                    {
                        return Fail("write failed at " + step + " retry");
                    }
                    Tx(run, sinceStart, step + " retry");
                }
                if (replied)
                {
                    run.PreludeReplies++;
                }
                else if (++run.PreludeMisses >= 2)
                {
                    // Silent through the whole prelude: the panel is taken to be silent for the rest of this upload too.
                    run.PreludeOn = false;
                    if (!run.Probe)
                    {
                        run.ChunkOn = false;
                        run.CommitOn = false;
                    }
                    GlacialLcdLog.Write($"u{run.Seq} no replies to the prelude, {(run.Probe ? "probing the later steps on their own" : "timers for the rest of the upload")}");
                }
                gapCovered = true;
            }
            else
            {
                Reply(state, ReplyWaitMs, step, step, sinceStart);
            }
        }
        if (Pause(GlacialLcdProtocol.ChunkLeadMs))
        {
            return UploadOutcome.Cancelled;
        }
        var chunk = new byte[chunkBytes];
        var chunkTimer = new System.Diagnostics.Stopwatch();
        int chunks = (jpeg.Length + chunkBytes - 1) / chunkBytes;
        int index = 0;
        for (int offset = 0; offset < jpeg.Length; offset += chunkBytes, index++)
        {
            // The last transfer is zero padded past the picture's end to the next protocol-size boundary, as the vendor's is.
            int take = Math.Min(chunkBytes, jpeg.Length - offset);
            int length = Math.Min(chunkBytes, (take + GlacialLcdProtocol.ChunkBytes - 1) / GlacialLcdProtocol.ChunkBytes * GlacialLcdProtocol.ChunkBytes);
            Array.Clear(chunk);
            jpeg.AsSpan(offset, take).CopyTo(chunk);
            chunkTimer.Restart();
            if (!BulkWrite(state, length == chunkBytes ? chunk : chunk.AsSpan(0, length).ToArray()))
            {
                return Fail($"bulk write failed at chunk {index}");
            }
            Tx(run, sinceStart, $"chunk {index + 1}/{chunks} {length}B");
            // Submit to submit: the gap counts from this write's start, so a slow write does not stretch it.
            int scaled = ChunkGapMs(_pace, chunkBytes);
            if (run.ChunkOn)
            {
                // One report per bulk transfer, whatever its size; the wait lasts the larger of the timer gap and the floor, so a silent panel costs no more than that per chunk.
                const int expected = 1;
                int limit = Math.Max(GlacialLcdProtocol.AckChunkMinTimeoutMs, scaled);
                bool all = true;
                var ackWatch = System.Diagnostics.Stopwatch.StartNew();
                for (int k = 0; k < expected && all; k++)
                {
                    all = AwaitAck(state, run, (int)Math.Max(1, limit - ackWatch.ElapsedMilliseconds), $"chunk {index + 1}/{chunks}", sinceStart, GlacialLcdProtocol.IsChunkAck, out _);
                }
                run.AckWaitMs += ackWatch.ElapsedMilliseconds;
                if (all)
                {
                    run.ChunkReplies++;
                }
                else
                {
                    // A late report for this chunk would otherwise release the next one early, so one miss ends reply waiting for the rest of the upload.
                    run.ChunkMisses++;
                    run.ChunkOn = false;
                    GlacialLcdLog.Write($"u{run.Seq} chunk reply missed, timers for the rest of the upload");
                }
            }
            else
            {
                // A poll, not a wait: a timed read costs a whole timer tick on Windows.
                Reply(state, 0, "chunk " + index, "chunk", sinceStart);
                int remaining = scaled - (int)chunkTimer.ElapsedMilliseconds;
                if (remaining > 0 && _hardAbort.Wait(remaining))
                {
                    return UploadOutcome.Cancelled;
                }
            }
        }
        if (!HidWrite(state, GlacialLcdProtocol.EncodeUploadEnd()))
        {
            return Fail("write failed at 73 FF");
        }
        Tx(run, sinceStart, "end 73FF");
        int settleMs = (int)((long)GlacialLcdProtocol.FinishSettleMs * _pace / 100);
        bool committed = false;
        if (run.CommitOn)
        {
            // The captured settle is the longest the panel is given to store the picture; its report ends the wait early.
            int limit = Math.Max(settleMs, GlacialLcdProtocol.FinishSettleMs);
            committed = AwaitAck(state, run, limit, "end 73FF", sinceStart, GlacialLcdProtocol.IsCommitAck, out long commitMs);
            run.CommitWaitMs = commitMs;
            if (committed)
            {
                run.CommitReplies++;
            }
            else
            {
                run.CommitMisses++;
                // The timer path's total is the floor: whatever of it the wait did not cover is still waited out.
                long remainder = FinishReplyWaitMs + settleMs - commitMs;
                if (!state.Stop && remainder > 0 && _hardAbort.Wait((int)remainder))
                {
                    return UploadOutcome.Cancelled;
                }
            }
        }
        if (!committed)
        {
            if (!run.CommitOn)
            {
                var finishWait = System.Diagnostics.Stopwatch.StartNew();
                while (finishWait.ElapsedMilliseconds < FinishReplyWaitMs && !state.Stop)
                {
                    if (Reply(state, FinishReplySliceMs, "end 73FF", "end", sinceStart, out bool readFailed) is not null || readFailed)
                    {
                        // A report arrived, or the read says the device is gone: either way, stop waiting.
                        break;
                    }
                }
                if (Pause(GlacialLcdProtocol.FinishSettleMs))
                {
                    return UploadOutcome.Cancelled;
                }
            }
        }
        if (state.Stop)
        {
            return UploadOutcome.Cancelled;
        }
        // The picture is stored; showing it is the app's last step.
        if (cfg.CloseWriteMode)
        {
            if (!HidWrite(state, GlacialLcdProtocol.EncodeCloseWriteMode()))
            {
                return Fail("write failed at 72 close");
            }
            Tx(run, sinceStart, "close write mode 72");
            Reply(state, ReplyWaitMs, "close write mode 72", "close", sinceStart);
        }
        if (!HidWrite(state, GlacialLcdProtocol.EncodeQuery()))
        {
            return Fail("write failed at DC before show");
        }
        Tx(run, sinceStart, "DC before show");
        if (!AwaitDc(state, run, sinceStart, "DC before show"))
        {
            return Stall("no reply to DC after the transfer");
        }
        if (!HidWrite(state, GlacialLcdProtocol.EncodeCustomPicture(targetSlot)))
        {
            return Fail("write failed at show 51");
        }
        Tx(run, sinceStart, "show 51");
        Reply(state, ReplyWaitMs, "show 51", "show", sinceStart);
        return UploadOutcome.Done;
    }

    /// <summary>
    /// The experimental raw transport: whole uncompressed frames instead of stored pictures. Enters the
    /// stream mode once, sends at most one frame per interval, and returns the panel to normal when the
    /// knob goes off or the Nexus panel ends. Never overlaps an upload: the worker runs one or the other.
    /// </summary>
    private void RawPass(WorkerState state, EncoderCache cache, GlacialLcdTuningValues cfg)
    {
        if (_rawActive && (!cfg.RawStream || !CanUpload()))
        {
            ExitRaw(state);
            return;
        }
        if (!cfg.RawStream)
        {
            return;
        }
        if (_tuning is { Stalled: true })
        {
            ProbeStalled(state);
            return;
        }
        if (!CanUpload())
        {
            return;
        }
        if (!_rawActive)
        {
            long now = _clock();
            lock (_stateLock)
            {
                if (now < _nextAllowedMs)
                {
                    return;
                }
            }
            if (!EnterRaw(state))
            {
                return;
            }
        }
        if (Ready())
        {
            SendRawFrame(state, cache, cfg);
        }
    }

    private bool EnterRaw(WorkerState state)
    {
        bool entryFailed = false;
        try
        {
            return EnterRawLocked(state, ref entryFailed);
        }
        finally
        {
            if (entryFailed)
            {
                // Outside the panel lock: a failure count at its limit writes settings.
                RawFailed("write failed at the frame stream mode select");
            }
        }
    }

    private bool EnterRawLocked(WorkerState state, ref bool entryFailed)
    {
        lock (_link.Sync)
        {
            if (state.Stop || !CanUpload())
            {
                return false;
            }
            lock (_hidLock)
            {
                if (_hid is { } hid)
                {
                    DrainInput(hid);
                }
            }
            // Owed before the write, so a shutdown that looks now cannot miss a panel that is about to be in stream mode.
            SetRawActive();
            if (!HidWrite(state, GlacialLcdProtocol.EncodeFrameStreamMode()))
            {
                ClearRawActive();
                entryFailed = !state.Stop;
                return false;
            }
            _link.ShowPending = false;
            GlacialLcdLog.Write("raw stream: entered frame stream mode (51 20)");
            // A reply to the mode select ends the settle early; the fixed wait is the 100 ms the reference raw-stream implementation sleeps after its mode switch (its frame stream entry).
            var sinceStart = System.Diagnostics.Stopwatch.StartNew();
            Reply(state, RawEnterSettleMs, "raw enter 51 20", "rawenter", sinceStart);
            lock (_hidLock)
            {
                if (_hid is { } hid)
                {
                    DrainInput(hid);
                }
            }
            if (state.Stop)
            {
                return false;
            }
        }
        MarkPending();
        return true;
    }

    /// <summary>
    /// Leaves the raw stream. When the knob alone went off the panel is put back to a wallpaper mode and
    /// the stored picture is shown again; when the Nexus panel ended, the native path that took over
    /// writes its own mode, so nothing is sent that could override it.
    /// </summary>
    private void ExitRaw(WorkerState state)
    {
        lock (_hidLock)
        {
            // A worker that outlived its disconnect leaves the newer worker's state alone.
            if (state.Stop || !ReferenceEquals(_current, state))
            {
                return;
            }
            _uploading = true;
            _idle.Reset();
        }
        try
        {
            lock (_link.Sync)
            {
                // A quiescing shutdown still puts the panel back; a Nexus panel that ended, a dark display, a sleep or a waiting native edit leave that to the path taking over.
                bool restore = !state.Stop && !_link.Suspended && _link.NexusPanelEnabled && _link.Streaming && _link.StreamWanted && !_link.Dark && !_link.NativePending;
                bool shown = false;
                if (restore)
                {
                    var sinceStart = System.Diagnostics.Stopwatch.StartNew();
                    var run = new UploadRun { Seq = Volatile.Read(ref _uploadSeq) };
                    HidWrite(state, GlacialLcdProtocol.EncodeQuery());
                    Tx(run, sinceStart, "raw exit DC");
                    Reply(state, ReplyWaitMs, "raw exit DC", "rawexit", sinceStart);
                    int slot;
                    bool stored;
                    lock (_stateLock)
                    {
                        stored = _lastHash is not null;
                        slot = _shownSlot;
                    }
                    // The stored picture comes straight back when there is one; otherwise the panel gets the first wallpaper, as the reference tool leaves it.
                    bool written = HidWrite(state, stored ? GlacialLcdProtocol.EncodeCustomPicture(slot) : GlacialLcdProtocol.EncodeMode(GlacialLcdProtocol.ModeStaticAsset, 0));
                    Tx(run, sinceStart, stored ? "raw exit show stored picture 51" : "raw exit wallpaper 51");
                    shown = stored && written;
                }
                ClearRawActive();
                _link.ShowPending = !shown;
                lock (_stateLock)
                {
                    _nextShowMs = 0;
                }
                GlacialLcdLog.Write($"raw stream: left frame stream mode ({(restore ? "panel restored" : "native path restores its mode")})");
            }
            MarkPending();
        }
        finally
        {
            FinishUploading(state);
        }
    }

    private void SendRawFrame(WorkerState state, EncoderCache cache, GlacialLcdTuningValues cfg)
    {
        int wrongSize = 0;
        lock (_frameLock)
        {
            if (_latest is null)
            {
                _pending = false;
                return;
            }
            if (_latest.Length != GlacialLcdProtocol.RawFrameBytes)
            {
                _pending = false;
                wrongSize = _latest.Length;
            }
            if (wrongSize == 0)
            {
                if (cache.Snapshot is null || cache.Snapshot.Length != _latest.Length)
                {
                    cache.Snapshot = new byte[_latest.Length];
                }
                _latest.CopyTo(cache.Snapshot, 0);
                _pending = false;
            }
        }
        if (wrongSize != 0)
        {
            RawFailed($"frame is {wrongSize} bytes, the panel takes {GlacialLcdProtocol.RawFrameBytes}", retry: false, transport: false);
            return;
        }
        lock (_hidLock)
        {
            if (state.Stop)
            {
                return;
            }
            if (_pipe is null || _hid is null)
            {
                MarkPending();
                return;
            }
            _uploading = true;
            _idle.Reset();
        }
        _abort.Reset();
        long start = _clock();
        _nextStartMs = start + Math.Max(RawMinFrameIntervalMs, (long)(1000.0 / cfg.MaxFps));
        int seq = Interlocked.Increment(ref _uploadSeq);
        Interlocked.Increment(ref _uploadAttempts);
        var run = new UploadRun { Seq = seq };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        bool ok = false;
        try
        {
            lock (_link.Sync)
            {
                ok = !ShouldStop(state) && RawFrame(state, cache.Snapshot!, run, watch);
            }
        }
        catch (Exception ex)
        {
            GlacialLcdLog.Write($"r{seq} exception: {ex}");
            run.Error ??= $"{ex.GetType().Name}: {ex.Message}";
        }
        FinishUploading(state);
        GlacialLcdLog.Write($"r{seq} end ok={ok} totalMs={watch.ElapsedMilliseconds}{(run.Error is null ? "" : " error=" + run.Error)}");
        if (state.Stop)
        {
            return;
        }
        if (ok)
        {
            _rawFailStreak = 0;
            lock (_stateLock)
            {
                _nextAllowedMs = 0;
            }
            Interlocked.Increment(ref _uploadsCompleted);
            _tuning?.RawFrame(true);
            _tuning?.UploadOk(watch.ElapsedMilliseconds, GlacialLcdProtocol.RawFrameBytes, 0);
            return;
        }
        if (run.Error is null)
        {
            // Cancelled by a mode change, not a fault.
            MarkPending();
            return;
        }
        RawFailed(run.Error, retry: true);
    }

    /// <summary>Counts one failed raw frame or entry: logs it, backs off, and after a few in a row switches the knob off so the normal path takes over.</summary>
    private void RawFailed(string error, bool retry = true, bool transport = true)
    {
        if (retry)
        {
            MarkPending();
        }
        if (transport)
        {
            _rawNeedsSync = true;
        }
        _tuning?.RawFrame(false);
        _tuning?.UploadFailed(error);
        int streak = ++_rawFailStreak;
        int delay;
        lock (_stateLock)
        {
            delay = transport ? Backoff(streak) : 0;
            _nextAllowedMs = _clock() + delay;
        }
        GlacialLcdLog.Write($"raw stream: failure {streak}/{RawFailuresBeforeOff}: {error}");
        Failed($"raw frame failed: {error}; retrying in {delay / 1000} s");
        if (streak >= RawFailuresBeforeOff && _tuning is not null)
        {
            GlacialLcdLog.Write($"raw stream: {streak} failures in a row, switching raw streaming off so the normal path takes over");
            // Applied by the worker loop outside any panel lock.
            _rawFailStreak = 0;
            _rawOffPending = true;
        }
    }

    private bool RawFrame(WorkerState state, byte[] frame, UploadRun run, System.Diagnostics.Stopwatch sinceStart)
    {
        lock (_hidLock)
        {
            if (_hid is { } hid)
            {
                DrainInput(hid);
            }
        }
        if (_rawNeedsSync)
        {
            // After a failure the panel may still hold half a frame; a state query that is answered shows it is listening again.
            if (!HidWrite(state, GlacialLcdProtocol.EncodeQuery()))
            {
                run.Error = state.Stop ? null : "write failed at raw resync DC";
                return false;
            }
            if (!AwaitAnyReply(state, StallReplyMs, "raw resync DC", sinceStart))
            {
                run.Error = "no reply to the state query after a failed frame";
                return false;
            }
            _rawNeedsSync = false;
        }
        bool accepted = false;
        for (int attempt = 0; attempt <= GlacialLcdProtocol.BusyRetries && !accepted; attempt++)
        {
            if (!HidWrite(state, GlacialLcdProtocol.EncodeFrameAnnounce(frame.Length)))
            {
                run.Error = state.Stop ? null : "write failed at raw announce 7F";
                return false;
            }
            GlacialLcdLog.Write($"r{run.Seq} TX +{sinceStart.ElapsedMilliseconds}ms raw announce 7F 03 {frame.Length}B");
            var waited = System.Diagnostics.Stopwatch.StartNew();
            bool rejected = false;
            while (!state.Stop && waited.ElapsedMilliseconds < GlacialLcdProtocol.AckFrameAnnounceTimeoutMs)
            {
                int slice = (int)Math.Min(FinishReplySliceMs, GlacialLcdProtocol.AckFrameAnnounceTimeoutMs - waited.ElapsedMilliseconds);
                var report = Reply(state, Math.Max(1, slice), "raw announce", "rawannounce", sinceStart, out bool failed);
                if (report is not null && GlacialLcdProtocol.IsFrameAnnounceAck(report))
                {
                    accepted = true;
                    break;
                }
                if (report is not null && GlacialLcdProtocol.IsAnnounceRejected(report))
                {
                    rejected = true;
                    break;
                }
                if (failed)
                {
                    break;
                }
            }
            if (state.Stop)
            {
                return false;
            }
            if (accepted)
            {
                GlacialLcdLog.Write($"r{run.Seq} ACK raw announce replied after {waited.ElapsedMilliseconds}ms");
                break;
            }
            if (!rejected)
            {
                GlacialLcdLog.Write($"r{run.Seq} ACK raw announce TIMEOUT after {waited.ElapsedMilliseconds}ms, frame skipped");
                run.Error = "no reply to the raw frame announce";
                return false;
            }
            // Refused right after the mode select (or while the panel is still busy): not ready yet, so ask again shortly before counting a failure.
            GlacialLcdLog.Write($"r{run.Seq} raw announce refused (not ready), asking again in {GlacialLcdProtocol.BusyRetryMs}ms ({attempt + 1}/{GlacialLcdProtocol.BusyRetries})");
            if (_hardAbort.Wait(GlacialLcdProtocol.BusyRetryMs))
            {
                return false;
            }
        }
        if (!accepted)
        {
            run.Error = "the panel kept refusing the raw frame announce";
            return false;
        }
        if (!BulkWrite(state, frame))
        {
            run.Error = state.Stop ? null : "bulk write failed for the raw frame";
            return false;
        }
        GlacialLcdLog.Write($"r{run.Seq} TX +{sinceStart.ElapsedMilliseconds}ms raw frame {frame.Length}B");
        return true;
    }

    /// <summary>The panel holds its picture, so there is nothing to resend.</summary>
    public bool Resend(IBulkUsbPipe pipe, IHidDevice? hid) => true;

    public void Disconnect(IBulkUsbPipe pipe, IHidDevice? hid)
    {
        GlacialLcdLog.Write("stream driver disconnect");
        WorkerState? state;
        lock (_hidLock)
        {
            state = _current;
            _current = null;
            _pipe = null;
            _hid = null;
            _uploading = false;
            _idle.Set();
            // Set with the handles cleared, so a write the worker has in flight is a cancel, not a failure.
            state?.Stop = true;
        }
        if (state is not null)
        {
            // The worker stops on its own; a short join keeps the hub's lock from waiting out a pacing gap.
            _abort.Set();
            _hardAbort.Set();
            _wake.Set();
            state.Thread?.Join(JoinOnDisconnectMs);
        }
        ClearRawActive();
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
            string? value;
            lock (_stateLock)
            {
                value = _lastHash is not null ? RememberedValue() : null;
                if (value is null || value == _persistedValue)
                {
                    return;
                }
                _persistedValue = value;
            }
            _memory?.Save(_memoryKey, value);
        }
    }

    /// <summary>The picture on display as stored: its slot, a colon, its hash. Called with the state lock held.</summary>
    private string RememberedValue() => _shownSlot.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + _lastHash;

    /// <summary>Splits a stored value into slot and hash; a bare hash is from a build that always wrote the default slot.</summary>
    private static (int Slot, string Hash)? DecodeRemembered(string value)
    {
        int colon = value.IndexOf(':', StringComparison.Ordinal);
        int slot = DefaultShownSlot;
        string hash = value;
        if (colon >= 0)
        {
            if (colon == 0
                || !int.TryParse(value.AsSpan(0, colon), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out slot))
            {
                return null;
            }
            hash = value[(colon + 1)..];
        }
        // A value this build could not have written means nothing is remembered.
        return hash.Length == 0 || Array.IndexOf(GlacialLcdProtocol.CustomSlots, slot) < 0 ? null : (slot, hash);
    }

    /// <param name="onlyIfGeneration">When given, nothing is forgotten unless the remembered picture is still at that generation.</param>
    private void ForgetPicture(int? onlyIfGeneration)
    {
        lock (_persistLock)
        {
            bool stored;
            lock (_stateLock)
            {
                if (onlyIfGeneration is int expected && _pictureGeneration != expected)
                {
                    return;
                }
                _lastHash = null;
                _pictureGeneration++;
                stored = _persistedValue is not null;
                _persistedValue = null;
            }
            if (stored)
            {
                _memory?.Clear(_memoryKey);
            }
        }
    }

    public void PersistState() => PersistNow();

    /// <summary>Drops the remembered picture, so the next start uploads afresh.</summary>
    public void ForgetRemembered() => ForgetPicture(null);

    /// <summary>Stops new uploads; one in flight runs on. Called first on shutdown and before a detach.</summary>
    public void BeginShutdown()
    {
        _quiescing = true;
        _wake.Set();
    }

    /// <summary>Waits for an upload in flight to reach its last step; false when it did not within the time.</summary>
    public bool WaitIdle(TimeSpan timeout)
    {
        if (_idle.IsSet && _rawExitDone.IsSet)
        {
            return true;
        }
        GlacialLcdLog.Write($"waiting up to {(int)timeout.TotalMilliseconds}ms for the upload in flight");
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        bool finished = _idle.Wait(timeout) && _rawExitDone.Wait(TimeSpan.FromMilliseconds(Math.Max(0, timeout.TotalMilliseconds - deadline.ElapsedMilliseconds)));
        GlacialLcdLog.Write(finished ? "upload in flight finished" : "upload in flight did not finish in time");
        return finished;
    }

    public void PrepareDetach()
    {
        BeginShutdown();
        if (_disconnectWaitMs <= 0)
        {
            return;
        }
        int estimate = Volatile.Read(ref _inflightEstimateMs);
        WaitIdle(TimeSpan.FromMilliseconds(Math.Min(_disconnectWaitMs, estimate + estimate / 2 + EstimateOverheadMs)));
    }
}
