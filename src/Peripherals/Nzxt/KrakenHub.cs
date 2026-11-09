using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.PixelFormats;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// Owns the Kraken's HID control channel and its WinUSB bulk pipe, and serializes every
/// exchange. HID I/O happens under <c>_lock</c>; the LCD's bulk pixel writes run outside it
/// under <c>_lcdTransferLock</c>, since they use a different endpoint. Callers outside the
/// worker thread read <see cref="Snapshot"/> without blocking.
/// </summary>
public sealed class KrakenHub : IDisposable
{
    private const int ConnectReadTimeoutMs = 1000;
    private const int CommandReadTimeoutMs = 700;
    private const int StreamRetryDelayMs = 1000;
    private const int StreamFailuresBeforeRetryDelay = 3;
    // A direct-path reply arrives within a few ms; a missing one is not an error.
    private const int DirectReplyTimeoutMs = 150;
    private const int PollReadTimeoutMs = 250;

    // 2023 Kraken while an LCD stream is open (KrakenModel.CamSessionInit): the vendor app
    // sends no status polls, the cooler reports on its own, and a keepalive goes out
    // periodically. A poll is the fallback once no report has been read for the fallback window.
    private const long CamKeepAliveMs = 28_000;
    private const long CamStatusFallbackMs = 3_000;
    private const long CamStatusStaleMs = 10_000;
    private const long CamFallbackLogMs = 60_000;
    private const long CamDiagMs = 30_000;
    private const int CamBacklightDelayMs = 70;
    // A frame on this model waits for the cooler's reply to its start command before the bulk. The
    // wait is a small multiple of the vendor app's start-to-bulk gap, and a miss only moves on. The
    // cooler never answers the end command, so the frame does not wait for one.
    private const int CamStartReplyMs = 5;
    /// <summary>How long a non-frame write waits for an open frame; a frame is well inside the default.</summary>
    internal int CamWriteWaitMs { get; set; } = 40;

    // Bulk pixel data goes out in chunks; 64 KiB measured ~13 MB/s on the bench unit.
    private const int BulkChunkBytes = 64 * 1024;
    // Direct-path transfer size; divides a 640x640 BGR888 frame evenly.
    private const int DirectChunkBytes = 245_760;

    private readonly object _lock = new();
    // Serialises whole LCD transfers with each other. The stream path holds this across a
    // frame while taking _lock only for the short HID steps, so the pixels do not block a
    // reader of the snapshot: pixels ride the bulk endpoint, commands ride HID. On a 2023 Kraken
    // the HID writes that are not part of the frame wait for it to close (AwaitFrameClosedLocked).
    private readonly object _lcdTransferLock = new();
    // True from a 2023 Kraken frame's start command to its end command; guarded by _lock.
    private bool _camFrameOpen;
    private int _camWritersWaiting;
    private readonly IKrakenLcdTransportFactory? _lcdFactory;
    private IHidDevice? _device;
    private IKrakenLcdTransport? _lcd;
    private bool _disposed;

    // Which Kraken is attached, and the HID report size its descriptor declares. The line
    // spans 64-byte and 512-byte reports, and Windows rejects a write that is not exactly
    // the declared length, so every encoded report is truncated to this on the way out.
    private KrakenModel _model = KrakenModel.All[0];
    // Mirrors _device.Serial and _model for readers that must not wait on the I/O lock.
    private volatile string? _serial;
    private volatile KrakenModel _modelRead = KrakenModel.All[0];
    private int _reportLength = KrakenProtocol.ReportLength;

    // Panel geometry. Seeded from the model table and then overwritten by whatever the
    // device reports for itself, which is authoritative.
    private int _lcdWidth;
    private int _lcdHeight;

    // 0x72 tuple selection. Only the 2023 Kraken and Kraken Elite ever moved theirs.
    private bool _useNewSpeedChannels = true;
    // KrakenModel.DirectStream confirmed by firmware at Connect: the capture behind it is 2.x.
    private bool _directStream;

    private volatile bool _isConnected;
    private volatile KrakenSnapshot _snapshot = KrakenSnapshot.Empty;
    // Last image pushed, stored unrotated so a rotation change can re-render it.
    private byte[]? _lastLcdFrame;
    private int _lastNakKey;
    // Last GIF pushed, panel-sized and unrotated; rotating it takes ffmpeg, so the caller does.
    private byte[]? _lastLcdGif;

    // Streaming state. The buckets are allocated once, then rotated: the panel rejects
    // a transfer into the bucket it is currently displaying (code 9), so a stream has
    // to write an idle one and switch to it.
    private bool _streamReady;
    private long _lastStatusMs;
    private long _lastKeepAliveMs;
    private long _fallbackLoggedMs;
    private long _diagAtMs;
    private int _statusReads;
    private int _framesWritten;
    private int _framesSkipped;
    private int _statusFallbacks;
    private int _streamActiveBucket = -1;
    private long _streamRetryAtMs;
    private int _streamFailures;

    // Three, not two. Ping-pong rewrites the bucket that just left the screen on the
    // very next frame while the panel is still reading it, which shows as a band of
    // decode garbage across the bottom rows. With three, a bucket sits out a full
    // frame before it is reused.
    private const int StreamBucketCount = 3;

    // Encode scratch, allocated once: a stream runs at panel rate and a per-frame buffer
    // this size would be pure garbage.
    private byte[]? _lcdScratch;

    public KrakenHub(IKrakenLcdTransportFactory? lcdFactory = null)
    {
        _lcdFactory = lcdFactory;
    }

    // Session lock. The backlight lives on the cooler, so the user's value is held here
    // while it is dark and never written into the snapshot or the settings. Guarded by _lock.
    private const string LockKey = "lcd";
    private LcdLockState? _lockState;
    private LcdLockSleepSet? _lockSleep;
    private Func<bool>? _lockSetting;
    private Func<int?>? _loadRestore;
    private Action<int?>? _saveRestore;
    private bool _lockDimmed;
    private bool _lockRestorePending;
    private int _lockSavedBrightness;
    // Changes made while dark are stored and applied on unlock, ahead of the backlight, so
    // nothing here can put an image on a glass the lock is holding at zero.
    private bool _lockPendingReupload;
    // The lock fade in flight. Sampled by the runner; the level it returns to is _lockSavedBrightness,
    // fixed before the first step, and no step ever reaches the snapshot or the settings.
    private LcdFade? _fade;
    private int _fadeLastSent = -1;
    private LcdFadeRunner? _fadeRunner;

    /// <summary>Millisecond clock the fade and the Z3 stream retry read; tests replace it.</summary>
    internal Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>Pause between the dark step and the real backlight at session open; tests replace it.</summary>
    internal Action<int> DelayMs { get; set; } = Thread.Sleep;

    /// <summary>
    /// False once the last status report is too old to steer fans by. Always true off the
    /// 2023 Kraken, whose per-second poll keeps the value fresh or fails the connection.
    /// </summary>
    public bool StatusFresh
    {
        get
        {
            if (!_modelRead.CamSessionInit)
            {
                return true;
            }
            var at = Volatile.Read(ref _lastStatusMs);
            return at != 0 && Clock() - at < CamStatusStaleMs;
        }
    }

    internal LcdFadeRunner FadeRunner => _fadeRunner ??= new LcdFadeRunner(StepFade, onFault: FaultFade);
    private KrakenDisplayMode? _lockPendingMode;

    /// <summary>True while the lock holds the backlight at zero. Streamed frames are dropped meanwhile.</summary>
    public bool LockDimmed
    {
        get { lock (_lock) { return _lockDimmed; } }
    }

    /// <summary>
    /// Wires the session lock. <paramref name="sleepWhenLocked"/> is read once per lock.
    /// The restore pair persists the pre-lock level in its own field, so a restart while dark
    /// still brings the glass back; it is never the user's brightness.
    /// </summary>
    public void BindLockSleep(LcdLockState state, Func<bool> sleepWhenLocked, Func<int?>? loadRestore = null, Action<int?>? saveRestore = null)
    {
        _lockState = state;
        _lockSleep = new LcdLockSleepSet(state);
        _lockSetting = sleepWhenLocked;
        _loadRestore = loadRestore;
        _saveRestore = saveRestore;
        state.Changed += OnLockChanged;
    }

    internal void OnLockChanged(bool locked)
    {
        if (_lockSleep is null || _lockSetting is null)
        {
            return;
        }
        if (locked)
        {
            EnterLockSleep();
        }
        else
        {
            _lockSleep.Drain();
            ExitLockSleep();
        }
    }

    // Same lock order as SetLcdBacklight: transfer lock first, so a rotation re-upload in
    // flight finishes before the backlight moves. The decision is taken under both locks:
    // a handler that waited behind a bucket upload while the PC was unlocked meanwhile sees
    // the unlock here and does nothing.
    /// <summary>Test seam: runs where a handler that already knows it is locked waits for the device locks.</summary>
    internal Action? TestHookBeforeLockEnter { get; set; }

    private void EnterLockSleep()
    {
        TestHookBeforeLockEnter?.Invoke();
        lock (_lcdTransferLock)
        lock (_lock)
        {
            if (_lockSleep is null || _lockSetting is null || !_lockSleep.IsSleeping(LockKey, _lockSetting))
            {
                return;
            }
            // Not connected yet: Connect applies the lock itself, from the same decision.
            if (_lockDimmed || !_isConnected || _device == null || !_model.HasLcd)
            {
                return;
            }
            // The snapshot always holds the user's level, never a fade step, so this is the level to
            // come back to even when an unlock's fade up is caught half way.
            _lockSavedBrightness = _snapshot.LcdBrightness;
            _lockDimmed = true;
            _saveRestore?.Invoke(_lockSavedBrightness);
            var from = _fade is { Down: false } rising ? rising.LevelAt(Clock()) : Scaled(_lockSavedBrightness);
            StartFadeLocked(from, 0, SleepBlackoutCoordinator.LockFadeDuration);
            ServiceLog.Info($"[lcd-lock] kraken LCD sleeping (backlight {_lockSavedBrightness}% kept)");
        }
    }

    // The brightness schedule. The snapshot, the lock memory and the stored slider stay the user's
    // level; only what goes to the cooler is scaled.
    private Func<int, int>? _scale;
    private Func<int?>? _loadSlider;
    private Action<int>? _saveSlider;
    private Func<bool>? _follows;

    /// <summary>Wires the brightness schedule: the scale to the cooler's level, where the user's own slider is remembered (the cooler reads back only the scaled value), and whether the cooler follows the schedule.</summary>
    public void BindSchedule(Func<int, int> scale, Func<int?> loadSlider, Action<int> saveSlider, Func<bool>? follows = null)
    {
        _scale = scale;
        _loadSlider = loadSlider;
        _saveSlider = saveSlider;
        _follows = follows;
    }

    /// <summary>The user's slider as remembered by the schedule binding, or null.</summary>
    public int? RememberedSlider => _loadSlider?.Invoke();

    /// <summary>Records the user's slider without touching the cooler.</summary>
    public void RememberSlider(int percent) => _saveSlider?.Invoke(Math.Clamp(percent, 0, 100));

    private int Scaled(int slider) => _scale?.Invoke(slider) ?? slider;

    /// <summary>Puts the current scaled level on the glass. Left alone while the lock holds it dark or a fade is running; those end on the scaled level themselves.</summary>
    public bool ApplySchedule()
    {
        lock (_lcdTransferLock)
        lock (_lock)
        {
            if (!_isConnected || _device == null || !_model.HasLcd || _lockDimmed || _lockRestorePending || _fade is not null)
            {
                return false;
            }
            return WriteLocked(BacklightReport(Scaled(_snapshot.LcdBrightness), _snapshot.LcdOrientationQuarterTurns));
        }
    }

    private byte[] BacklightReport(int brightnessPercent, int orientationQuarterTurns) =>
        KrakenProtocol.EncodeSetBacklight(brightnessPercent, orientationQuarterTurns, _model.CamSessionInit);

    private void ExitLockSleep()
    {
        lock (_lcdTransferLock)
        lock (_lock)
        {
            if (!_lockDimmed)
            {
                return;
            }
            _lockDimmed = false;
            if (_isConnected && _device != null)
            {
                if (_model.Rgb565DirectStream)
                {
                    // The panel may have dropped the live session while dark; frames re-enter first.
                    _streamReady = false;
                }
                ApplyPendingWhileDarkLocked();
                // Up from 0, or from the step a fade down was caught on.
                var from = _fade is { Down: true } falling ? falling.LevelAt(Clock()) : 0;
                var target = Scaled(_lockSavedBrightness);
                if (from == target)
                {
                    _fade = null;
                    FinishRestoreLocked(WriteLocked(BacklightReport(target, _snapshot.LcdOrientationQuarterTurns)));
                }
                else
                {
                    StartFadeLocked(from, target, SleepBlackoutCoordinator.UnlockFadeDuration);
                }
            }
            else
            {
                // The cooler stays dark and its read-back will say zero; Connect restores the real value.
                _lockRestorePending = true;
            }
        }
    }

    // Caller holds _lock. Cleared only once the write lands; Poll and the next Connect retry it.
    private void FinishRestoreLocked(bool wrote)
    {
        if (wrote)
        {
            _saveRestore?.Invoke(null);
            ServiceLog.Info($"[lcd-lock] kraken LCD waking to {_lockSavedBrightness}%");
        }
        else
        {
            _lockRestorePending = true;
        }
    }

    // Caller holds _lock. A ramp that starts and ends at the same level is just the write.
    private void StartFadeLocked(int from, int to, TimeSpan duration)
    {
        var turns = _snapshot.LcdOrientationQuarterTurns;
        if (from == to)
        {
            _fade = null;
            var wrote = WriteLocked(BacklightReport(to, turns));
            if (to > 0)
            {
                FinishRestoreLocked(wrote);
            }
            return;
        }
        _fade = new LcdFade(from, to, duration, Clock());
        // The cooler already shows the level the ramp starts at (a fade up starts from 0, which
        // the dark write or the fade down left there).
        _fadeLastSent = from;
        FadeRunner.Kick();
    }

    /// <summary>A step threw: the ramp is dropped and its end state is applied (dark for a fade down, the level it was returning to for a fade up), so the glass is not left on a half step.</summary>
    internal void FaultFade()
    {
        lock (_lock)
        {
            AwaitFrameClosedLocked();
            if (_fade is not { } fade)
            {
                return;
            }
            _fade = null;
            try
            {
                var wrote = WriteLocked(BacklightReport(fade.To, _snapshot.LcdOrientationQuarterTurns));
                if (!fade.Down)
                {
                    FinishRestoreLocked(wrote);
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[nzxt-kraken] settling after a failed fade step: {ex.GetType().Name}: {ex.Message}");
                if (!fade.Down)
                {
                    // The poll retries the restore.
                    _lockRestorePending = true;
                }
            }
        }
    }

    /// <summary>One sample of the fade: writes the stepped level when it changed, finishes the ramp when it lands.</summary>
    internal bool StepFade()
    {
        lock (_lock)
        {
            AwaitFrameClosedLocked();
            if (_fade is not { } fade || !_isConnected || _device == null)
            {
                _fade = null;
                return false;
            }
            var now = Clock();
            var level = fade.LevelAt(now);
            var done = fade.IsDoneAt(now);
            if (!done && level == _fadeLastSent)
            {
                return true;
            }
            var wrote = WriteLocked(BacklightReport(level, _snapshot.LcdOrientationQuarterTurns));
            if (wrote)
            {
                _fadeLastSent = level;
            }
            if (!done)
            {
                return true;
            }
            _fade = null;
            if (!fade.Down)
            {
                FinishRestoreLocked(wrote);
            }
            return false;
        }
    }

    // A restoring backlight write that failed at unlock is retried on every poll until it lands.
    private void RetryRestoreLocked()
    {
        if (!_lockRestorePending || _lockDimmed || !_isConnected || !_model.HasLcd)
        {
            return;
        }
        if (WriteLocked(BacklightReport(Scaled(_lockSavedBrightness), _snapshot.LcdOrientationQuarterTurns)))
        {
            _lockRestorePending = false;
            _saveRestore?.Invoke(null);
            ServiceLog.Info($"[lcd-lock] kraken LCD restored to {_lockSavedBrightness}% on retry");
        }
    }

    // Image and mode changes that arrived while the glass was held dark.
    private void ApplyPendingWhileDarkLocked()
    {
        var reupload = _lockPendingReupload;
        var mode = _lockPendingMode;
        _lockPendingReupload = false;
        _lockPendingMode = null;
        if (_model.Rgb565DirectStream)
        {
            if (mode is { } held && held != KrakenDisplayMode.Bucket)
            {
                ClearPlusHold();
                _streamReady = false;
                WriteLocked(KrakenProtocol.EncodeSetDisplayMode(held, 0));
                _snapshot = _snapshot.WithDisplayMode(held);
            }
            else if ((reupload || mode == KrakenDisplayMode.Bucket) && _lastLcdFrame != null)
            {
                MarkPlusStillDirty();
            }
            return;
        }
        if (reupload && _lastLcdFrame != null)
        {
            UploadLcdFrameLocked(_lastLcdFrame, _snapshot.LcdOrientationQuarterTurns);
        }
        else if (mode is { } wanted)
        {
            var reply = ExchangeLocked(
                KrakenProtocol.EncodeSetDisplayMode(wanted, 0), 0x39, 0x01, CommandReadTimeoutMs);
            if (reply != null && KrakenProtocol.IsAck(reply))
            {
                _snapshot = _snapshot.WithDisplayMode(wanted);
            }
        }
    }

    public const string DeviceId = "nzxt-kraken";
    public const string ProductName = "NZXT Kraken";

    /// <summary>Lighting zone ids. Kept distinct from the cooling channel ids on the same device.</summary>
    public const string RingZoneId = "nzxt-kraken:led-ring";
    public const string FansZoneId = "nzxt-kraken:led-fans";

    public bool IsConnected => _isConnected;

    private volatile string? _claimedHidPath;
    private volatile KrakenModel? _claimedModel;

    /// <summary>hidraw node of the interface the worker selected; held across detaches until the cooler is gone or control is off.</summary>
    public string? ClaimedHidPath => _claimedHidPath;

    /// <summary>Chip name the in-kernel driver registers for the claimed model; null when nothing is claimed or the kernel has no driver for it.</summary>
    public string? ClaimedHwmonName => _claimedModel is { } m ? KrakenModel.KernelHwmonName(m.ProductId) : null;

    public void Claim(string hidPath, KrakenModel model)
    {
        _claimedHidPath = hidPath;
        _claimedModel = model;
    }

    public void ReleaseClaim()
    {
        _claimedHidPath = null;
        _claimedModel = null;
    }

    /// <summary>The attached model, or the Elite V2 row while nothing is attached.</summary>
    public KrakenModel Model
    {
        get => _modelRead;
    }

    /// <summary>Marketing name of the attached model, for UI and logs.</summary>
    public string ModelName
    {
        get => _modelRead.Name;
    }

    /// <summary>Panel width in pixels; 0 on a model with no LCD.</summary>
    public int LcdWidth
    {
        get { lock (_lock) { return _lcdWidth; } }
    }

    public int LcdHeight
    {
        get { lock (_lock) { return _lcdHeight; } }
    }

    /// <summary>True on the 2023 Kraken (0x300E), whose LCD wire runs on the vendor app's session and cadence.</summary>
    public bool IsCamSessionModel => _modelRead.CamSessionInit;

    /// <summary>Bytes in one uncompressed RGBA frame at the attached panel's size.</summary>
    public int LcdFrameBytes
    {
        get { lock (_lock) { return _lcdWidth * _lcdHeight * 4; } }
    }

    /// <summary>USB serial of the attached cooler, or null when nothing is attached.</summary>
    public string? Serial => _serial;

    /// <summary>Read this reference once, then use it for all field accesses.</summary>
    public KrakenSnapshot Snapshot => _snapshot;

    /// <summary>Zone id for a channel index, matching the order channels are reported in.</summary>
    public static string ZoneIdForChannelIndex(int channelIndex) =>
        channelIndex == 0 ? RingZoneId : FansZoneId;

    /// <summary>True when the bulk pipe opened, i.e. LCD image upload is available.</summary>
    public bool HasLcd
    {
        get
        {
            lock (_lock)
            {
                return _lcd != null;
            }
        }
    }

    public void Attach(IHidDevice device, KrakenModel model, int reportLength)
    {
        lock (_lock)
        {
            _device?.Dispose();
            _device = device;
            _serial = device.Serial;
            _model = model;
            _modelRead = model;
            _reportLength = reportLength > 0 ? reportLength : KrakenProtocol.ReportLength;
            _lcdWidth = model.LcdWidth;
            _lcdHeight = model.LcdHeight;
            _useNewSpeedChannels = !model.SpeedChannelsFollowFirmware;
            _directStream = false;
        }
    }

    /// <summary>
    /// Writes one encoded report, cut to the length the attached device declares. Encoders
    /// build at <see cref="KrakenProtocol.ReportLength"/> and every command's payload fits
    /// well inside 64 bytes, so the tail dropped here is always padding.
    /// </summary>
    private bool WriteLocked(byte[] report)
    {
        if (_camFrameOpen)
        {
            // Every entry point that writes waits for the frame first (AwaitFrameClosedLocked); this
            // counts the writes that got out anyway, which are only ones past the wait bound.
            Interlocked.Increment(ref _nonFrameWritesDuringFrame);
        }
        return WriteFrameLocked(report);
    }

    private int _nonFrameWritesDuringFrame;

    /// <summary>Tests read how many non-frame writes went out between a frame's start and end.</summary>
    internal int NonFrameWritesDuringFrame => Volatile.Read(ref _nonFrameWritesDuringFrame);

    // The writes of a 2023 Kraken frame itself, which must not wait for the frame they belong to.
    private bool WriteFrameLocked(byte[] report)
    {
        if (_device == null)
        {
            return false;
        }
        return _device.Write(report.Length <= _reportLength
            ? report.AsSpan()
            : report.AsSpan(0, _reportLength));
    }

    // On a 2023 Kraken every HID write that is not part of a frame goes out between frames, never
    // between a frame's start and end commands, as the vendor app does. A public entry point that
    // writes and takes only _lock calls this once, first thing after taking it and before reading
    // any state, because the wait releases _lock so the frame can finish and state may change
    // meanwhile. Entry points that take _lcdTransferLock first need no call, as the sender holds it
    // across the frame. If the frame outlasts the bound the write goes out anyway, since a
    // keepalive or fan write is worth more than one frame. Caller holds _lock.
    private void AwaitFrameClosedLocked()
    {
        if (!_camFrameOpen)
        {
            return;
        }
        long deadline = Stopwatch.GetTimestamp() + CamWriteWaitMs * Stopwatch.Frequency / 1000;
        _camWritersWaiting++;
        try
        {
            while (_camFrameOpen)
            {
                long left = deadline - Stopwatch.GetTimestamp();
                if (left <= 0)
                {
                    return;
                }
                Monitor.Wait(_lock, (int)Math.Max(1, left * 1000 / Stopwatch.Frequency));
            }
        }
        finally
        {
            _camWritersWaiting--;
            Monitor.PulseAll(_lock);
        }
    }

    // Monitor is not fair: a sender that re-enters at once after a frame would keep winning _lock
    // over the writers its frame close just woke. It steps aside until they have written, bounded
    // like their wait. Caller holds _lock.
    private void YieldToWritersLocked()
    {
        if (_camWritersWaiting == 0)
        {
            return;
        }
        long deadline = Stopwatch.GetTimestamp() + CamWriteWaitMs * Stopwatch.Frequency / 1000;
        while (_camWritersWaiting > 0)
        {
            long left = deadline - Stopwatch.GetTimestamp();
            if (left <= 0)
            {
                return;
            }
            Monitor.Wait(_lock, (int)Math.Max(1, left * 1000 / Stopwatch.Frequency));
        }
    }

    internal int CamWritersWaiting
    {
        get { lock (_lock) { return _camWritersWaiting; } }
    }

    /// <summary>
    /// Reads the static device facts and marks the hub connected. Called from the worker
    /// thread only.
    /// </summary>
    public bool Connect()
    {
        var connected = ConnectCore();
        if (connected)
        {
            lock (_camLock)
            {
                _camClosed = false;
            }
        }
        return connected;
    }

    private bool ConnectCore()
    {
        lock (_lock)
        {
            AwaitFrameClosedLocked();
            if (_device == null)
            {
                return false;
            }

            _lastNakKey = 0;
            var fwReply = ExchangeLocked(KrakenProtocol.EncodeFirmwareRequest(), 0x11, 0x01, ConnectReadTimeoutMs);
            if (fwReply == null)
            {
                return false;
            }
            var fw = KrakenProtocol.DecodeFirmware(fwReply);
            if (_model.SpeedChannelsFollowFirmware)
            {
                _useNewSpeedChannels = KrakenProtocol.UsesNewSpeedChannels(fw);
            }
            _directStream = _model.DirectStream && fw is { Major: >= 2 };

            // Starts the cooler's telemetry stream. Without it the accessory table is not
            // populated yet and the lighting query answers with zero channels.
            WriteLocked(KrakenProtocol.EncodeSetUpdateInterval(_model.TelemetryInterval));
            WriteLocked(KrakenProtocol.EncodeStartReporting());

            var channels = _model.Lighting == KrakenLightingProtocol.None
                ? Array.Empty<KrakenLightingChannel>()
                : ReadLightingChannelsLocked();

            int brightness = 0;
            int? resyncLevel = null;
            int orientation = 0;
            var mode = KrakenDisplayMode.Liquid;
            if (_model.HasLcd)
            {
                var lcdReply = ExchangeLocked(KrakenProtocol.EncodeLcdInfoRequest(), 0x31, 0x01, CommandReadTimeoutMs);
                var lcdInfo = lcdReply == null ? null : KrakenProtocol.DecodeLcdInfo(lcdReply);
                if (lcdInfo.HasValue)
                {
                    brightness = lcdInfo.Value.BrightnessPercent;
                    // The cooler reads back what was written, which may be a capped level. While following, the
                    // remembered slider is the user's value; otherwise only when it scales to exactly the reading.
                    if (_loadSlider?.Invoke() is { } remembered && (_follows?.Invoke() == true || Scaled(remembered) == brightness))
                    {
                        resyncLevel = Scaled(remembered) != brightness ? remembered : null;
                        brightness = remembered;
                    }
                    orientation = lcdInfo.Value.OrientationQuarterTurns;
                    // The panel reports its own size; the model table is only the seed, so a
                    // variant we have not measured still gets driven at its real resolution.
                    if (lcdInfo.Value.Width > 0 && lcdInfo.Value.Height > 0)
                    {
                        if (lcdInfo.Value.Width != _lcdWidth || lcdInfo.Value.Height != _lcdHeight)
                        {
                            ServiceLog.Info(
                                $"[nzxt-kraken] panel reports {lcdInfo.Value.Width}x{lcdInfo.Value.Height}, " +
                                $"table said {_lcdWidth}x{_lcdHeight}; using the device");
                        }
                        _lcdWidth = lcdInfo.Value.Width;
                        _lcdHeight = lcdInfo.Value.Height;
                    }
                }

                var modeReply = ExchangeLocked(KrakenProtocol.EncodeReadDisplayModeRequest(), 0x31, 0x03, CommandReadTimeoutMs);
                mode = (modeReply == null ? null : KrakenProtocol.DecodeDisplayMode(modeReply))
                    ?? KrakenDisplayMode.Liquid;
            }

            // A cooler that stayed dark across a reconnect, or across a restart, reads back
            // zero, which is not the user's value; the one held here (or persisted) is.
            var sleepNow = _model.HasLcd && _lockSleep is not null && _lockSetting is not null
                && _lockSleep.IsSleeping(LockKey, _lockSetting);
            if (!_lockDimmed && !_lockRestorePending && _model.HasLcd && _loadRestore?.Invoke() is { } persisted)
            {
                _lockSavedBrightness = persisted;
                _lockRestorePending = true;
            }
            _fade = null;
            var restore = _lockRestorePending || (_lockDimmed && !sleepNow);
            if (_lockDimmed || restore)
            {
                brightness = _lockSavedBrightness;
            }
            else if (sleepNow)
            {
                _lockSavedBrightness = brightness;
                _saveRestore?.Invoke(brightness);
            }
            _lockDimmed = sleepNow;

            _snapshot = new KrakenSnapshot(
                0, 0, 0, 0, 0,
                fw?.ToString() ?? "",
                brightness, orientation, mode, channels);
            if (_model.HasLcd)
            {
                _lcd = _lcdFactory?.Open(_device.Serial);
                if (_lcd == null)
                {
                    ServiceLog.Info("[nzxt-kraken] connected without an LCD bulk pipe; image upload unavailable");
                }
            }

            if (_model.HasLcd && !sleepNow)
            {
                // Changes made while the glass was held dark and not yet shown.
                ApplyPendingWhileDarkLocked();
            }
            var restoreFailed = false;
            if (_model.HasLcd && (sleepNow || restore))
            {
                var wrote = WriteLocked(BacklightReport(sleepNow ? 0 : Scaled(brightness), orientation));
                if (!sleepNow)
                {
                    if (wrote)
                    {
                        _saveRestore?.Invoke(null);
                    }
                    else
                    {
                        restoreFailed = true;
                    }
                }
                ServiceLog.Info(sleepNow
                    ? "[lcd-lock] kraken LCD attached while locked; staying dark"
                    : $"[lcd-lock] kraken LCD restored to {brightness}% after reconnect");
            }
            _lockRestorePending = restoreFailed;
            if (resyncLevel is { } resync && !sleepNow && !restore)
            {
                // The cooler kept a stale capped level across the reconnect.
                WriteLocked(BacklightReport(Scaled(resync), orientation));
            }

            _isConnected = true;
            return true;
        }
    }

    private IReadOnlyList<KrakenLightingChannel> ReadLightingChannelsLocked()
    {
        // The accessory table can answer empty right after the stream starts, and an empty
        // result would leave every RGB zone undrivable for the whole session.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var channels = ReadLightingChannelsOnceLocked();
            if (channels.Count > 0)
            {
                return channels;
            }
        }
        ServiceLog.Warn("[nzxt-kraken] no RGB channels reported; lighting stays unavailable");
        return Array.Empty<KrakenLightingChannel>();
    }

    private IReadOnlyList<KrakenLightingChannel> ReadLightingChannelsOnceLocked()
    {
        var channels = new List<KrakenLightingChannel>();
        var reply = ExchangeLocked(KrakenProtocol.EncodeLightingInfoRequest(), 0x21, 0x03, CommandReadTimeoutMs);
        if (reply == null)
        {
            return channels;
        }
        int count = KrakenProtocol.DecodeChannelCount(reply);
        for (int channel = 0; channel < count; channel++)
        {
            // A channel is a chain: each slot is one accessory daisied off the last. A
            // multi-fan radiator reports as ONE accessory covering the whole radiator
            // (an F240 is a single 0x1B, not two entries), so the slots past the first
            // are only populated when the user has chained more hardware onto the port.
            byte first = 0;
            int leds = 0;
            int rings = 0;
            int accessories = 0;
            for (int slot = 0; slot < KrakenProtocol.AccessorySlotsPerChannel; slot++)
            {
                byte accessory = KrakenProtocol.DecodeAccessory(reply, channel, slot);
                if (accessory == 0)
                {
                    continue;
                }
                if (accessories == 0)
                {
                    first = accessory;
                }
                accessories++;
                leds += KrakenProtocol.LedCountForAccessory(accessory);
                var (accRings, _) = KrakenProtocol.AccessoryRings(accessory);
                rings += accRings;
            }
            if (accessories == 0)
            {
                continue;
            }
            var name = KrakenProtocol.AccessoryName(first);
            if (accessories > 1)
            {
                name = $"{name} x{accessories}";
            }
            channels.Add(new KrakenLightingChannel((byte)(1 << channel), first, name, leds, rings));
        }
        return channels;
    }

    /// <summary>
    /// Requests telemetry and folds it into the snapshot. Returns false only when the
    /// device looks gone, which is the worker's signal to tear the handle down.
    /// </summary>
    public bool Poll()
    {
        lock (_lock)
        {
            AwaitFrameClosedLocked();
            if (_device == null)
            {
                return false;
            }
            DrainLocked();
            RetryRestoreLocked();
            if (_model.CamSessionInit)
            {
                var now = Clock();
                LogCamDiagLocked(now);
                if (_streamReady && !PollQuietLocked(now))
                {
                    return true;
                }
            }
            if (!WriteLocked(KrakenProtocol.EncodeStatusRequest()))
            {
                return false;
            }
            Span<byte> buf = stackalloc byte[KrakenProtocol.ReportLength];
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int n = _device.Read(buf, PollReadTimeoutMs);
                if (n < 0)
                {
                    return false;
                }
                if (n == 0)
                {
                    // A quiet window is not a disconnect.
                    return true;
                }
                var reading = KrakenProtocol.DecodeStatus(buf[..n]);
                if (reading.HasValue)
                {
                    FoldStatusLocked(reading.Value);
                    return true;
                }
            }
            return true;
        }
    }

    private void FoldStatusLocked(KrakenReading reading)
    {
        _snapshot = _snapshot.WithReading(reading);
        Volatile.Write(ref _lastStatusMs, Clock());
        Interlocked.Increment(ref _statusReads);
    }

    /// <summary>
    /// The poll of an open 2023 Kraken stream: the keepalive when due, and no status request
    /// while reports keep arriving. Returns true when the caller must still request one
    /// because none has been read for the fallback window, false when this poll is done.
    /// </summary>
    private bool PollQuietLocked(long now)
    {
        if (now - _lastKeepAliveMs >= CamKeepAliveMs)
        {
            WriteLocked(CamKeepAlive);
            _lastKeepAliveMs = now;
        }
        var at = Volatile.Read(ref _lastStatusMs);
        if (now - at < CamStatusFallbackMs)
        {
            return false;
        }
        Interlocked.Increment(ref _statusFallbacks);
        if (now - _fallbackLoggedMs >= CamFallbackLogMs)
        {
            _fallbackLoggedMs = now;
            ServiceLog.Warn(
                $"[nzxt-kraken] no status report for {(at == 0 ? "the whole session" : $"{now - at} ms")} while streaming; polling once");
        }
        return true;
    }

    private void LogCamDiagLocked(long now)
    {
        if (_diagAtMs == 0)
        {
            _diagAtMs = now;
            return;
        }
        if (now - _diagAtMs < CamDiagMs)
        {
            return;
        }
        _diagAtMs = now;
        var written = Interlocked.Exchange(ref _framesWritten, 0);
        var skipped = Interlocked.Exchange(ref _framesSkipped, 0);
        var reports = Interlocked.Exchange(ref _statusReads, 0);
        var fallbacks = Interlocked.Exchange(ref _statusFallbacks, 0);
        if (written + skipped + reports + fallbacks == 0)
        {
            return;
        }
        ServiceLog.Info(
            $"[nzxt-kraken] {CamDiagMs / 1000} s: {written} frames written, {skipped} skipped as unchanged, " +
            $"{reports} status reports read, {fallbacks} fallback polls, stream {(_streamReady ? "open" : "closed")}" +
            CamCadenceText() + CamReplyText());
    }

    public bool SetPumpCurve(ReadOnlySpan<byte> duties) => SendCurve(pump: true, duties);

    public bool SetFanCurve(ReadOnlySpan<byte> duties) => SendCurve(pump: false, duties);

    /// <summary>Applies a flat duty by filling every point of the curve.</summary>
    public bool SetPumpDuty(int percent) => SetPumpCurve(FlatCurve(percent, KrakenProtocol.PumpDutyFloor));

    public bool SetFanDuty(int percent) => SetFanCurve(FlatCurve(percent, 0));

    private static byte[] FlatCurve(int percent, int floor)
    {
        byte duty = (byte)Math.Clamp(percent, floor, 100);
        var curve = new byte[KrakenProtocol.CurvePointCount];
        curve.AsSpan().Fill(duty);
        return curve;
    }

    private bool SendCurve(bool pump, ReadOnlySpan<byte> duties)
    {
        lock (_lock)
        {
            AwaitFrameClosedLocked();
            var channel = _useNewSpeedChannels
                ? (pump ? KrakenProtocol.PumpChannel : KrakenProtocol.FanChannel)
                : (pump ? KrakenProtocol.LegacyPumpChannel : KrakenProtocol.LegacyFanChannel);
            return WriteLocked(KrakenProtocol.EncodeSpeedCurve(channel, duties));
        }
    }

    /// <summary>
    /// Backlight and rotation share one command, so both are always sent together using
    /// the snapshot for whichever value the caller is not changing.
    /// </summary>
    public bool SetLcdBacklight(int brightnessPercent, int orientationQuarterTurns)
    {
        // Taken before the transfer lock; the flag only flips under both, so a stale read at
        // worst sends one command that the lock path then corrects.
        var report = BacklightReport(Scaled(brightnessPercent), orientationQuarterTurns);
        // A rotation re-uploads the stored frame, which is a full bulk transfer, so this
        // takes the transfer lock first - same order as PushStreamFrame, never the reverse.
        lock (_lcdTransferLock)
        lock (_lock)
        {
            // While the lock holds the glass dark a new level is remembered, not shown.
            if (_lockDimmed)
            {
                _lockSavedBrightness = Math.Clamp(brightnessPercent, 0, 100);
                // A restart mid-lock restores this level, not the one from before the change.
                _saveRestore?.Invoke(_lockSavedBrightness);
                // Dark, or the step a fade down is on: the change must not jump the ramp.
                var shown = _fade is { } running ? running.LevelAt(Clock()) : 0;
                report = BacklightReport(shown, orientationQuarterTurns);
            }
            else
            {
                // An explicit level after the unlock wins over a fade up still in flight, and with
                // the fade gone nothing else would clear the marker it was returning to.
                if (_fade is { Down: false })
                {
                    _saveRestore?.Invoke(null);
                    _lockRestorePending = false;
                }
                _fade = null;
            }
            if (_device == null || !WriteLocked(report))
            {
                return false;
            }
            int turns = orientationQuarterTurns & 0x03;
            bool rotated = turns != _snapshot.LcdOrientationQuarterTurns;
            _snapshot = _snapshot.WithLcd(Math.Clamp(brightnessPercent, 0, 100), turns);
            _saveSlider?.Invoke(_snapshot.LcdBrightness);
            // The panel will not re-orient a stored image on its own.
            if (rotated && _lastLcdFrame != null && _snapshot.DisplayMode == KrakenDisplayMode.Bucket)
            {
                if (_lockDimmed)
                {
                    _lockPendingReupload = true;
                }
                else
                {
                    _lockPendingReupload = false;
                    UploadLcdFrameLocked(_lastLcdFrame, turns);
                }
            }
            return true;
        }
    }

    public bool SetDisplayMode(KrakenDisplayMode mode, int bucketIndex = 0)
    {
        bool plus = _model.Rgb565DirectStream;
        if (plus && mode != KrakenDisplayMode.Bucket)
        {
            // Whatever the device answers, the held image must stop being resent.
            ClearPlusHold();
        }
        lock (_lock)
        {
            AwaitFrameClosedLocked();
            if (_device == null)
            {
                return false;
            }
            if (plus && mode == KrakenDisplayMode.Bucket)
            {
                // No bucket store on this firmware: the stored still goes out as a direct frame.
                if (_lastLcdFrame == null)
                {
                    return false;
                }
                _lockPendingMode = _lockDimmed ? mode : null;
                _lockPendingReupload = false;
                _snapshot = _snapshot.WithDisplayMode(mode);
                if (!_lockDimmed)
                {
                    MarkPlusStillDirty();
                }
                return true;
            }
            if (_lockDimmed)
            {
                // Held until unlock; the glass stays dark and unchanged meanwhile.
                _lockPendingMode = mode;
                _lockPendingReupload = false;
                _snapshot = _snapshot.WithDisplayMode(mode);
                return true;
            }
            // An explicit change after the lock supersedes anything held from before it.
            _lockPendingMode = null;
            _lockPendingReupload = false;
            var reply = ExchangeLocked(
                KrakenProtocol.EncodeSetDisplayMode(mode, bucketIndex), 0x39, 0x01, CommandReadTimeoutMs);
            if (plus)
            {
                _streamReady = false;
            }
            if (!AcceptedLocked(reply))
            {
                return false;
            }
            _snapshot = _snapshot.WithDisplayMode(mode);
            if (_directStream)
            {
                _streamReady = false;
            }
            return true;
        }
    }

    public bool SetLighting(byte channelId, KrakenColorMode mode, KrakenAnimationSpeed speed, ReadOnlySpan<byte> rgbColors, bool forward = true)
    {
        var report = KrakenProtocol.EncodeColors(channelId, mode, speed, rgbColors, forward);
        lock (_lock)
        {
            AwaitFrameClosedLocked();
            return WriteLocked(report);
        }
    }

    /// <summary>
    /// Sets every LED on a channel with one report. The firmware applies it on arrival, so
    /// there is no latch, no apply command, and nothing to wait for; the NAK this answers
    /// with carries no information (it answers writes that visibly land).
    /// </summary>
    public bool SetDirectColors(byte channelId, ReadOnlySpan<byte> rgbColors)
    {
        lock (_lock)
        {
            AwaitFrameClosedLocked();
            switch (_model.Lighting)
            {
                case KrakenLightingProtocol.ChannelReport:
                    return WriteLocked(KrakenProtocol.EncodeChannelColors(channelId, rgbColors));

                case KrakenLightingProtocol.StreamedTables:
                {
                    // Both tables then the latch, in that order: the submit is what the
                    // firmware acts on, and a table written after it lands on the next frame.
                    var tables = KrakenProtocol.EncodeStreamedColors(channelId, rgbColors);
                    foreach (var table in tables)
                    {
                        if (!WriteLocked(table))
                        {
                            return false;
                        }
                    }
                    return WriteLocked(KrakenProtocol.EncodeSubmitColors(channelId));
                }

                default:
                    return false;
            }
        }
    }

    /// <summary>Per-channel LED ceiling for the attached model's lighting protocol.</summary>
    public int MaxDirectColors => Model.Lighting == KrakenLightingProtocol.StreamedTables
        ? KrakenProtocol.MaxStreamedColors
        : KrakenProtocol.MaxDirectColors;

    /// <summary>
    /// Uploads one full-panel RGBA frame and makes the panel show it.
    ///
    /// Every bucket is deleted first. That step is not optional: a stale allocation makes
    /// the setup command return success while placing the image somewhere the panel never
    /// renders, which is the failure that makes this sequence look like it works when it
    /// does not.
    ///
    /// The bucket store is flash, and a full cycle measures ~450 ms, so this is for still
    /// images. Do not drive an animation through it.
    /// </summary>
    public bool UploadLcdImage(ReadOnlySpan<byte> rgba)
    {
        var expected = LcdFrameBytes;
        if (expected <= 0 || rgba.Length != expected)
        {
            ServiceLog.Warn($"[nzxt-kraken] LCD frame must be {expected} bytes, got {rgba.Length}");
            return false;
        }

        var source = rgba.ToArray();
        if (_model.Rgb565DirectStream)
        {
            return UploadPlusImage(source);
        }
        lock (_lcdTransferLock)
        lock (_lock)
        {
            if (_device == null || _lcd == null)
            {
                return false;
            }
            _lastLcdFrame = source;
            _lastLcdGif = null;
            _streamReady = false;
            if (_lockDimmed)
            {
                // Stored now, shown on unlock.
                _lockPendingReupload = true;
                _lockPendingMode = null;
                _snapshot = _snapshot.WithDisplayMode(KrakenDisplayMode.Bucket);
                return true;
            }
            _lockPendingReupload = false;
            _lockPendingMode = null;
            return UploadLcdFrameLocked(source, _snapshot.LcdOrientationQuarterTurns);
        }
    }

    // Plus V2 still: stored, then encoded and sent outside the device lock.
    private bool UploadPlusImage(byte[] source)
    {
        lock (_lcdTransferLock)
        {
            lock (_lock)
            {
                if (_device == null || _lcd == null)
                {
                    return false;
                }
                _lastLcdFrame = source;
                _lastLcdGif = null;
                _snapshot = _snapshot.WithDisplayMode(KrakenDisplayMode.Bucket);
                _lockPendingMode = null;
                if (_lockDimmed)
                {
                    // Stored now, shown on unlock.
                    _lockPendingReupload = true;
                    return true;
                }
                _lockPendingReupload = false;
            }
            MarkPlusStillDirty();
            return PushPlusStillLocked(Clock());
        }
    }

    /// <summary>
    /// Turns one source frame into the bytes this model's panel decodes, into the reusable
    /// scratch buffer. Returns the encoded length and the bulk format byte that describes it.
    /// <paramref name="sourceIsBgra"/> is set for capture-order frames (the panel stream);
    /// the still-image route posts RGBA.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_lcdScratch))]
    private int EncodeLcdPayloadLocked(ReadOnlySpan<byte> frame, int quarterTurns, bool sourceIsBgra, out byte format)
    {
        int w = _lcdWidth, h = _lcdHeight;
        switch (_model.LcdFormat)
        {
            case KrakenLcdFormat.Q565:
                format = KrakenProtocol.BulkFormatQ565;
                EnsureLcdScratch(Q565Encoder.MaxEncodedLength(w, h));
                return Q565Encoder.Encode(frame, w, h, quarterTurns, _lcdScratch, sourceIsBgra);

            case KrakenLcdFormat.Rgb565:
                format = KrakenProtocol.BulkFormatRgb565;
                EnsureLcdScratch(Rgb565Encoder.EncodedLength(w, h));
                return Rgb565Encoder.Encode(frame, w, h, quarterTurns, _lcdScratch, sourceIsBgra);

            default:
            {
                format = KrakenProtocol.BulkFormatRgba8888;
                var wire = KrakenProtocol.ToWireRgba(frame, w, h, quarterTurns, sourceIsBgra);
                EnsureLcdScratch(wire.Length);
                wire.CopyTo(_lcdScratch.AsSpan());
                return wire.Length;
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_lcdScratch))]
    private void EnsureLcdScratch(int bytes)
    {
        if (_lcdScratch == null || _lcdScratch.Length < bytes)
        {
            _lcdScratch = new byte[bytes];
        }
    }

    private bool UploadLcdFrameLocked(byte[] source, int quarterTurns)
    {
        if (_device == null || _lcd == null)
        {
            return false;
        }
        if (_model.Rgb565DirectStream)
        {
            MarkPlusStillDirty();
            return true;
        }
        int encoded = EncodeLcdPayloadLocked(source, quarterTurns, sourceIsBgra: false, out var format);
        return UploadBucketLocked(_lcdScratch.AsSpan(0, encoded), format);
    }

    /// <summary>
    /// Uploads a GIF the pump animates itself and shows it. <paramref name="gif"/> was rotated
    /// for <paramref name="quarterTurns"/> off the lock, so a rotation since makes it Stale, as
    /// does, with <paramref name="onlyIfShown"/>, any upload that replaced <paramref name="unrotated"/>.
    /// The bulk transfer holds the HID lock; the cooler runs its own curves meanwhile.
    /// </summary>
    public KrakenGifUpload UploadLcdGif(byte[] gif, byte[] unrotated, int quarterTurns, bool onlyIfShown = false)
    {
        lock (_lcdTransferLock)
        lock (_lock)
        {
            if (_device == null || _lcd == null || !_model.FirmwareGif)
            {
                return KrakenGifUpload.Failed;
            }
            if (_snapshot.LcdOrientationQuarterTurns != (quarterTurns & 0x03)
                || (onlyIfShown && (!ReferenceEquals(_lastLcdGif, unrotated) || _snapshot.DisplayMode != KrakenDisplayMode.Bucket)))
            {
                return KrakenGifUpload.Stale;
            }
            // A rotation must not bring back the still this GIF replaced.
            _lastLcdFrame = null;
            _lastLcdGif = unrotated;
            return UploadBucketLocked(gif, KrakenProtocol.BulkFormatGif) ? KrakenGifUpload.Shown : KrakenGifUpload.Failed;
        }
    }

    /// <summary>The unrotated copy of the GIF on the panel, or null when it shows anything else.</summary>
    public byte[]? ShownLcdGif
    {
        get
        {
            lock (_lock)
            {
                return _snapshot.DisplayMode == KrakenDisplayMode.Bucket ? _lastLcdGif : null;
            }
        }
    }

    private bool UploadBucketLocked(ReadOnlySpan<byte> payload, byte format)
    {
        var lcd = _lcd;
        if (_device == null || lcd == null)
        {
            return false;
        }
        var header = KrakenProtocol.EncodeBulkHeader(format, payload.Length);
        int pages = KrakenProtocol.PagesFor(payload.Length);
        // The bucket wipe below invalidates whatever a stream set up.
        _streamReady = false;
        {
            // Drops a half-finished transfer; these models' uploads open with it.
            if (_directStream || _model.StreamRecreatesBuckets)
            {
                ExchangeLocked(KrakenProtocol.EncodeCancelTransfers(), 0x37, 0x03, CommandReadTimeoutMs);
            }
            // Releases the active bucket so it becomes deletable.
            ExchangeLocked(KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Liquid, 0), 0x39, 0x01, CommandReadTimeoutMs);
            for (int i = 0; i < KrakenProtocol.BucketCount; i++)
            {
                ExchangeLocked(KrakenProtocol.EncodeDeleteBucket(i), 0x33, 0x02, CommandReadTimeoutMs);
            }

            const int bucket = 0;
            var setup = ExchangeLocked(KrakenProtocol.EncodeSetupBucket(bucket, 0, pages), 0x33, 0x01, CommandReadTimeoutMs);
            if (!AcceptedLocked(setup))
            {
                ServiceLog.Warn("[nzxt-kraken] LCD bucket setup rejected");
                return false;
            }

            var start = ExchangeLocked(KrakenProtocol.EncodeStartTransfer(bucket), 0x37, 0x01, CommandReadTimeoutMs);
            if (!AcceptedLocked(start))
            {
                ServiceLog.Warn("[nzxt-kraken] LCD transfer start rejected");
                return false;
            }

            // The header must be its own bulk transfer; concatenating it with the pixels
            // corrupts the upload without any error being reported.
            if (!lcd.Write(header))
            {
                return false;
            }
            for (int offset = 0; offset < payload.Length; offset += BulkChunkBytes)
            {
                int len = Math.Min(BulkChunkBytes, payload.Length - offset);
                if (!lcd.Write(payload.Slice(offset, len)))
                {
                    ServiceLog.Warn($"[nzxt-kraken] LCD bulk write failed at offset {offset}");
                    return false;
                }
            }

            ExchangeLocked(KrakenProtocol.EncodeEndTransfer(), 0x37, 0x02, CommandReadTimeoutMs);

            var activate = ExchangeLocked(
                KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Bucket, bucket), 0x39, 0x01, CommandReadTimeoutMs);
            if (!AcceptedLocked(activate))
            {
                ServiceLog.Warn("[nzxt-kraken] LCD bucket activation rejected");
                return false;
            }
            _snapshot = _snapshot.WithDisplayMode(KrakenDisplayMode.Bucket);
            return true;
        }
    }

    /// <summary>
    /// Pushes one frame for a live stream, triple-buffered. Allocates its buckets on the
    /// first call and rotates thereafter; unlike <see cref="UploadLcdImage"/> this does
    /// not wipe the bucket table per frame, which is what makes a stream viable.
    ///
    /// Frames arrive in capture order (BGRA); the colour swap and the rotation both ride
    /// the encode rather than costing a separate pass over 1.6 MB.
    /// </summary>
    public bool PushStreamFrame(ReadOnlySpan<byte> bgra)
    {
        var expected = LcdFrameBytes;
        if (expected <= 0 || bgra.Length != expected)
        {
            return false;
        }
        if (_model.Rgb565DirectStream)
        {
            return PushPlusFrame(bgra);
        }
        if (_directStream)
        {
            return PushDirectFrame(bgra);
        }
        if (_model.StreamRecreatesBuckets)
        {
            return PushRecreatedBucketFrame(bgra);
        }
        lock (_lcdTransferLock)
        {
            // Every pushed frame re-activates bucket mode on the cooler, so frames stop while
            // the lock holds the glass dark. Reported as taken: a dropped frame is not a fault.
            if (LockDimmed)
            {
                return true;
            }
            int target;
            int encoded;
            byte format;
            byte[] scratch;
            IKrakenLcdTransport lcd;
            lock (_lock)
            {
                if (_device == null || _lcd == null)
                {
                    return false;
                }
                if (!_streamReady && !PrepareStreamBucketsLocked())
                {
                    return false;
                }
                lcd = _lcd;
                target = (_streamActiveBucket + 1) % StreamBucketCount;
                // Rotation rides the encode: the panel does not re-orient what it is sent.
                encoded = EncodeLcdPayloadLocked(
                    bgra, _snapshot.LcdOrientationQuarterTurns, sourceIsBgra: true, out format);
                var start = ExchangeLocked(
                    KrakenProtocol.EncodeStartTransfer(target), 0x37, 0x01, CommandReadTimeoutMs);
                if (start == null || !KrakenProtocol.IsAck(start))
                {
                    _streamReady = false;
                    return false;
                }
                scratch = _lcdScratch;
            }

            // Bulk endpoint only, outside _lock: HID commands for lighting and telemetry
            // keep flowing while the pixels stream, which is what lets an animation run at
            // its own rate instead of waiting a whole frame for the pipe.
            if (!WriteBulkPayload(lcd, format, scratch.AsSpan(0, encoded)))
            {
                lock (_lock) { _streamReady = false; }
                return false;
            }

            lock (_lock)
            {
                if (_device == null)
                {
                    return false;
                }
                ExchangeLocked(KrakenProtocol.EncodeEndTransfer(), 0x37, 0x02, CommandReadTimeoutMs);
                var activate = ExchangeLocked(
                    KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Bucket, target), 0x39, 0x01, CommandReadTimeoutMs);
                if (activate == null || !KrakenProtocol.IsAck(activate))
                {
                    // Re-allocate next call: a rejected transfer usually means the bucket
                    // table no longer matches what this hub believes.
                    _streamReady = false;
                    return false;
                }
                _streamActiveBucket = target;
                _snapshot = _snapshot.WithDisplayMode(KrakenDisplayMode.Bucket);
                return true;
            }
        }
    }


    // RGB565 direct live stream, Plus V2 and Kraken 2023 (KrakenModel.Rgb565DirectStream). The wire buffer holds the last
    // encoded frame, which is what the idle refresh resends; the source copy lets an
    // unchanged frame skip the encode. Buffers are guarded by _lcdTransferLock.
    //
    // The hold (_plusActive) says the panel is being kept lit by resends. It is armed only
    // by a send that began in the current generation and cleared by anything that ends the
    // content, always under _plusHoldLock, which is never held across I/O.
    private const long PlusIdleResendMs = 1000;
    private const long PlusEndDelayMs = 880;
    private const int PlusMinWaitMs = 20;
    private const int PlusIdleWaitMs = 250;
    private readonly object _plusHoldLock = new();
    private byte[]? _plusWire;
    private byte[]? _plusSource;
    private byte[]? _plusHeader;
    private int _plusTurns;
    private long _plusSentAtMs;
    private bool _plusActive;
    private bool _plusHoldIsStill;
    private bool _plusSendingStill;
    private bool _plusEndPending;
    private bool _plusStillDirty;
    private int _plusGen;
    private static readonly byte[] PlusBeginSession = KrakenProtocol.EncodeBeginLiveSession();
    private static readonly byte[] PlusStatusRequest = KrakenProtocol.EncodeStatusRequest();
    private static readonly byte[] PlusLcdInfoRequest = KrakenProtocol.EncodeLcdInfoRequest();
    private static readonly byte[] CamKeepAlive = KrakenProtocol.EncodeSessionKeepAlive();
    private static readonly byte[] CamBacklightOff = KrakenProtocol.EncodeSessionBacklightOff();
    private static readonly byte[] PlusCancel = KrakenProtocol.EncodeCancelTransfers();
    private static readonly byte[] PlusStart = KrakenProtocol.EncodeStartDirectTransfer(KrakenProtocol.BulkFormatRgb565);
    private static readonly byte[] PlusEnd = KrakenProtocol.EncodeEndTransfer();
    private static readonly byte[] PlusLiquid = KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Liquid, 0);

    // Ends the held content. endCommand asks the refresher to send the vendor's closing
    // mode command a short while later, as the vendor does after its last frame.
    private void ClearPlusHold(bool endCommand = false)
    {
        lock (_plusHoldLock)
        {
            _plusEndPending = endCommand && _plusActive;
            _plusGen++;
            _plusActive = false;
            _plusStillDirty = false;
        }
    }

    // Returns the generation the send belongs to and records which kind of content is in flight.
    private int BeginPlusSend(bool still)
    {
        lock (_plusHoldLock)
        {
            _plusSendingStill = still;
            return _plusGen;
        }
    }

    // Arms the hold only if nothing cleared it since the send began.
    private void ArmPlusHold(int gen, bool still, long now)
    {
        lock (_plusHoldLock)
        {
            if (gen != _plusGen)
            {
                return;
            }
            _plusActive = true;
            _plusSendingStill = false;
            _plusHoldIsStill = still;
            _plusEndPending = false;
            Volatile.Write(ref _plusSentAtMs, now);
        }
    }

    private void MarkPlusStillDirty()
    {
        lock (_plusHoldLock)
        {
            _plusStillDirty = true;
        }
    }

    /// <summary>
    /// One live frame on the RGB565 direct-stream models (Plus V2, Kraken 2023): <c>36 01 00 01 06</c>, a 20-byte header, RGB565 with
    /// the high byte first, <c>36 02</c>. No bucket exists on this firmware. An unchanged
    /// frame is not re-encoded and is held back until the idle refresh is due.
    /// </summary>
    private bool PushPlusFrame(ReadOnlySpan<byte> bgra)
    {
        if (_model.CamSessionInit)
        {
            return OfferCamFrame(bgra);
        }
        lock (_lcdTransferLock)
        {
            if (LockDimmed)
            {
                return true;
            }
            int w = _lcdWidth, h = _lcdHeight;
            int turns = _snapshot.LcdOrientationQuarterTurns;
            var now = Clock();
            bool changed = _plusSource == null || _plusSource.Length != bgra.Length
                || _plusWire == null || turns != _plusTurns || _plusHoldIsStill
                || !bgra.SequenceEqual(_plusSource);
            if (!changed && _plusActive && now - Volatile.Read(ref _plusSentAtMs) < PlusIdleResendMs)
            {
                Interlocked.Increment(ref _framesSkipped);
                return true;
            }
            if (changed)
            {
                EnsurePlusWire(Rgb565Encoder.EncodedLength(w, h));
                if (_plusSource == null || _plusSource.Length != bgra.Length)
                {
                    _plusSource = new byte[bgra.Length];
                }
                Rgb565Encoder.Encode(bgra, w, h, turns, _plusWire!, sourceIsBgra: true, highByteFirst: true);
                bgra.CopyTo(_plusSource);
                _plusTurns = turns;
            }
            return SendPlusWireLocked(now, still: false);
        }
    }

    private void EnsurePlusWire(int len)
    {
        if (_plusWire == null || _plusWire.Length != len)
        {
            _plusWire = new byte[len];
            _plusHeader = KrakenProtocol.EncodeBulkHeader(KrakenProtocol.BulkFormatRgb565, len);
        }
    }

    /// <summary>
    /// Milliseconds until <see cref="RefreshIdleFrame"/> has something to do, so a dedicated
    /// timer can wake on the deadline instead of polling.
    /// </summary>
    public int IdleRefreshDelayMs()
    {
        if (!_model.Rgb565DirectStream)
        {
            return PlusIdleWaitMs;
        }
        long now = Clock();
        lock (_plusHoldLock)
        {
            if (_plusStillDirty)
            {
                return PlusMinWaitMs;
            }
            long due;
            if (_plusActive)
            {
                due = _plusSentAtMs + PlusIdleResendMs;
            }
            else if (_plusEndPending)
            {
                due = _plusSentAtMs + PlusEndDelayMs;
            }
            else
            {
                return PlusIdleWaitMs;
            }
            return (int)Math.Clamp(due - now, PlusMinWaitMs, PlusIdleResendMs);
        }
    }

    /// <summary>
    /// Re-encodes a changed still, resends the held frame when the idle interval is up, and
    /// sends the closing mode command after a stream ends. No-op for every other model.
    /// </summary>
    public void RefreshIdleFrame()
    {
        if (!_model.Rgb565DirectStream || !Monitor.TryEnter(_lcdTransferLock))
        {
            return;
        }
        try
        {
            bool dirty, active, endPending;
            long sentAt;
            lock (_plusHoldLock)
            {
                dirty = _plusStillDirty;
                active = _plusActive;
                endPending = _plusEndPending;
                sentAt = _plusSentAtMs;
            }
            var now = Clock();
            if (dirty)
            {
                PushPlusStillLocked(now);
            }
            else if (active && now - sentAt >= PlusIdleResendMs && _plusWire != null && !LockDimmed)
            {
                SendPlusWireLocked(now, _plusHoldIsStill);
            }
            else if (endPending && !active && now - sentAt >= PlusEndDelayMs)
            {
                lock (_plusHoldLock)
                {
                    _plusEndPending = false;
                }
                lock (_lock)
                {
                    _streamReady = false;
                    if (_device != null)
                    {
                        WriteLocked(PlusLiquid);
                    }
                }
            }
        }
        finally
        {
            Monitor.Exit(_lcdTransferLock);
        }
    }

    /// <summary>
    /// Called when the panel stream ends. Stops the refresh and queues the vendor's closing
    /// mode command; a held still image is left alone.
    /// </summary>
    public void ReleaseStream()
    {
        if (!_model.Rgb565DirectStream)
        {
            return;
        }
        if (_model.CamSessionInit)
        {
            // Joined before the hold is cleared, so a send in flight cannot re-arm it afterwards.
            StopCamSender(join: true);
            lock (_camLock)
            {
                _camFresh = false;
            }
            _camStats.BreakChain();
        }
        lock (_plusHoldLock)
        {
            bool holdsStill = _plusSendingStill || (_plusActive && _plusHoldIsStill);
            if (!holdsStill)
            {
                _plusEndPending = true;
                _plusGen++;
                _plusActive = false;
            }
        }
    }

    // Waits for the cooler's 0x37 reply to a frame's start command (sub-command 1), at most
    // timeoutMs from sentAt, and records the latency or a miss. Status reports read meanwhile are folded
    // as ExchangeLocked does. A miss neither fails the frame nor touches the stream state. Caller holds
    // _lock, the only place HID input is read, so nothing else can take the reply. The deadline runs on
    // Stopwatch because TickCount64 follows the coarse system tick on Windows.
    private void AwaitCamReplyLocked(byte subCommand, int timeoutMs, long sentAt, KrakenReplySeries stats)
    {
        if (_device == null)
        {
            return;
        }
        Span<byte> buf = stackalloc byte[KrakenProtocol.ReportLength];
        long deadline = sentAt + timeoutMs * Stopwatch.Frequency / 1000;
        while (true)
        {
            long now = Stopwatch.GetTimestamp();
            long left = deadline - now;
            if (left <= 0)
            {
                stats.RecordMiss();
                return;
            }
            int waitMs = (int)Math.Max(1, (left * 1000 + Stopwatch.Frequency - 1) / Stopwatch.Frequency);
            int n = _device.Read(buf, waitMs);
            if (n < 0)
            {
                stats.RecordMiss();
                return;
            }
            if (n == 0)
            {
                continue;
            }
            var span = buf[..n];
            if (KrakenProtocol.IsStatusReply(span))
            {
                var reading = KrakenProtocol.DecodeStatus(span);
                if (reading.HasValue)
                {
                    FoldStatusLocked(reading.Value);
                }
                continue;
            }
            if (span.Length > 1 && span[0] == 0x37 && span[1] == subCommand)
            {
                stats.RecordReply(Stopwatch.GetTimestamp() - sentAt);
                return;
            }
        }
    }

    // Caller holds _lcdTransferLock. The device lock covers only the HID steps.
    private bool SendPlusWireLocked(long now, bool still)
    {
        int gen = BeginPlusSend(still);
        bool cam = _model.CamSessionInit;
        long startedAt = 0;
        var wire = _plusWire;
        var header = _plusHeader;
        IKrakenLcdTransport lcd;
        try
        {
            lock (_lock)
            {
                if (cam)
                {
                    YieldToWritersLocked();
                }
                if (_device == null || _lcd == null || wire == null || header == null)
                {
                    return false;
                }
                lcd = _lcd;
                if (!_streamReady)
                {
                    if (cam)
                    {
                        OpenCamSessionLocked();
                    }
                    else
                    {
                        WriteLocked(PlusBeginSession);
                        WriteLocked(PlusCancel);
                    }
                    _streamReady = true;
                }
                var mode = still ? KrakenDisplayMode.Bucket : KrakenDisplayMode.Liquid;
                if (_snapshot.DisplayMode != mode)
                {
                    _snapshot = _snapshot.WithDisplayMode(mode);
                }
                if (cam)
                {
                    // A reply left from an earlier frame must not answer this frame's start.
                    DrainLocked();
                    _camFrameOpen = true;
                }
                if (!WriteFrameLocked(PlusStart))
                {
                    _streamReady = false;
                    return false;
                }
                if (cam)
                {
                    startedAt = Stopwatch.GetTimestamp();
                    AwaitCamReplyLocked(0x01, CamStartReplyMs, startedAt, _camReplies);
                }
            }
            if (cam)
            {
                _camStats.RecordStart(startedAt);
            }
            // The header is its own bulk transfer, then the whole frame in one.
            if (!lcd.Write(header) || !lcd.Write(wire))
            {
                lock (_lock) { _streamReady = false; }
                return false;
            }
            lock (_lock)
            {
                if (_device == null)
                {
                    _streamReady = false;
                    return false;
                }
                if (!WriteFrameLocked(PlusEnd))
                {
                    _streamReady = false;
                    return false;
                }
            }
        }
        finally
        {
            if (cam)
            {
                lock (_lock)
                {
                    _camFrameOpen = false;
                    Monitor.PulseAll(_lock);
                }
            }
        }
        Interlocked.Increment(ref _framesWritten);
        ArmPlusHold(gen, still, now);
        return true;
    }

    // Cadenced LCD sender for the 2023 Kraken (KrakenModel.CamSessionInit). The vendor app starts a frame
    // every fixed interval and repeats a frame when nothing new is ready; this sender does the same. A
    // producer only hands over its latest frame (a newer one replaces a pending one, nothing queues) and
    // one sender thread owns the wire. It runs only while frames keep arriving: once they stop for the
    // idle window the hold's slower resend keeps the panel lit.
    private const long CamSenderIdleExitMs = 10_000;
    private const int CamSenderJoinMs = 2_000;
    private const long CamSendWarnMs = 60_000;
    private readonly object _camLock = new();
    private readonly KrakenCadenceStats _camStats = new();
    private readonly KrakenReplySeries _camReplies = new();

    internal KrakenReplySeries CamReplies => _camReplies;

    internal int StatusReportsRead => Volatile.Read(ref _statusReads);
    private byte[]? _camPending;
    private bool _camFresh;
    private long _camOfferedAtMs;
    private long _camWarnedAtMs;
    private CamRun? _camRun;
    // Set by Detach and Dispose under _camLock before the sender stops, so a frame offered in the
    // window before the device goes cannot start a new run; Connect clears it.
    private bool _camClosed;

    /// <summary>Tests run code in the window between an offer's device check and its sender start.</summary>
    internal Action? BeforeCamOffer { get; set; }

    /// <summary>Tests turn the thread off and step the sender by hand.</summary>
    internal bool CamSenderThread { get; set; } = true;

    /// <summary>The slot clock for one sender run; tests replace it with a fake.</summary>
    internal Func<CancellationToken, KrakenCadence> CadenceFactory { get; set; } = KrakenCadence.Create;

    internal bool CamSenderRunning
    {
        get { lock (_camLock) { return _camRun != null; } }
    }

    private sealed class CamRun(CancellationTokenSource cts, KrakenCadence cadence)
    {
        public CancellationTokenSource Cts { get; } = cts;
        public KrakenCadence Cadence { get; } = cadence;
        public Thread? Thread { get; set; }
    }

    // Encodes the producer's frame into the pending buffer. A frame dropped by the lock fade is reported as taken.
    private bool OfferCamFrame(ReadOnlySpan<byte> bgra)
    {
        if (LockDimmed)
        {
            return true;
        }
        lock (_lock)
        {
            if (_device == null || _lcd == null)
            {
                return false;
            }
        }
        BeforeCamOffer?.Invoke();
        int w = _lcdWidth, h = _lcdHeight;
        int turns = _snapshot.LcdOrientationQuarterTurns;
        var len = Rgb565Encoder.EncodedLength(w, h);
        lock (_camLock)
        {
            if (_camPending == null || _camPending.Length != len)
            {
                _camPending = new byte[len];
            }
            Rgb565Encoder.Encode(bgra, w, h, turns, _camPending, sourceIsBgra: true, highByteFirst: true);
            _camFresh = true;
            Volatile.Write(ref _camOfferedAtMs, Clock());
            if (_camRun == null && CamSenderThread && !_camClosed && !_disposed)
            {
                _camRun = StartCamRunLocked();
            }
        }
        return true;
    }

    private CamRun StartCamRunLocked()
    {
        var cts = new CancellationTokenSource();
        var run = new CamRun(cts, CadenceFactory(cts.Token));
        _camStats.BreakChain();
        run.Thread = new Thread(() => CamSenderLoop(run))
        {
            IsBackground = true,
            Name = "kraken-lcd-cadence",
        };
        run.Thread.Start();
        return run;
    }

    private void CamSenderLoop(CamRun run)
    {
        try
        {
            while (run.Cadence.WaitNextSlot(out _))
            {
                SendCamSlot();
                if (Clock() - Volatile.Read(ref _camOfferedAtMs) >= CamSenderIdleExitMs)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[nzxt-kraken] LCD sender stopped: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (_camLock)
            {
                if (ReferenceEquals(_camRun, run))
                {
                    _camRun = null;
                }
            }
            run.Cadence.Dispose();
            run.Cts.Dispose();
        }
    }

    private void StopCamSender(bool join, bool close = false)
    {
        CamRun? run;
        lock (_camLock)
        {
            if (close)
            {
                _camClosed = true;
            }
            run = _camRun;
            _camRun = null;
            run?.Cts.Cancel();
        }
        if (join && run?.Thread is { } thread && thread != Thread.CurrentThread)
        {
            thread.Join(CamSenderJoinMs);
        }
    }

    /// <summary>
    /// One slot of the cadenced wire: sends the pending frame, or the last one again when nothing new
    /// arrived. Holds the transfer lock across the frame, the device lock only for the short HID steps.
    /// </summary>
    internal bool SendCamSlot()
    {
        lock (_lcdTransferLock)
        {
            if (LockDimmed)
            {
                return true;
            }
            bool fresh;
            lock (_camLock)
            {
                fresh = _camFresh;
                if (fresh && _camPending is { } pending)
                {
                    var previous = _plusWire;
                    bool resized = previous == null || previous.Length != pending.Length;
                    _plusWire = pending;
                    _camPending = resized ? null : previous;
                    if (resized)
                    {
                        _plusHeader = KrakenProtocol.EncodeBulkHeader(KrakenProtocol.BulkFormatRgb565, pending.Length);
                    }
                    _camFresh = false;
                }
                else
                {
                    fresh = false;
                }
            }
            if (_plusWire == null)
            {
                return true;
            }
            var sent = SendPlusWireLocked(Clock(), still: false);
            if (sent)
            {
                _camStats.CountSend(fresh);
            }
            else
            {
                var now = Clock();
                if (_camWarnedAtMs == 0 || now - _camWarnedAtMs >= CamSendWarnMs)
                {
                    _camWarnedAtMs = now;
                    ServiceLog.Warn("[nzxt-kraken] LCD frame send failed; the session reopens on the next slot");
                }
            }
            return sent;
        }
    }

    private string CamReplyText()
    {
        if (_camReplies.Take(Stopwatch.Frequency) is not { } r)
        {
            return string.Empty;
        }
        var f = CultureInfo.InvariantCulture;
        return $", start reply ms p50 {r.P50Ms.ToString("F1", f)} p95 {r.P95Ms.ToString("F1", f)} " +
            $"max {r.MaxMs.ToString("F1", f)} over {r.Replies}, {r.Misses} missed";
    }

    private string CamCadenceText()
    {
        if (_camStats.Take(Stopwatch.Frequency) is not { } c)
        {
            return string.Empty;
        }
        var f = CultureInfo.InvariantCulture;
        return $", start-to-start ms min {c.MinMs.ToString("F1", f)} p50 {c.P50Ms.ToString("F1", f)} " +
            $"p95 {c.P95Ms.ToString("F1", f)} max {c.MaxMs.ToString("F1", f)} over {c.Samples}, " +
            $"{c.Fresh} new frames sent, {c.Repeated} repeated";
    }

    // The vendor app's order at stream start, then its real backlight a moment after the dark
    // step. Caller holds _lock and _lcdTransferLock.
    private void OpenCamSessionLocked()
    {
        WriteLocked(CamKeepAlive);
        WriteLocked(KrakenProtocol.EncodeSetUpdateInterval(_model.TelemetryInterval));
        WriteLocked(PlusStatusRequest);
        WriteLocked(PlusBeginSession);
        WriteLocked(PlusLcdInfoRequest);
        WriteLocked(PlusCancel);
        WriteLocked(CamBacklightOff);
        DelayMs(CamBacklightDelayMs);
        WriteLocked(BacklightReport(Scaled(_snapshot.LcdBrightness), _snapshot.LcdOrientationQuarterTurns));
        _lastKeepAliveMs = Clock();
    }

    // Caller holds _lcdTransferLock. Encodes the stored still at the current orientation
    // outside the device lock, then sends it.
    private bool PushPlusStillLocked(long now)
    {
        byte[]? source;
        int turns;
        lock (_lock)
        {
            if (_lockDimmed)
            {
                // Dropped while dark so the refresher stops waking; unlock re-shows it.
                lock (_plusHoldLock)
                {
                    _plusStillDirty = false;
                }
                _lockPendingReupload = true;
                return true;
            }
            source = _lastLcdFrame;
            turns = _snapshot.LcdOrientationQuarterTurns;
        }
        lock (_plusHoldLock)
        {
            if (!_plusStillDirty)
            {
                return true;
            }
            _plusStillDirty = false;
        }
        if (source == null)
        {
            return false;
        }
        EnsurePlusWire(Rgb565Encoder.EncodedLength(_lcdWidth, _lcdHeight));
        Rgb565Encoder.Encode(source, _lcdWidth, _lcdHeight, turns, _plusWire!, sourceIsBgra: false, highByteFirst: true);
        _plusSource = null;
        _plusTurns = turns;
        return SendPlusWireLocked(now, still: true);
    }

    /// <summary>
    /// One live frame on the 2023 Elite's direct path: start, raw BGR888 on the bulk
    /// endpoint, end. No bucket and no per-frame mode switch, so the flash store is never written.
    /// </summary>
    private bool PushDirectFrame(ReadOnlySpan<byte> bgra)
    {
        lock (_lcdTransferLock)
        {
            int encoded;
            byte[] scratch;
            IKrakenLcdTransport lcd;
            lock (_lock)
            {
                if (_device == null || _lcd == null)
                {
                    return false;
                }
                if (!_streamReady)
                {
                    // One-time entry: drop half-done transfers and stored images.
                    ExchangeLocked(KrakenProtocol.EncodeCancelTransfers(), 0x37, 0x03, CommandReadTimeoutMs);
                    ExchangeLocked(KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Liquid, 0), 0x39, 0x01, CommandReadTimeoutMs);
                    for (int i = 0; i < KrakenProtocol.BucketCount; i++)
                    {
                        ExchangeLocked(KrakenProtocol.EncodeDeleteBucket(i), 0x33, 0x02, CommandReadTimeoutMs);
                    }
                    _snapshot = _snapshot.WithDisplayMode(KrakenDisplayMode.Liquid);
                    _streamReady = true;
                }
                lcd = _lcd;
                EnsureLcdScratch(_lcdWidth * _lcdHeight * 3);
                encoded = KrakenProtocol.ToWireBgr(
                    bgra, _lcdWidth, _lcdHeight, _snapshot.LcdOrientationQuarterTurns, sourceIsBgra: true, _lcdScratch);
                // The capture shows one report read after start and end, not which,
                // so neither reply is required; re-entry is left to the calls that invalidate it.
                ExchangeLocked(KrakenProtocol.EncodeStartDirectTransfer(), 0x37, 0x01, DirectReplyTimeoutMs);
                scratch = _lcdScratch;
            }

            if (!WriteBulkPayload(lcd, KrakenProtocol.BulkFormatBgr888, scratch.AsSpan(0, encoded), DirectChunkBytes))
            {
                lock (_lock) { _streamReady = false; }
                return false;
            }

            lock (_lock)
            {
                if (_device == null)
                {
                    return false;
                }
                ExchangeLocked(KrakenProtocol.EncodeEndTransfer(), 0x37, 0x02, DirectReplyTimeoutMs);
                return true;
            }
        }
    }

    /// <summary>
    /// One live frame on the Z3, in the order a USB capture of the vendor app shows: the
    /// target bucket is deleted (twice) and re-created before every write, rotating
    /// through all sixteen at fixed page offsets. A filled bucket rewritten in place is
    /// acked but never shown.
    /// </summary>
    private bool PushRecreatedBucketFrame(ReadOnlySpan<byte> bgra)
    {
        lock (_lcdTransferLock)
        {
            // Re-entry after a failed frame is a burst of exchanges under _lock, so a run of
            // failures retries on a slow timer rather than crowding out cooling control.
            if (Clock() < Volatile.Read(ref _streamRetryAtMs))
            {
                return false;
            }
            bool ok = PushRecreatedBucketFrameOnce(bgra);
            _streamFailures = ok ? 0 : _streamFailures + 1;
            if (_streamFailures >= StreamFailuresBeforeRetryDelay)
            {
                Volatile.Write(ref _streamRetryAtMs, Clock() + StreamRetryDelayMs);
            }
            return ok;
        }
    }

    private bool PushRecreatedBucketFrameOnce(ReadOnlySpan<byte> bgra)
    {
        lock (_lcdTransferLock)
        {
            if (LockDimmed)
            {
                return true;
            }
            int target;
            int encoded;
            byte format;
            byte[] scratch;
            IKrakenLcdTransport lcd;
            lock (_lock)
            {
                if (_device == null || _lcd == null)
                {
                    return false;
                }
                if (!_streamReady)
                {
                    EnterRecreatedBucketStreamLocked();
                }
                lcd = _lcd;
                target = (_streamActiveBucket + 1) % KrakenProtocol.BucketCount;
                encoded = EncodeLcdPayloadLocked(
                    bgra, _snapshot.LcdOrientationQuarterTurns, sourceIsBgra: true, out format);
                // Pages cover the pixels only, not the bulk header: what the capture reserves.
                int pages = (encoded + KrakenProtocol.BucketPageBytes - 1) / KrakenProtocol.BucketPageBytes;
                ExchangeLocked(KrakenProtocol.EncodeDeleteBucket(target), 0x33, 0x02, CommandReadTimeoutMs);
                ExchangeLocked(KrakenProtocol.EncodeDeleteBucket(target), 0x33, 0x02, CommandReadTimeoutMs);
                var setup = ExchangeLocked(
                    KrakenProtocol.EncodeSetupBucket(target, target * pages, pages), 0x33, 0x01, CommandReadTimeoutMs);
                if (setup == null || !KrakenProtocol.IsAck(setup))
                {
                    _streamReady = false;
                    return false;
                }
                var start = ExchangeLocked(KrakenProtocol.EncodeStartTransfer(target), 0x37, 0x01, CommandReadTimeoutMs);
                if (start == null || !KrakenProtocol.IsAck(start))
                {
                    _streamReady = false;
                    return false;
                }
                scratch = _lcdScratch;
            }

            bool written = WriteBulkPayload(lcd, format, scratch.AsSpan(0, encoded));

            lock (_lock)
            {
                if (!written)
                {
                    _streamReady = false;
                    return false;
                }
                if (_device == null)
                {
                    return false;
                }
                ExchangeLocked(KrakenProtocol.EncodeEndTransfer(), 0x37, 0x02, CommandReadTimeoutMs);
                var show = ExchangeLocked(
                    KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Bucket, target), 0x39, 0x01, CommandReadTimeoutMs);
                if (show == null || !KrakenProtocol.IsAck(show))
                {
                    _streamReady = false;
                    return false;
                }
                _streamActiveBucket = target;
                _snapshot = _snapshot.WithDisplayMode(KrakenDisplayMode.Bucket);
                return true;
            }
        }
    }

    /// <summary>Stream entry: drops a half-done transfer, releases the panel and clears every bucket.</summary>
    private void EnterRecreatedBucketStreamLocked()
    {
        ExchangeLocked(KrakenProtocol.EncodeCancelTransfers(), 0x37, 0x03, CommandReadTimeoutMs);
        ExchangeLocked(KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Liquid, 0), 0x39, 0x01, CommandReadTimeoutMs);
        for (int i = 0; i < KrakenProtocol.BucketCount; i++)
        {
            ExchangeLocked(KrakenProtocol.EncodeDeleteBucket(i), 0x33, 0x02, CommandReadTimeoutMs);
        }
        _streamActiveBucket = -1;
        _streamReady = true;
        _snapshot = _snapshot.WithDisplayMode(KrakenDisplayMode.Liquid);
    }

    /// <summary>
    /// A reply counts as success when it carries the ack byte. The 2023 Elite on firmware 2.x
    /// is reported to answer successful bucket commands with other values (0x05), so there
    /// any reply does; a rejection still arrives as a NAK, which reads as null.
    /// </summary>
    private bool AcceptedLocked(byte[]? reply) =>
        reply != null && (_directStream || KrakenProtocol.IsAck(reply));

    /// <summary>Header then pixels on the bulk endpoint; takes no lock of its own.</summary>
    private static bool WriteBulkPayload(IKrakenLcdTransport lcd, byte format, ReadOnlySpan<byte> payload, int chunkBytes = BulkChunkBytes)
    {
        var header = KrakenProtocol.EncodeBulkHeader(format, payload.Length);
        // The header must be its own bulk transfer; concatenating corrupts the upload.
        if (!lcd.Write(header))
        {
            return false;
        }
        for (int offset = 0; offset < payload.Length; offset += chunkBytes)
        {
            int len = Math.Min(chunkBytes, payload.Length - offset);
            if (!lcd.Write(payload.Slice(offset, len)))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Clears the bucket table and reserves the stream's non-overlapping frame slots.</summary>
    private bool PrepareStreamBucketsLocked()
    {
        // Sized for a raw frame so any encoding fits, however incompressible the content.
        int pages = KrakenProtocol.PagesFor(_lcdWidth * _lcdHeight * 4);
        ExchangeLocked(KrakenProtocol.EncodeSetDisplayMode(KrakenDisplayMode.Liquid, 0), 0x39, 0x01, CommandReadTimeoutMs);
        for (int i = 0; i < KrakenProtocol.BucketCount; i++)
        {
            ExchangeLocked(KrakenProtocol.EncodeDeleteBucket(i), 0x33, 0x02, CommandReadTimeoutMs);
        }
        for (int bucket = 0; bucket < StreamBucketCount; bucket++)
        {
            var setup = ExchangeLocked(
                KrakenProtocol.EncodeSetupBucket(bucket, bucket * pages, pages), 0x33, 0x01, CommandReadTimeoutMs);
            if (setup == null || !KrakenProtocol.IsAck(setup))
            {
                ServiceLog.Warn($"[nzxt-kraken] stream bucket {bucket} setup rejected");
                return false;
            }
        }
        _streamActiveBucket = -1;
        _streamReady = true;
        return true;
    }

    /// <summary>
    /// Writes a command and waits for its reply. Unsolicited status reports arrive on the
    /// same pipe about once a second and are folded into the snapshot rather than discarded.
    /// Returns null when no matching reply arrived.
    /// </summary>
    private byte[]? ExchangeLocked(byte[] request, byte replyReportId, byte replySubCommand, int timeoutMs)
    {
        if (_device == null)
        {
            return null;
        }
        // Fire-and-forget writes (lighting, curves) leave their own replies queued. Reading
        // a stale one as this command's answer is how a healthy exchange reports failure, so
        // clear the queue before asking.
        DrainLocked();
        if (!WriteLocked(request))
        {
            return null;
        }
        var buf = new byte[KrakenProtocol.ReportLength];
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            int n = _device.Read(buf, timeoutMs);
            if (n < 0)
            {
                return null;
            }
            if (n == 0)
            {
                continue;
            }
            var span = buf.AsSpan(0, n);
            if (KrakenProtocol.IsStatusReply(span))
            {
                var reading = KrakenProtocol.DecodeStatus(span);
                if (reading.HasValue)
                {
                    FoldStatusLocked(reading.Value);
                }
                continue;
            }
            // A NAK echoes the command it rejected; one raised by a different command must
            // not fail this exchange.
            if (span.Length > 15 && span[0] == KrakenProtocol.ReportNak)
            {
                if (span[14] == request[0] && span[15] == request[1])
                {
                    // Once per distinct command, so a device that rejects every frame logs once.
                    int nakKey = (span[14] << 8) | span[15];
                    if (_lastNakKey != nakKey)
                    {
                        _lastNakKey = nakKey;
                        ServiceLog.Warn($"[nzxt-kraken] device rejected command {span[14]:X2} {span[15]:X2}");
                    }
                    return null;
                }
                continue;
            }
            if (span.Length > 1 && span[0] == replyReportId && span[1] == replySubCommand)
            {
                _lastNakKey = 0;
                return buf[..n];
            }
        }
        return null;
    }

    /// <summary>
    /// Empties the input queue, folding any telemetry it finds into the snapshot. The bound
    /// is a safety net: the device streams status once a second, so a healthy queue is short.
    /// </summary>
    private void DrainLocked()
    {
        if (_device == null)
        {
            return;
        }
        Span<byte> buf = stackalloc byte[KrakenProtocol.ReportLength];
        for (int i = 0; i < 64; i++)
        {
            int n = _device.Read(buf, 0);
            if (n <= 0)
            {
                return;
            }
            var span = buf[..n];
            if (KrakenProtocol.IsStatusReply(span))
            {
                var reading = KrakenProtocol.DecodeStatus(span);
                if (reading.HasValue)
                {
                    FoldStatusLocked(reading.Value);
                }
            }
        }
    }

    // Counts Detach calls, so a reader of per-connection state can tell a new cooler from the same one.
    private int _detaches;
    internal int Detaches => Volatile.Read(ref _detaches);

    public void Detach()
    {
        StopCamSender(join: false, close: true);
        lock (_lock)
        {
            Interlocked.Increment(ref _detaches);
            _camFrameOpen = false;
            Monitor.PulseAll(_lock);
            _isConnected = false;
            _fade = null;
            _snapshot = KrakenSnapshot.Empty;
            // A re-attached cooler may hold buckets from before; the next stream re-enters.
            _streamReady = false;
            Volatile.Write(ref _lastStatusMs, 0);
            ClearPlusHold();
            _lastNakKey = 0;
            Volatile.Write(ref _streamRetryAtMs, 0);
            Volatile.Write(ref _streamFailures, 0);
            _lcd?.Dispose();
            _lcd = null;
            _device?.Dispose();
            _device = null;
            _serial = null;
        }
    }

    public void Dispose()
    {
        StopCamSender(join: false, close: true);
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _camFrameOpen = false;
            Monitor.PulseAll(_lock);
            _isConnected = false;
            _fade = null;
            _snapshot = KrakenSnapshot.Empty;
            _lcd?.Dispose();
            _lcd = null;
            _device?.Dispose();
            _device = null;
            _serial = null;
        }
    }
}

public enum KrakenGifUpload
{
    Shown,
    /// <summary>The orientation or the shown GIF changed while it was being prepared; nothing was sent.</summary>
    Stale,
    Failed,
}
