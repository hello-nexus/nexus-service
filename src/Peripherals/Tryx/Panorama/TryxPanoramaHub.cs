using System;
using Nexus.Service.Lighting;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Fps;
using Nexus.Service.Media;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>Normalized (0..1) crop rectangle plus optional orientation (mirror + CW
/// rotation) the dashboard cropper produced, applied in the ffmpeg transcode so the
/// user's framing fills the panel without letterboxing.</summary>
public readonly record struct TryxVideoCrop(double X, double Y, double W, double H, int Rotate = 0, bool Mirror = false);

/// <summary>
/// Singleton coordinator for a Tryx Panorama AIO device. Owns the open transport,
/// the shared <see cref="TryxPanoramaState"/> snapshot, and the high-level
/// operations the heartbeat worker and REST routes call into.
/// Hot-plug is self-healing: each <see cref="EnsureConnected"/> re-runs port discovery.
/// </summary>
public sealed class TryxPanoramaHub : IDisposable
{
    private const string FpsDemandSource = "tryx-overlay";

    private readonly ITryxPanoramaPanelDiscovery _discovery;
    private readonly Func<TryxPanoramaPortInfo, ITryxPanoramaTransport> _transportFactory;
    private readonly ISensorProvider _sensors;
    private readonly IFpsProvider _fps;
    private readonly IConfigStore _configStore;
    private readonly object _lock = new();
    private ITryxPanoramaTransport? _transport;
    private bool _disposed;
    // Set while EnsureLocalCopyAsync holds the adb pull, so the heartbeat does not
    // issue a concurrent adb command against the same device.
    private volatile bool _importInProgress;
    private readonly TryxOverlayConfig _overlay;
    private readonly TryxSlideshow _slideshow = new(TryxThumbnailCache.ReadDuration);

    public TryxPanoramaHub(
        ITryxPanoramaPanelDiscovery discovery,
        Func<TryxPanoramaPortInfo, ITryxPanoramaTransport> transportFactory,
        ISensorProvider sensors,
        IFpsProvider fps,
        IConfigStore configStore)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
        _sensors = sensors;
        _fps = fps;
        _configStore = configStore;

        var saved = configStore.Load().Tryx;
        _overlay = new TryxOverlayConfig { Docked = saved.OverlayDocked };
        State.Brightness = saved.Brightness;
        LoadContent(saved, Array.Empty<string>());
    }

    private void LoadContent(TryxSettings saved, IReadOnlyList<string> customMedia)
    {
        _overlay.Items = ToRuntimeItems(saved.OverlayItems);
        _overlay.Color = saved.OverlayColor;
        _overlay.Align = saved.OverlayAlign;
        _overlay.Filter = saved.OverlayFilter;
        _overlay.Opacity = saved.OverlayOpacity;
        _overlay.Font = saved.OverlayFont;
        _overlay.Size = saved.OverlaySize;
        State.CurrentMedia = saved.CurrentMedia;
        State.CurrentMediaIsCustom = saved.CurrentMediaIsCustom;
        _slideshow.Configure(new TryxSlideshowConfig
        {
            Enabled = saved.SlideshowEnabled,
            IntervalSec = saved.SlideshowIntervalSec,
            Shuffle = saved.SlideshowShuffle,
            FinishVideos = saved.SlideshowFinishVideos,
        }, State.CurrentMedia, customMedia, NowMs());
    }

    /// <summary>
    /// Re-reads the persisted content after a profile switch or Panels apply and pushes it to a
    /// connected panel: the wallpaper (a custom clip only when the panel still holds it) and the overlay.
    /// Brightness and the docked flag stay as they are.
    /// </summary>
    public void ReloadFromSettings()
    {
        var saved = _configStore.Load().Tryx;
        lock (_txGate)
        {
            LoadContent(saved, ListCustomMedia());
            if (!IsConnected || _importInProgress)
            {
                return;
            }
            // A custom clip the panel does not hold cannot be selected; the preset-only fallback is no media.
            if (State.CurrentMediaIsCustom && !AvailableCustomMediaFilenames.Contains(State.CurrentMedia))
            {
                State.CurrentMedia = "";
                State.CurrentMediaIsCustom = false;
            }
            if (!string.IsNullOrEmpty(State.CurrentMedia))
            {
                SendReliable(TryxRkProtocol.BuildPreset(State.CurrentMedia, Wire(State.ScreenEnabled), State.Brightness));
            }
            SendReliable(TryxRkProtocol.BuildOverlay(
                BuildOverlayLines(), BuildOverlayPositions(), ParseHexColorRgb(_overlay.Color),
                _overlay.Font, _overlay.Size, _overlay.Align));
            _fps.SetDemand(FpsDemandSource, OverlayUsesFps());
        }
    }

    private static long NowMs() => Environment.TickCount64;

    // Session lock. State.ScreenEnabled stays the user's choice; only what goes on the wire
    // is forced off while the lock holds the panel, so unlock puts back exactly that choice.
    private const string LockKey = "screen";
    private LcdLockSleepSet? _lockSleep;
    private volatile bool _lockDimmed;

    private bool LockSetting() => _configStore.Load().Tryx.SleepWhenLocked;

    private bool Wire(bool screenOn) => screenOn && !_lockDimmed;

    /// <summary>The persisted Sleep-when-locked choice.</summary>
    public bool SleepWhenLocked => LockSetting();

    /// <summary>Wires the session lock; the setting is read once per lock.</summary>
    public void BindLockSleep(LcdLockState state)
    {
        _lockSleep = new LcdLockSleepSet(state);
        state.Changed += OnLockChanged;
    }

    // Lock order: _txGate, then _lock, then _lockGate, then _fadeGate. The two gates are leaves: nothing
    // holding either sends, takes _txGate or takes _lock, so they cannot invert with the send path.
    private readonly object _lockGate = new();

    /// <summary>Test seam: runs between the lock's decision and the start of its fade.</summary>
    internal Action? TestHookAfterLockDecision { get; set; }

    /// <summary>True while the lock holds the screen off on the wire.</summary>
    internal bool LockDimmed => _lockDimmed;

    /// <summary>
    /// Brightness steps a lock fade takes. Every step is a full config frame through the same
    /// serialized write path as any other command, and the panel re-enumerates on its own every
    /// so often, so this stays well under the other devices' step count.
    /// </summary>
    internal const int FadeSteps = 8;

    // The fade, sampled by one runner loop: a new or reversed fade is just new state for the same
    // loop, so steps never queue up behind each other, and a step that blocks on a slow write is
    // simply skipped over because the level is read off the clock.
    private readonly object _fadeGate = new();
    private LcdFade? _fade;
    private int _fadeLastSent = -1;
    // The panel was last told the screen is off (the end of a fade down or an instant off).
    private bool _offOnWire;
    private LcdFadeRunner? _fadeRunner;

    /// <summary>Millisecond clock the fade reads; tests replace it.</summary>
    internal Func<long> FadeClock { get; set; } = () => Environment.TickCount64;

    internal LcdFadeRunner FadeRunner => _fadeRunner ??= new LcdFadeRunner(StepFade, onFault: FaultFade);

    /// <summary>True while a lock fade is in flight; the slideshow holds still meanwhile.</summary>
    internal bool FadeActive
    {
        get { lock (_fadeGate) { return _fade is not null; } }
    }

    /// <summary>Test seam: runs inside the fade gate in the done branch, before it decides what to resend.</summary>
    internal Action? TestHookInFadeDoneGate { get; set; }

    /// <summary>Test seam: the unlock handler is about to take the lock gate.</summary>
    internal Action? TestHookUnlockHandlerEntered { get; set; }

    /// <summary>Test seam: the unlock handler has left the lock gate.</summary>
    internal Action? TestHookUnlockHandlerCleared { get; set; }

    /// <summary>Test seam: runs in a fade step between reading the ramp and writing the frame.</summary>
    internal Action? TestHookBeforeStepSend { get; set; }

    /// <summary>Test seam: runs in ApplyInitialConfig between the lock decision and the flag it sets.</summary>
    internal Action? TestHookInInitialConfigDecision { get; set; }

    // Caller holds _lockGate. Only records the ramp and wakes the loop; nothing is sent here.
    private void BeginFadeLocked(int from, int to, TimeSpan duration)
    {
        lock (_fadeGate)
        {
            _fade = new LcdFade(from, to, duration, FadeClock(), FadeSteps);
            _fadeLastSent = -1;
        }
        FadeRunner.Kick();
    }

    private void CancelFade()
    {
        lock (_fadeGate) { _fade = null; }
    }

    private int CurrentWireLevel()
    {
        lock (_fadeGate)
        {
            if (_fade is { } running)
            {
                return running.LevelAt(FadeClock());
            }
        }
        return _offOnWire ? 0 : State.Brightness;
    }

    /// <summary>One sample of the fade: sends the stepped level when it changed, and the final state when it lands.</summary>
    internal bool StepFade()
    {
        LcdFade? fade;
        int lastSent;
        lock (_fadeGate)
        {
            fade = _fade;
            lastSent = _fadeLastSent;
        }
        if (fade is null)
        {
            return false;
        }
        var now = FadeClock();
        var level = fade.LevelAt(now);
        TestHookBeforeStepSend?.Invoke();
        if (!fade.IsDoneAt(now))
        {
            if (level != lastSent && IsConnected)
            {
                bool sent;
                // Under the send gate, with the ramp and the screen choice read again inside it: a
                // step that was decided before an explicit change must not land after that change.
                lock (_txGate)
                {
                    sent = IsCurrentFade(fade) && SendOnly(TryxRkProtocol.BuildConfig(State.ScreenEnabled, level));
                }
                if (sent)
                {
                    lock (_fadeGate)
                    {
                        if (ReferenceEquals(_fade, fade)) { _fadeLastSent = level; }
                    }
                    ResendSettledIfCancelled(fade);
                }
            }
            return true;
        }
        // The end of the ramp: off for a fade down, the user's brightness for a fade up. Reliable,
        // because this is the state the panel keeps. The frames carry the user's own enabled choice,
        // so a screen switched off during the ramp is never relit by it.
        bool endSent;
        bool screenOn;
        lock (_txGate)
        {
            screenOn = State.ScreenEnabled;
            endSent = IsConnected && IsCurrentFade(fade) && SendReliable(fade.Down
                ? TryxRkProtocol.BuildConfig(false, State.Brightness)
                : TryxRkProtocol.BuildConfig(screenOn, State.Brightness));
        }
        var sentFinal = endSent;
        bool resend;
        bool more;
        lock (_fadeGate)
        {
            TestHookInFadeDoneGate?.Invoke();
            if (!ReferenceEquals(_fade, fade))
            {
                // Replaced by a reversal: the new ramp owns the screen. Cancelled by a reconnect: the
                // settled state goes out again, after the gate is released, since that is a send.
                resend = _fade is null;
                more = _fade is not null;
            }
            else
            {
                _fade = null;
                if (sentFinal)
                {
                    _offOnWire = fade.Down || !screenOn;
                }
                resend = false;
                more = false;
            }
        }
        if (resend)
        {
            ResendSettled();
        }
        return more;
    }

    /// <summary>A step threw: the ramp is dropped and the settled state goes out, so the screen is not left on a half step.</summary>
    internal void FaultFade()
    {
        bool had;
        lock (_fadeGate)
        {
            had = _fade is not null;
            _fade = null;
        }
        if (!had)
        {
            return;
        }
        try { ResendSettled(); }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[tryx] settling after a failed fade step: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // A reconnect's initial config writes without the send gate, so a step already in flight can
    // land after it. When the ramp was cancelled under a step, the settled state goes out again.
    private void ResendSettledIfCancelled(LcdFade fade)
    {
        lock (_fadeGate)
        {
            // Replaced by a reversal is not cancelled: resending then would flash the old state.
            if (_fade is not null)
            {
                return;
            }
        }
        ResendSettled();
    }

    private bool IsCurrentFade(LcdFade fade)
    {
        lock (_fadeGate) { return ReferenceEquals(_fade, fade); }
    }

    /// <summary>Test seam: runs in a resend after the frame is built, inside the send gate.</summary>
    internal Action? TestHookInResend { get; set; }

    // Built and sent under the send gate, so an explicit screen change cannot slip between reading
    // the state and the write and then be overwritten by it.
    private void ResendSettled()
    {
        lock (_txGate)
        {
            if (!IsConnected)
            {
                return;
            }
            var screenOn = Wire(State.ScreenEnabled);
            var frame = TryxRkProtocol.BuildConfig(screenOn, State.Brightness);
            TestHookInResend?.Invoke();
            if (SendOnly(frame))
            {
                _offOnWire = !screenOn;
            }
        }
    }

    internal void OnLockChanged(bool locked)
    {
        if (_lockSleep is null)
        {
            return;
        }
        if (locked)
        {
            // Decided under the gate: a handler that ran after its unlock sees the unlock here.
            lock (_lockGate)
            {
                if (!_lockSleep.IsSleeping(LockKey, LockSetting) || _lockDimmed)
                {
                    return;
                }
                _lockDimmed = true;
            }
            TestHookAfterLockDecision?.Invoke();
            lock (_lockGate)
            {
                // The unlock may have run since; then there is nothing to fade out.
                if (!_lockDimmed || !IsConnected || !State.ScreenEnabled)
                {
                    return;
                }
                ServiceLog.Info("[lcd-lock] tryx screen fading out");
                BeginFadeLocked(CurrentWireLevel(), 0, SleepBlackoutCoordinator.LockFadeDuration);
            }
            return;
        }
        bool wasDimmed;
        TestHookUnlockHandlerEntered?.Invoke();
        try
        {
            lock (_lockGate)
            {
                _lockSleep.Drain();
                wasDimmed = _lockDimmed;
                _lockDimmed = false;
                if (!wasDimmed || !IsConnected || !State.ScreenEnabled)
                {
                    return;
                }
                ServiceLog.Info("[lcd-lock] tryx screen fading in");
                // Screen on at brightness 0, then up to the user's level: from 0 when the panel is off,
                // or from the step a fade down was caught on.
                BeginFadeLocked(CurrentWireLevel(), State.Brightness, SleepBlackoutCoordinator.UnlockFadeDuration);
                // The clip that comes back with the screen gets a full hold from the start of the ramp,
                // so the heartbeat inside it cannot select a clip at full brightness.
                _slideshow.Rearm(State.CurrentMedia, NowMs());
            }
        }
        finally
        {
            TestHookUnlockHandlerCleared?.Invoke();
        }
    }

    public TryxPanoramaState State { get; } = new();

    /// <summary>True while an import holds the port; the heartbeat skips STATE-all then.</summary>
    public bool ImportInProgress => _importInProgress;

    public bool IsConnected => _transport is { IsOpen: true };

    public TryxOverlayConfig Overlay => _overlay;

    public TryxSlideshowConfig Slideshow => _slideshow.Config;

    /// <summary>The custom-upload library the media list shows and the slideshow cycles on
    /// RK firmware: the panel's reported /userdata/user/ files once it has answered this
    /// connection (so Kanali uploads and deletes show as-is), else the local thumbnail record;
    /// minus cloud downloads, which Nexus's own install also lands in /userdata/user/. The
    /// legacy adb firmware's /sdcard/pcMedia listing is not part of it.</summary>
    public List<string> ListCustomMedia()
        => (MediaListReceived ? AvailableCustomMediaFilenames : TryxThumbnailCache.ListCustomMedia())
            .Where(n => !IsCloudDownload(n))
            .Distinct(StringComparer.Ordinal).ToList();

    private bool MediaListReceived => _transport is { MediaListVersion: > 0 };

    // Cloud themes are named download_<materialId> by both Kanali's and Nexus's install paths.
    public static bool IsCloudDownload(string name)
        => name.StartsWith("download_", StringComparison.Ordinal)
           && name.Length > 9 && char.IsAsciiDigit(name[9]);

    public void SetSlideshow(TryxSlideshowConfig config)
    {
        _slideshow.Configure(config, State.CurrentMedia, ListCustomMedia(), NowMs());
        var applied = _slideshow.Config;
        _configStore.Update(s =>
        {
            s.Tryx.SlideshowEnabled = applied.Enabled;
            s.Tryx.SlideshowIntervalSec = applied.IntervalSec;
            s.Tryx.SlideshowShuffle = applied.Shuffle;
            s.Tryx.SlideshowFinishVideos = applied.FinishVideos;
        });
    }

    /// <summary>Wallpaper preset ids the connected panel reported as on-device
    /// (see <see cref="ITryxPanoramaTransport.AvailableMediaIds"/>); empty before
    /// the panel's media-list push arrives or when disconnected.</summary>
    public IReadOnlyList<string> AvailableMediaIds => _transport?.AvailableMediaIds ?? Array.Empty<string>();

    /// <summary>Filenames the panel reported it has stored (device truth), or empty if it
    /// has not pushed its list this session (it does so only on a cold boot / re-enumeration).</summary>
    public IReadOnlyList<string> AvailableMediaFilenames => _transport?.AvailableMediaFilenames ?? Array.Empty<string>();

    /// <summary>Custom user uploads the panel reported (/userdata/user/), excluding presets and
    /// cloud downloads; the library shown in the custom-media list. Empty when disconnected.</summary>
    public IReadOnlyList<string> AvailableCustomMediaFilenames => _transport?.AvailableCustomMediaFilenames ?? Array.Empty<string>();

    // Fixed per-model on-panel storage; the panel's media-list push carries only
    // per-file sizes, never a device total, so this is a hardcoded spec value.
    public const long MediaCapacityBytes = 2L * 1024 * 1024 * 1024;

    // Headroom a transfer must clear below MediaCapacityBytes: MediaUsedBytes reflects
    // this session's last-synced count, not a live on-device query, so this absorbs
    // filesystem overhead and any push still in flight.
    public const long MediaCapacityPaddingBytes = 64L * 1024 * 1024;

    private const string InsufficientSpaceMsg = "insufficient_space";

    private readonly object _mediaSizesLock = new();
    private readonly Dictionary<string, long> _mediaSizes = new(StringComparer.Ordinal);
    private int _syncedMediaListVersion = -1;
    // Set once the media-list request has been sent for the current connection (after the panel
    // serial is known); reset on each connect so a reconnect re-fetches.
    private volatile bool _fileListRequested;

    /// <summary>The panel's serial_number (needed as the sn on get_file_list / file_remove);
    /// empty until the device_info reply (from the connect-time CMD_Get_DeviceInfo) is parsed.</summary>
    public string PanelSerial => _transport?.PanelSerial ?? "";

    /// <summary>The panel's firmware_version from the same device_info reply; empty until it is parsed.</summary>
    public string PanelFirmwareVersion => _transport?.PanelFirmwareVersion ?? "";

    /// <summary>Bytes stored on the panel's /userdata: this session's own per-file map,
    /// re-synced from the transport's media-list push on each version bump and adjusted
    /// locally for uploads/deletes made since, until the next list reply replaces them.</summary>
    public long MediaUsedBytes
    {
        get
        {
            lock (_mediaSizesLock)
            {
                SyncMediaSizesLocked();
                long total = 0;
                foreach (var size in _mediaSizes.Values)
                {
                    total += size;
                }
                return total;
            }
        }
    }

    // Re-syncs from the transport only on a version bump (a fresh panel push), so local
    // deltas recorded between pushes are not overwritten by re-reading the same push twice.
    private void SyncMediaSizesLocked()
    {
        var transport = _transport;
        if (transport is null)
        {
            return;
        }
        var version = transport.MediaListVersion;
        if (version == _syncedMediaListVersion)
        {
            return;
        }
        _mediaSizes.Clear();
        foreach (var kv in transport.MediaFileSizes)
        {
            _mediaSizes[kv.Key] = kv.Value;
        }
        _syncedMediaListVersion = version;
    }

    private void RecordMediaUpload(string deviceFileName, long containerBytes)
    {
        lock (_mediaSizesLock)
        {
            SyncMediaSizesLocked();
            _mediaSizes[deviceFileName] = containerBytes;
        }
    }

    /// <summary>Removes <paramref name="deviceFileName"/> from the used-bytes accounting
    /// after a delete; the caller has already removed the on-device file (or, for the RK
    /// usbprint transport, its local record - the panel exposes no confirmed remove wire
    /// frame, so this local delta is corrected by the next real device push if wrong).</summary>
    public void RecordMediaDeleted(string deviceFileName)
    {
        lock (_mediaSizesLock)
        {
            SyncMediaSizesLocked();
            _mediaSizes.Remove(deviceFileName);
        }
    }

    private bool HasSpaceFor(long transferBytes)
        => MediaUsedBytes + transferBytes + MediaCapacityPaddingBytes <= MediaCapacityBytes;

    /// <summary>Deletes a stored media file on the panel via the file_remove command (the RK
    /// firmware exposes no adb), then drops it from the used-bytes accounting so the freed
    /// space is immediately available to the next upload's space check.</summary>
    public bool RemoveDeviceMedia(string deviceFileName)
    {
        if (string.IsNullOrEmpty(deviceFileName)) return false;
        var sn = PanelSerial;
        // file_remove is a locked command the panel silently ignores without the serial, and
        // SendReliable only confirms the bytes left the host - so a serial-less send would report
        // success while the file survives (and the caller would then drop the local thumbnail).
        // Fail instead until the connect-time device_info reply has given us the serial.
        if (string.IsNullOrEmpty(sn)) return false;
        var ok = SendReliable(TryxRkProtocol.BuildFileRemove(deviceFileName, sn));
        if (ok)
        {
            RecordMediaDeleted(deviceFileName);
            if (TryxKanaliData.DataDir() is { } kanaliDir) TryxKanaliStore.Unregister(kanaliDir, deviceFileName);
            // Re-fetch so the panel's list (and the per-file sizes) drop the removed file
            // authoritatively, not just via the local delta.
            SendReliable(TryxRkProtocol.BuildGetFileList(sn));
        }
        return ok;
    }

    // A file_list reply measured ~25 ms on the bench unit; this bounds the wait when the panel
    // is busy or silent.
    private const int MediaListRefreshTimeoutMs = 1000;

    /// <summary>Re-reads the panel's file list and waits for the reply, so media added or removed
    /// by another app (Kanali) shows on the next read. Skipped while a transfer holds the send
    /// gate or before the panel serial is known.</summary>
    public void RefreshMediaList() => RefreshMediaList(waitForGate: false);

    private void RefreshMediaList(bool waitForGate)
    {
        var transport = _transport;
        var sn = PanelSerial;
        if (transport is not { IsOpen: true } || string.IsNullOrEmpty(sn) || _importInProgress) return;
        if (waitForGate) Monitor.Enter(_txGate);
        else if (!Monitor.TryEnter(_txGate)) return;
        var before = transport.MediaListVersion;
        bool sent;
        try { sent = SendOnly(TryxRkProtocol.BuildGetFileList(sn)); }
        finally { Monitor.Exit(_txGate); }
        // A panel that has not answered a list on this transport is not waited on, so firmware
        // without get_file_list never stalls the caller.
        if (sent && before > 0) SpinWait.SpinUntil(() => transport.MediaListVersion != before, MediaListRefreshTimeoutMs);
    }

    // A 2240x1080 IDR measured 30-105 KB on the bench unit; past this head size a file is
    // treated as having no decodable first frame.
    private const int MaxThumbnailHeadBytes = 2 * 1024 * 1024;
    // A 64 KiB pull chunk measured ~40 ms on the bench unit.
    private const int FilePullTimeoutMs = 1000;

    // Link failures a file may hit before it is left without a thumbnail until restart.
    private const int MaxThumbnailTransientFailures = 3;

    private readonly object _thumbLock = new();
    private readonly Queue<(string Name, string Key)> _thumbQueue = new();
    // name|size of files already tried, so one that yields no frame is not re-pulled on every
    // media poll; a transient pull failure removes its key so a later poll retries.
    private readonly HashSet<string> _thumbAttempted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _thumbTransientFailures = new(StringComparer.Ordinal);
    private bool _thumbWorkerRunning;

    /// <summary>Queues a thumbnail for a panel-stored file that has none, decoded from its first
    /// frame pulled off the panel. Runs in the background; the next media read returns it.</summary>
    public void QueueThumbnail(string deviceFileName)
    {
        if (!TryxThumbnailCache.IsSafeDeviceName(deviceFileName) || FfmpegResolver.Path is null) return;
        lock (_thumbLock)
        {
            var key = ThumbKey(deviceFileName);
            if (!_thumbAttempted.Add(key)) return;
            _thumbQueue.Enqueue((deviceFileName, key));
            if (_thumbWorkerRunning) return;
            _thumbWorkerRunning = true;
        }
        _ = Task.Run(DrainThumbnailQueue);
    }

    private string ThumbKey(string deviceFileName)
        => _transport?.MediaFileSizes.TryGetValue(deviceFileName, out var size) == true
            ? $"{deviceFileName}|{size}"
            : deviceFileName;

    private void DrainThumbnailQueue()
    {
        while (true)
        {
            (string Name, string Key) item;
            lock (_thumbLock)
            {
                if (!_thumbQueue.TryDequeue(out item))
                {
                    _thumbWorkerRunning = false;
                    return;
                }
            }
            try { GeneratePanelThumbnail(item.Name, item.Key); }
            catch (Exception ex) { ServiceLog.Warn($"[tryx] thumbnail for {item.Name} failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private void GeneratePanelThumbnail(string deviceFileName, string key)
    {
        var ffmpeg = FfmpegResolver.Path;
        if (ffmpeg is null || _disposed) return;
        var head = PullMediaHead(deviceFileName, out var transient);
        if (head is null)
        {
            if (transient)
            {
                lock (_thumbLock)
                {
                    _thumbTransientFailures.TryGetValue(key, out var failures);
                    _thumbTransientFailures[key] = ++failures;
                    if (failures < MaxThumbnailTransientFailures) _thumbAttempted.Remove(key);
                }
                return;
            }
            ServiceLog.Info($"[tryx] no thumbnail for {deviceFileName}: first frame not readable from the panel");
            return;
        }
        var mkv = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nexus-tryx-thumb-{Guid.NewGuid():N}.mkv");
        try
        {
            File.WriteAllBytes(mkv, TryxMediaHead.BuildMatroska(head.Keyframe, head.Width, head.Height));
            TryxThumbnailCache.Write(ffmpeg, mkv, deviceFileName);
            if (head.DurationSec > 0 && TryxThumbnailCache.ReadDuration(deviceFileName) <= 0)
            {
                TryxThumbnailCache.WriteDuration(deviceFileName, head.DurationSec);
            }
        }
        finally
        {
            try { File.Delete(mkv); } catch { /* best effort */ }
        }
    }

    /// <summary>Pulls <paramref name="deviceFileName"/> off the panel chunk by chunk until its first
    /// keyframe is complete; null if the panel refuses the pull or no keyframe fits the head cap.
    /// <paramref name="transient"/> is true when the link, not the file, failed. Takes the send
    /// gate per chunk so the heartbeat keeps running between chunks.</summary>
    internal TryxMediaHead.Head? PullMediaHead(string deviceFileName, out bool transient)
    {
        transient = true;
        var head = new MemoryStream();
        while (head.Length < MaxThumbnailHeadBytes)
        {
            var chunk = PullChunk(deviceFileName, head.Length);
            // One retry: a late error reply to an unrelated frame also ends a pending pull.
            if (chunk is not { Ok: true }) chunk = PullChunk(deviceFileName, head.Length);
            if (chunk is null) return null;
            transient = false;
            var c = chunk.Value;
            if (!c.Ok || c.Data.Length == 0 || c.Offset != head.Length) return null;
            TryxRkProtocol.UnmaskPulledData(c.Data, c.Offset);
            head.Write(c.Data);
            var parsed = TryxMediaHead.Parse(head.GetBuffer().AsSpan(0, (int)head.Length));
            if (parsed is not null) return parsed;
            if (head.Length >= c.FileSize) return null;
            transient = true;
        }
        transient = false;
        return null;
    }

    private TryxMediaList.FilePullChunk? PullChunk(string deviceFileName, long offset)
    {
        lock (_txGate)
        {
            var transport = _transport;
            if (_disposed || transport is not { IsOpen: true }) return null;
            try { return transport.PullFileChunk(deviceFileName, offset, FilePullTimeoutMs); }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[tryx] pull {deviceFileName} failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return null;
            }
        }
    }

    public bool EnsureConnected()
    {
        if (_disposed) return false;
        if (IsConnected) return true;
        lock (_lock)
        {
            if (IsConnected) return true;
            foreach (var port in _discovery.Discover())
            {
                try
                {
                    var t = _transportFactory(port);
                    _transport = t;
                    State.Serial = port.Serial;
                    State.AdbSerial = port.AdbSerial;
                    State.PortName = port.PortName;
                    State.ProductId = port.ProductId;
                    State.ModelName = TryxPanoramaProtocol.GetModelName(port.ProductId);
                    State.LastConnectedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    ServiceLog.Info($"[tryx] connected to {port.PortName} (serial={port.Serial}, adb={port.AdbSerial})");
                    if (ApplyInitialConfig(t)) return true;
                    // The panel opened but its first writes stalled - present on the bus
                    // but not draining (mid re-enumeration). Drop this handle, and with it
                    // the timed-out write's abandoned overlapped I/O, so the next tick's
                    // discovery retries a clean transport instead of a second write
                    // colliding with the in-flight one on the same handle.
                    try { t.Dispose(); } catch { /* best effort */ }
                    _transport = null;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[tryx] open {port.PortName} failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            return false;
        }
    }

    // Sends the persisted brightness on fresh connect so the panel picks it up
    // without requiring a dashboard interaction. Called only from inside the
    // EnsureConnected lock after _transport is set; uses the transport reference
    // directly to avoid re-entering EnsureConnected.
    // Returns false if any initial write stalled or failed: the panel opened but is not
    // write-ready (mid re-enumeration). The caller drops the transport then, so a timed-out
    // write's abandoned overlapped I/O is aborted by the handle close instead of left in
    // flight for the next write to collide with on the same handle.
    private bool ApplyInitialConfig(ITryxPanoramaTransport transport)
    {
        try
        {
            // A panel that attaches or re-enumerates while locked joins the sleep before its
            // first write, so the remembered enabled state never reaches the wire.
            lock (_lockGate)
            {
                var sleeping = _lockSleep?.IsSleeping(LockKey, LockSetting) == true;
                TestHookInInitialConfigDecision?.Invoke();
                if (sleeping)
                {
                    _lockDimmed = true;
                }
                // A reconnect starts from the settled state, not from half a ramp.
                CancelFade();
                _offOnWire = !Wire(State.ScreenEnabled);
            }
            transport.Write(TryxRkProtocol.BuildConfig(Wire(State.ScreenEnabled), State.Brightness));
            // Bootstrap the device serial: get_file_list needs the panel's serial_number (locked),
            // which we only learn from device_info. Request it here; once the drain parses the
            // reply, the heartbeat sends get_file_list so the media list + used-bytes populate
            // (a warm reconnect doesn't re-trigger the unprompted cold-boot list push).
            _fileListRequested = false;
            ResetCoolerSession();
            transport.Write(TryxRkProtocol.BuildGetDeviceInfo());
            // Re-assert the last preset wallpaper so a reconnect (service restart, or the
            // panel's own re-enumeration) restores the picture instead of leaving the panel
            // black. A custom clip lives only on the panel / behind a re-transfer, so it is
            // not re-pushed here - only built-in presets, which are one cheap selection.
            if (!string.IsNullOrEmpty(State.CurrentMedia) && !State.CurrentMediaIsCustom)
            {
                transport.Write(TryxRkProtocol.BuildPreset(State.CurrentMedia, Wire(State.ScreenEnabled), State.Brightness));
            }
            if (_overlay.Items.Count > 0)
            {
                transport.Write(TryxRkProtocol.BuildOverlay(
                    BuildOverlayLines(), BuildOverlayPositions(), ParseHexColorRgb(_overlay.Color),
                    _overlay.Font, _overlay.Size, _overlay.Align));
            }
            _slideshow.Rearm(State.CurrentMedia, NowMs());
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[tryx] initial config apply failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public void Disconnect()
    {
        lock (_lock)
        {
            try { _transport?.Dispose(); } catch { /* best effort */ }
            _transport = null;
        }
        _fps.SetDemand(FpsDemandSource, false);
    }

    public bool SendConn()
        => SendOnly(TryxRkProtocol.BuildHeartbeat());

    /// <summary>One heartbeat tick. Skips entirely if a file transfer holds the send gate,
    /// so no control frame lands between the transfer's BEGIN/DATA/COMMIT frames.</summary>
    public void SendHeartbeatTick()
    {
        _fps.SetDemand(FpsDemandSource, OverlayUsesFps());
        if (!Monitor.TryEnter(_txGate)) return;
        try
        {
            SendConn();
            if (!_importInProgress)
            {
                SendStateAll();
                AdvanceSlideshowIfDue();
            }
            // Once the connect-time device_info reply has given us the panel serial, request the
            // media list once for this connection (the panel only pushes it unprompted on a cold
            // boot). SendOnly re-enters _txGate (reentrant).
            if (!_fileListRequested && !_importInProgress)
            {
                var sn = PanelSerial;
                if (!string.IsNullOrEmpty(sn))
                {
                    _fileListRequested = true;
                    SendOnly(TryxRkProtocol.BuildGetFileList(sn));
                }
            }
            if (!_importInProgress) TickCooler();
            State.LastFrameMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        finally
        {
            Monitor.Exit(_txGate);
        }
    }

    private bool OverlayUsesFps() => _overlay.Items.Any(i => i.Device == "fps");

    // Runs on the heartbeat under _txGate. SelectCustomMedia re-arms the hold on success;
    // on failure the pacer has already pushed the deadline out one interval. A select turns
    // the screen on, so a screen the user switched off is left alone.
    private void AdvanceSlideshowIfDue()
    {
        if (!State.ScreenEnabled || _lockDimmed || FadeActive) return;
        var next = _slideshow.Tick(ListCustomMedia, NowMs());
        if (next is null) return;
        if (!SelectCustomMedia(next))
        {
            ServiceLog.Warn($"[tryx] slideshow: select {next} failed");
        }
    }

    public bool SendStateAll()
    {
        if (_overlay.Items.Count == 0)
        {
            return true;
        }
        return SendOnly(TryxRkProtocol.BuildOverlay(
            BuildOverlayLines(), BuildOverlayPositions(), ParseHexColorRgb(_overlay.Color),
            _overlay.Font, _overlay.Size, _overlay.Align));
    }

    public bool SetEnabled(bool enable)
    {
        // Screen on/off rides the same f200.f5 config as brightness (f5.f1 = enable);
        // carry the current brightness so turning the screen back on restores it.
        // An explicit choice ends any lock ramp: its next step must not undo it.
        // The send and the state update stay under the send gate, so a ramp step or a resend cannot
        // land between them and undo the choice.
        lock (_txGate)
        {
            CancelFade();
            var ok = SendReliable(TryxRkProtocol.BuildConfig(Wire(enable), State.Brightness));
            if (ok)
            {
                _offOnWire = !Wire(enable);
                State.ScreenEnabled = enable;
                // The clip that comes back with the screen gets a full hold.
                if (enable) _slideshow.Rearm(State.CurrentMedia, NowMs());
            }
            return ok;
        }
    }

    public bool SetBrightness(int brightness)
    {
        var clamped = Math.Clamp(brightness, 0, 100);
        var ok = SendReliable(TryxRkProtocol.BuildConfig(Wire(State.ScreenEnabled), clamped));
        if (ok)
        {
            State.Brightness = clamped;
            _configStore.Update(s => s.Tryx.Brightness = clamped);
        }
        return ok;
    }

    /// <summary><paramref name="wallpaperMedia"/> is the panel's built-in wallpaper
    /// filename (TryxRkProtocol.PresetMediaFile). Screen state and brightness ride
    /// along so the panel keeps them.</summary>
    public bool SetPreset(string wallpaperMedia)
    {
        // Send, state and re-arm stay under the gate so a heartbeat slideshow advance
        // cannot land between them and leave State pointing at a clip the panel left.
        lock (_txGate)
        {
            var ok = SendReliable(TryxRkProtocol.BuildPreset(wallpaperMedia, Wire(State.ScreenEnabled), State.Brightness));
            if (ok)
            {
                State.CurrentMedia = wallpaperMedia;
                State.CurrentMediaIsCustom = false;
                _configStore.Update(s =>
                {
                    s.Tryx.CurrentMedia = wallpaperMedia;
                    s.Tryx.CurrentMediaIsCustom = false;
                });
                _slideshow.Rearm(wallpaperMedia, NowMs());
            }
            return ok;
        }
    }

    public bool SetOverlay(TryxOverlayConfig overlay)
    {
        _overlay.Items = overlay.Items;
        // Only assert fps demand while connected: the heartbeat re-asserts it each
        // tick once a panel attaches, so a disconnected edit here must not start ETW
        // capture that nothing on the panel consumes. Disconnect() clears it.
        _fps.SetDemand(FpsDemandSource, OverlayUsesFps() && IsConnected);
        _overlay.Color = overlay.Color;
        _overlay.Align = overlay.Align;
        _overlay.Filter = overlay.Filter;
        _overlay.Opacity = overlay.Opacity;
        _overlay.Font = overlay.Font;
        _overlay.Size = overlay.Size;
        _overlay.Docked = overlay.Docked;
        _configStore.Update(s =>
        {
            s.Tryx.OverlayItems = ToSettingsItems(overlay.Items);
            s.Tryx.OverlayColor = overlay.Color;
            s.Tryx.OverlayAlign = overlay.Align;
            s.Tryx.OverlayFilter = overlay.Filter;
            s.Tryx.OverlayOpacity = overlay.Opacity;
            s.Tryx.OverlayFont = overlay.Font;
            s.Tryx.OverlaySize = overlay.Size;
            s.Tryx.OverlayDocked = overlay.Docked;
        });
        return SendReliable(TryxRkProtocol.BuildOverlay(
            BuildOverlayLines(), BuildOverlayPositions(), ParseHexColorRgb(overlay.Color),
            overlay.Font, overlay.Size, overlay.Align));
    }

    private static List<TryxOverlaySensorItem> ToRuntimeItems(List<TryxOverlaySensorItemSettings> saved)
    {
        var items = new List<TryxOverlaySensorItem>(saved.Count);
        foreach (var s in saved)
        {
            items.Add(new TryxOverlaySensorItem { SensorId = s.SensorId, Device = s.Device, Label = s.Label, X = s.X, Y = s.Y });
        }
        return items;
    }

    private static List<TryxOverlaySensorItemSettings> ToSettingsItems(List<TryxOverlaySensorItem> items)
    {
        var saved = new List<TryxOverlaySensorItemSettings>(items.Count);
        foreach (var i in items)
        {
            saved.Add(new TryxOverlaySensorItemSettings { SensorId = i.SensorId, Device = i.Device, Label = i.Label, X = i.X, Y = i.Y });
        }
        return saved;
    }

    // Panel surface is a fixed-size 2:1 display; every custom clip is transcoded to it.
    private const int PanelWidth = 2240;
    private const int PanelHeight = 1080;
    private const int PanelFps = 60;
    private static readonly TimeSpan TranscodeTimeout = TimeSpan.FromMinutes(5);

    private int _transferSession = 600000;
    // Held for the whole BEGIN..COMMIT burst; the heartbeat acquires the same gate so
    // no control frame lands mid-transfer (a per-frame transport lock is not enough).
    private readonly object _txGate = new();
    // Serializes imports so two concurrent uploads can't interleave transfers or race
    // the transfer flag on the single shared transport.
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private readonly TryxCloudCatalog _cloud = new();

    /// <summary>Transcodes <paramref name="localPath"/> to the panel's H.264 format, wraps it
    /// in the Tryx media container, streams it over the RK file-transfer protocol, and selects
    /// it. RK custom media is a directly playable "media" file (no encryption). Returns
    /// (ok, message); message is empty on success or on a pre-existing failure mode, and
    /// carries the stable <see cref="InsufficientSpaceMsg"/> marker when the capacity check
    /// rejects the transfer. <paramref name="label"/> is the name the library shows for it.</summary>
    public async Task<(bool Ok, string Msg)> ImportAndPlayVideoAsync(
        string localPath, string label, TryxVideoCrop? crop, int targetWidth, int targetHeight, CancellationToken ct)
    {
        var ffmpegPath = FfmpegResolver.Path;
        if (ffmpegPath is null)
        {
            ServiceLog.Error("[tryx] ffmpeg not found; cannot import video");
            return (false, "");
        }
        if (!EnsureConnected()) return (false, "");

        // Kanali's naming: <local timestamp>.mp4 for the clip, + .h264_WxH on the panel.
        var stamp = TryxKanaliStore.Timestamp(DateTime.Now);
        var deviceFileName = $"{stamp}.mp4.h264_{PanelWidth}x{PanelHeight}";
        var mp4Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nexus-tryx-{Guid.NewGuid():N}.mp4");
        await _importGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await TranscodeToPanelMp4Async(ffmpegPath, localPath, crop, mp4Path, ct))
            {
                return (false, "");
            }
            // Bundled ffmpeg has only the mp4 muxer; extract the Annex-B stream ourselves.
            var mp4 = await File.ReadAllBytesAsync(mp4Path, ct);
            byte[] h264;
            try { h264 = Mp4AnnexB.Convert(mp4); }
            catch (Exception ex)
            {
                ServiceLog.Error($"[tryx] MP4->Annex-B conversion failed: {ex.GetType().Name}: {ex.Message}");
                return (false, "");
            }
            var frames = CountH264Frames(h264);
            var container = TryxRkProtocol.WrapMediaContainer(h264, PanelFps, PanelWidth, PanelHeight, frames, TryxRkProtocol.MediaHeaderMagic);

            if (!HasSpaceFor(container.Length))
            {
                return (false, InsufficientSpaceMsg);
            }
            if (!SendFileTransfer(deviceFileName, container, fileType: "media", ct, out _))
            {
                return (false, "");
            }
            RecordMediaUpload(deviceFileName, container.Length);
            // Stored before the panel switch is published, so the preview never asks for a missing clip.
            TryxMediaStore.SaveCopy(mp4Path, deviceFileName);
            TryxThumbnailCache.WriteLabel(deviceFileName, label);
            // The library lists the panel's own file list once received; re-read it so the new
            // file joins it (and the slideshow) without waiting for a media poll.
            RefreshMediaList(waitForGate: true);
            // Same f200 config that selects a built-in preset; wallpaper = the pushed file.
            if (!SendReliable(TryxRkProtocol.BuildPreset(deviceFileName, Wire(State.ScreenEnabled), State.Brightness)))
            {
                return (false, "");
            }
            State.CurrentMedia = deviceFileName;
            State.CurrentMediaIsCustom = true;
            State.ScreenEnabled = true;
            _configStore.Update(s => { s.Tryx.CurrentMedia = deviceFileName; s.Tryx.CurrentMediaIsCustom = true; });

            // Thumbnail from the cropped transcode, so it matches what the panel plays.
            try { TryxThumbnailCache.Write(ffmpegPath, mp4Path, deviceFileName); } catch { }
            var durationSec = 0.0;
            try { durationSec = TryxThumbnailCache.ProbeDuration(ffmpegPath, localPath); } catch { }
            try { TryxThumbnailCache.WriteDuration(deviceFileName, durationSec); } catch { }
            if (TryxKanaliData.DataDir() is { } kanaliDir)
            {
                RegisterWithKanali(kanaliDir, ffmpegPath, localPath, mp4Path, durationSec, stamp, deviceFileName);
            }
            // After the duration sidecar exists, so a "finish videos" hold is computed from it.
            _slideshow.Rearm(deviceFileName, NowMs());
            return (true, "");
        }
        finally
        {
            try { File.Delete(mp4Path); } catch { /* best effort */ }
            _importGate.Release();
        }
    }

    // Kanali's upload bookkeeping: clip in kanali\media, a full-size PNG of the source's middle
    // frame in media\thumb, then the store.json entry that stops Kanali deleting the panel file.
    private void RegisterWithKanali(
        string kanaliDir, string ffmpegPath, string sourcePath, string clipMp4, double durationSec,
        string stamp, string deviceFileName)
    {
        try
        {
            var mediaPath = System.IO.Path.Combine(TryxKanaliStore.MediaDir(kanaliDir), stamp + ".mp4");
            Directory.CreateDirectory(TryxKanaliStore.MediaDir(kanaliDir));
            File.Copy(clipMp4, mediaPath, overwrite: true);
            var thumbDir = TryxKanaliStore.ThumbDir(kanaliDir);
            Directory.CreateDirectory(thumbDir);
            string? thumbPath = System.IO.Path.Combine(thumbDir, TryxKanaliStore.Timestamp(DateTime.Now) + ".png");
            if (!TryxThumbnailCache.ExtractFullFrameTo(ffmpegPath, sourcePath, durationSec / 2, thumbPath)) thumbPath = null;
            TryxKanaliStore.Register(kanaliDir, new TryxKanaliStore.Upload(
                mediaPath, thumbPath, new FileInfo(mediaPath).Length, deviceFileName, PanelSerial));
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[tryx] Kanali library copy of {deviceFileName} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Installs a cloud wallpaper the way Kanali does: the still-encrypted download is sent as
    /// a "tmp" file and the panel decrypts it into its preset storage (/userdata/default/), which
    /// Kanali never prunes. Returns (ok, message).</summary>
    public async Task<(bool Ok, string Msg)> InstallCloudMaterialAsync(int id, CancellationToken ct)
    {
        if (!EnsureConnected()) return (false, "Tryx Panorama not connected");
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"nexus-tryx-cloud-{id}-{Guid.NewGuid():N}.bin");
        await _importGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string installName;
            string wrappedKey;
            try
            {
                installName = await _cloud.DownloadEncryptedAsync(id, tmp, ct);
                wrappedKey = await _cloud.GetPanelWrappedSm4KeyAsync(ct);
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[tryx] cloud download failed for {id}: {ex.GetType().Name}: {ex.Message}");
                return (false, "download failed");
            }
            if (!HasSpaceFor(new FileInfo(tmp).Length))
            {
                return (false, InsufficientSpaceMsg);
            }
            if (!await InstallEncryptedPresetAsync(tmp, installName, wrappedKey, ct)) return (false, "install failed");
            _configStore.Update(s =>
            {
                if (!s.Tryx.InstalledCloudIds.Contains(id)) s.Tryx.InstalledCloudIds.Add(id);
            });
            return (true, "");
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            _importGate.Release();
        }
    }

    public Task<List<TryxCloudMaterial>> GetCloudCatalogAsync(CancellationToken ct)
        => _cloud.GetCatalogAsync("PANO_1011", ct);

    /// <summary>Whether cloud material <paramref name="id"/> is on the panel. The panel is the
    /// source of truth when it has reported its media list (a "download_{id}." file); otherwise
    /// falls back to our persisted record, so a fresh install self-heals once the panel lists it.</summary>
    public bool IsCloudInstalled(int id)
    {
        var prefix = $"download_{id}.";
        foreach (var name in AvailableMediaFilenames)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return _configStore.Load().Tryx.InstalledCloudIds.Contains(id);
    }

    public Task<System.IO.Stream?> OpenCloudCoverAsync(int id, CancellationToken ct)
        => _cloud.OpenCoverAsync(id, ct);

    // Kanali's interval between decrypt-job status queries.
    internal TimeSpan DecryptPollInterval { get; set; } = TimeSpan.FromSeconds(3);
    // Kanali polls without a bound; this caps a job the panel never finishes.
    private static readonly TimeSpan DecryptJobTimeout = TimeSpan.FromMinutes(5);
    private const int DecryptReplyTimeoutMs = 2000;

    /// <summary>Sends <paramref name="encryptedPath"/> (IV + SM4-CBC, as downloaded) as a "tmp" file,
    /// runs the panel's decrypt job into preset storage under <paramref name="deviceFileName"/>,
    /// then selects it. Caller holds the import gate.</summary>
    internal async Task<bool> InstallEncryptedPresetAsync(
        string encryptedPath, string deviceFileName, string wrappedSm4Key, CancellationToken ct)
    {
        if (!TryxThumbnailCache.IsSafeDeviceName(deviceFileName)) return false;
        if (!EnsureConnected()) return false;
        var bytes = await File.ReadAllBytesAsync(encryptedPath, ct);
        if (!HasSpaceFor(bytes.Length))
        {
            ServiceLog.Warn($"[tryx] install of {deviceFileName} rejected, insufficient panel space");
            return false;
        }
        if (!SendFileTransfer(deviceFileName, bytes, fileType: "tmp", ct, out var trackId)) return false;

        // The panel forgets a running job when it re-enumerates, so the job is tied to this transport.
        var jobTransport = _transport;
        var start = Request(
            TryxRkProtocol.BuildFileDecryptJobStart(trackId, deviceFileName, deviceFileName, "preset", wrappedSm4Key),
            replyField: 803, r => TryxMediaList.ParseDecryptJobStartStatus(r));
        if (start is not { Status: 0 } accepted)
        {
            ServiceLog.Error($"[tryx] decrypt job for {deviceFileName} not accepted (status {start?.Status.ToString(CultureInfo.InvariantCulture) ?? "no reply"})");
            return false;
        }
        var deadline = Environment.TickCount64 + (long)DecryptJobTimeout.TotalMilliseconds;
        while (true)
        {
            await Task.Delay(DecryptPollInterval, ct).ConfigureAwait(false);
            if (!ReferenceEquals(_transport, jobTransport) || jobTransport is not { IsOpen: true })
            {
                ServiceLog.Error($"[tryx] decrypt job for {deviceFileName} lost: the panel disconnected");
                return false;
            }
            var status = Request(
                TryxRkProtocol.BuildFileDecryptJobQuery(trackId, accepted.SessionId),
                replyField: 804, r => TryxMediaList.ParseDecryptJobStatus(r));
            if (status is { DecryptStatus: not 0 } failed)
            {
                ServiceLog.Error($"[tryx] decrypt of {deviceFileName} failed (decrypt status {failed.DecryptStatus})");
                return false;
            }
            if (status is { JobStatus: 0 }) break;
            if (status is { JobStatus: 1 } || Environment.TickCount64 > deadline)
            {
                ServiceLog.Error($"[tryx] decrypt job for {deviceFileName} lost (job status {status?.JobStatus.ToString(CultureInfo.InvariantCulture) ?? "no reply"})");
                return false;
            }
        }
        RecordMediaUpload(deviceFileName, bytes.Length);
        RefreshMediaList(waitForGate: true);
        lock (_txGate)
        {
            if (!SendReliable(TryxRkProtocol.BuildPreset(deviceFileName, Wire(State.ScreenEnabled), State.Brightness))) return false;
            State.CurrentMedia = deviceFileName;
            State.CurrentMediaIsCustom = false;
            _configStore.Update(s => { s.Tryx.CurrentMedia = deviceFileName; s.Tryx.CurrentMediaIsCustom = false; });
            _slideshow.Rearm(deviceFileName, NowMs());
        }
        return true;
    }

    // One request/reply exchange under the send gate; null when the panel does not answer.
    private T? Request<T>(byte[] frame, int replyField, Func<byte[], T?> parse, int timeoutMs = DecryptReplyTimeoutMs) where T : struct
    {
        var transport = _transport;
        if (transport is not { IsOpen: true }) return null;
        byte[]? reply;
        lock (_txGate)
        {
            try { reply = transport.Request(frame, replyField, timeoutMs); }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[tryx] request {replyField} failed: {ex.GetType().Name}: {ex.Message}");
                Disconnect();
                return null;
            }
        }
        return reply is null ? null : parse(reply);
    }

    // Streams a full file: BEGIN (f400 name+size) -> DATA (f401 chunks) -> COMMIT (f402 type).
    // Holds _txGate so the heartbeat skips its whole tick for the burst (Kanali sends none
    // mid-transfer; the receiving panel will not standby). Any failure or cancellation drops
    // the transport, because a partial transfer leaves the panel awaiting more chunks - a
    // re-enumeration resyncs it, and a stray control frame into a half-open session is worse
    // than a reconnect.
    private bool SendFileTransfer(string deviceFileName, byte[] payload, string fileType, CancellationToken ct, out uint session)
    {
        session = unchecked((uint)Interlocked.Increment(ref _transferSession));
        lock (_txGate)
        {
            var completed = false;
            try
            {
                if (!SendOnly(TryxRkProtocol.BuildFileBegin(session, deviceFileName, payload.Length))) return false;
                for (var offset = 0; offset < payload.Length; offset += TryxRkProtocol.FileChunkSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var len = Math.Min(TryxRkProtocol.FileChunkSize, payload.Length - offset);
                    if (!SendOnly(TryxRkProtocol.BuildFileChunk(session, payload.AsSpan(offset, len)))) return false;
                }
                completed = SendOnly(TryxRkProtocol.BuildFileCommit(session, fileType));
                return completed;
            }
            finally
            {
                if (!completed) Disconnect();
            }
        }
    }

    private async Task<bool> TranscodeToPanelMp4Async(
        string ffmpegPath, string input, TryxVideoCrop? crop, string outputMp4, CancellationToken ct)
    {
        // MediaX's filter graph: lanczos scale to full range, yuv420p, fps.
        var scale = $"scale={PanelWidth}:{PanelHeight}:flags=lanczos+accurate_rnd+full_chroma_inp:out_range=pc,format=yuv420p,fps={PanelFps}";
        var source = TryxKanaliEncode.ProbeSource(ffmpegPath, input);
        var vf = crop is { } c
            ? $"{CropRect.OrientationFilter(c.Rotate, c.Mirror)}crop=in_w*{F(c.W)}:in_h*{F(c.H)}:in_w*{F(c.X)}:in_h*{F(c.Y)},{scale}"
            : scale;
        var args = $"-nostdin -hide_banner -loglevel error -y -i \"{input}\" -an " +
                   $"-vf \"{vf}\" -c:v libx264 {TryxKanaliEncode.X264Options(source, PanelWidth, PanelHeight, PanelFps)} " +
                   $"-movflags +faststart \"{outputMp4}\"";
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
        });
        if (p is null) return false;
        // Bound the run and kill the child on cancel/timeout so it can't orphan (leaving
        // the output file locked); WaitForExitAsync/ReadToEnd only observe, they don't kill.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TranscodeTimeout);
        try
        {
            var err = await p.StandardError.ReadToEndAsync(timeoutCts.Token);
            await p.WaitForExitAsync(timeoutCts.Token);
            if (p.ExitCode != 0 || !File.Exists(outputMp4) || new FileInfo(outputMp4).Length == 0)
            {
                ServiceLog.Error($"[tryx] transcode failed (exit {p.ExitCode}): {err.Trim()}");
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            ServiceLog.Error("[tryx] transcode cancelled or timed out");
            return false;
        }
    }

    private static string F(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    // Counts H.264 access units by VCL NAL units (type 1 non-IDR, 5 IDR). x264 emits one
    // slice per frame by default, so this equals the frame count; used for the container's
    // frame-count header field.
    private static int CountH264Frames(ReadOnlySpan<byte> h264)
    {
        var frames = 0;
        for (var i = 0; i + 3 < h264.Length; i++)
        {
            if (h264[i] == 0 && h264[i + 1] == 0 && h264[i + 2] == 1)
            {
                var nalType = h264[i + 3] & 0x1f;
                if (nalType is 1 or 5) frames++;
            }
        }
        return frames;
    }

    /// <summary>Ensures a local copy of <paramref name="deviceFileName"/> exists in the media store,
    /// pulling from the device via adb if necessary. Holds the import-pause for the duration
    /// of the pull so the heartbeat does not issue concurrent adb commands.</summary>
    public Task<bool> EnsureLocalCopyAsync(string deviceFileName, CancellationToken ct)
    {
        if (TryxMediaStore.Exists(deviceFileName))
        {
            return Task.FromResult(true);
        }

        var adbSerial = State.AdbSerial;
        if (string.IsNullOrEmpty(adbSerial))
        {
            return Task.FromResult(false);
        }

        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null)
        {
            return Task.FromResult(false);
        }

        ct.ThrowIfCancellationRequested();

        var destPath = TryxMediaStore.Path(deviceFileName);
        _importInProgress = true;
        try
        {
            Directory.CreateDirectory(TryxMediaStore.StoreDir);
            RunAdb(adbPath, $"-s {adbSerial} pull /sdcard/pcMedia/{deviceFileName} \"{destPath}\"",
                60_000, out _, out _);

            if (!File.Exists(destPath))
            {
                return Task.FromResult(false);
            }

            var ffmpegPath = FfmpegResolver.Path;
            if (ffmpegPath is not null && !File.Exists(TryxThumbnailCache.ThumbPath(deviceFileName)))
            {
                try { TryxThumbnailCache.Write(ffmpegPath, destPath, deviceFileName); } catch { }
                try
                {
                    var dur = TryxThumbnailCache.ProbeDuration(ffmpegPath, destPath);
                    TryxThumbnailCache.WriteDuration(deviceFileName, dur);
                }
                catch { }
            }

            return Task.FromResult(true);
        }
        finally
        {
            _importInProgress = false;
        }
    }

    /// <summary>Selects an already-on-device custom file without re-pushing it. The RK
    /// firmware selects any on-panel media (preset or custom) with the same f200 config.</summary>
    public bool SelectCustomMedia(string deviceFileName)
    {
        if (!TryxThumbnailCache.IsSafeDeviceName(deviceFileName)) return false;
        lock (_txGate)
        {
            if (!SendReliable(TryxRkProtocol.BuildPreset(deviceFileName, screenOn: Wire(true), State.Brightness))) return false;
            State.CurrentMedia = deviceFileName;
            State.CurrentMediaIsCustom = true;
            State.ScreenEnabled = true;
            _configStore.Update(s => { s.Tryx.CurrentMedia = deviceFileName; s.Tryx.CurrentMediaIsCustom = true; });
            _slideshow.Rearm(deviceFileName, NowMs());
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }

    // ── Internals ──

    /// <summary>
    /// Builds the STATE-all sensor JSON from live ISensorProvider reads.
    /// Sensor name patterns match LHM's naming conventions (Windows path);
    /// Mac/Linux providers return subsets - missing sensors produce 0.
    /// </summary>
    internal string BuildLiveSensorJsonForTest() => BuildLiveSensorJson();

    /// <summary>Sensor values read for the STATE-all JSON and the overlay stat mapper,
    /// so both consume the same derivation instead of duplicating sensor lookups.</summary>
    private readonly struct TryxSensorReadout
    {
        public int CpuTemp { get; init; }
        public int CpuLoad { get; init; }
        public int CpuClock { get; init; }
        public int CpuPower { get; init; }
        public int CpuVoltage { get; init; }
        public int GpuTemp { get; init; }
        public int GpuLoad { get; init; }
        public int GpuClock { get; init; }
        public int GpuPower { get; init; }
        public int GpuVoltage { get; init; }
        public int MemTotal { get; init; }
        public int MemUsed { get; init; }
        public int MemLoad { get; init; }
        public int MemClock { get; init; }
        public int MoboTemp { get; init; }
        public int PchTemp { get; init; }
    }

    private TryxSensorReadout ReadSensors()
    {
        var cpuSensors = _sensors.GetCpuSensors();
        var gpuSensors = GetPrimaryGpuSensors();
        var memSensors = _sensors.GetMemorySensors();
        var moboSensors = _sensors.GetMotherboardSensors();

        var cpuTemp = RoundSensor(cpuSensors, "Temperature", "Package");
        var cpuLoad = RoundSensor(cpuSensors, "Load", "CPU Total");
        var cpuClock = RoundSensor(cpuSensors, "Clock", "Core Max");
        if (cpuClock == 0) cpuClock = MaxClock(cpuSensors, "Core");
        var cpuPower = RoundSensor(cpuSensors, "Power", "Package");
        var cpuVoltage = RoundSensor(cpuSensors, "Voltage", "VCore");

        // "Core" matches "GPU Core" on NVIDIA/AMD; fall back to first Temperature
        // sensor for GPUs that name the die temperature differently.
        var gpuTemp = (int)Math.Round(
            (FindSensor(gpuSensors, "Temperature", "Core")
             ?? FindSensor(gpuSensors, "Temperature", null))?.Value ?? 0f);
        var gpuLoad = RoundSensor(gpuSensors, "Load", "Core");
        var gpuClock = RoundSensor(gpuSensors, "Clock", "Core");
        var gpuPower = RoundSensor(gpuSensors, "Power", null);
        var gpuVoltage = (int)Math.Round(
            (FindSensor(gpuSensors, "Voltage", "Core")
             ?? FindSensor(gpuSensors, "Voltage", null))?.Value ?? 0f);

        // Memory Used/Available are Data type in GB; convert to integer GB.
        var memUsed = (int)Math.Round(FindSensor(memSensors, "Data", "Used")?.Value ?? 0f);
        var memAvail = (int)Math.Round(FindSensor(memSensors, "Data", "Available")?.Value ?? 0f);
        var memTotal = memUsed + memAvail;
        var memLoad = RoundSensor(memSensors, "Load", null);
        // AMD exposes RAM clock as "Memory" under CPU Clock sensors; Intel/other
        // may expose it under motherboard sensors. Fall back to 0 when unavailable.
        var memClock = (int)Math.Round(
            (FindSensor(cpuSensors, "Clock", "Memory")
             ?? FindSensor(moboSensors, "Clock", "Memory"))?.Value ?? 0f);

        var moboTemp = RoundSensor(moboSensors, "Temperature", null);
        var pchTemp = RoundSensor(moboSensors, "Temperature", "PCH");

        return new TryxSensorReadout
        {
            CpuTemp = cpuTemp,
            CpuLoad = cpuLoad,
            CpuClock = cpuClock,
            CpuPower = cpuPower,
            CpuVoltage = cpuVoltage,
            GpuTemp = gpuTemp,
            GpuLoad = gpuLoad,
            GpuClock = gpuClock,
            GpuPower = gpuPower,
            GpuVoltage = gpuVoltage,
            MemTotal = memTotal,
            MemUsed = memUsed,
            MemLoad = memLoad,
            MemClock = memClock,
            MoboTemp = moboTemp,
            PchTemp = pchTemp,
        };
    }

    private string BuildLiveSensorJson()
    {
        var r = ReadSensors();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var sb = new StringBuilder(512);
        sb.Append("{\"network\":{\"upload\":0,\"download\":0},");
        sb.Append("\"memory\":{\"total\":").Append(r.MemTotal)
          .Append(",\"used\":").Append(r.MemUsed)
          .Append(",\"load\":").Append(r.MemLoad)
          .Append(",\"temperature\":0,\"speed\":").Append(r.MemClock).Append("},");
        sb.Append("\"cpu\":{\"load\":").Append(r.CpuLoad)
          .Append(",\"usage\":").Append(r.CpuLoad)
          .Append(",\"temperature\":").Append(r.CpuTemp)
          .Append(",\"speedAverage\":").Append(r.CpuClock)
          .Append(",\"power\":").Append(r.CpuPower)
          .Append(",\"voltage\":").Append(r.CpuVoltage).Append("},");
        sb.Append("\"gpu\":{\"load\":").Append(r.GpuLoad)
          .Append(",\"temperature\":").Append(r.GpuTemp)
          .Append(",\"fan\":0,\"speed\":").Append(r.GpuClock)
          .Append(",\"power\":").Append(r.GpuPower)
          .Append(",\"voltage\":").Append(r.GpuVoltage).Append("},");
        sb.Append("\"disk\":{\"total\":0,\"used\":0,\"load\":0,\"activity\":0,\"temperature\":0,\"readSpeed\":0,\"writeSpeed\":0},");
        sb.Append("\"fans\":[{\"onBoard\":true,\"type\":\"Fan\",\"name\":\"Fan CPU\",\"value\":0}],");
        sb.Append("\"motherboard\":{\"temperature\":").Append(r.MoboTemp)
          .Append(",\"pchTemperature\":").Append(r.PchTemp).Append("},");
        sb.Append("\"timestamp\":").Append(ts).Append('}');
        return sb.ToString();
    }

    private List<TryxOverlayLine> BuildOverlayLines()
    {
        var items = _overlay.Items;
        var sources = GatherOverlaySources(items);
        var lines = new List<TryxOverlayLine>(items.Count);
        foreach (var item in items)
        {
            lines.Add(new TryxOverlayLine(item.Label, ResolveSensorValue(item.Device, item.SensorId, sources)));
        }
        return lines;
    }

    /// <summary>Gathers the extras snapshot and network aggregate once per frame, only when an item reads a category that needs them.</summary>
    private SensorSnapshotSources GatherOverlaySources(IReadOnlyList<TryxOverlaySensorItem> items)
    {
        var needExtras = false;
        var needNetwork = false;
        foreach (var item in items)
        {
            if (SensorSnapshotResolver.CategoryUsesExtras(item.Device))
            {
                needExtras = true;
                needNetwork |= item.Device == "network";
            }
        }
        if (!needExtras)
        {
            return default;
        }
        var extras = _sensors.GetSensorExtras();
        return new SensorSnapshotSources(
            extras,
            NetworkSensors: needNetwork ? SensorSnapshotResolver.BuildNicNetworkSensors(extras.Nics) : null);
    }

    private List<(double X, double Y)> BuildOverlayPositions()
    {
        var positions = new List<(double, double)>(_overlay.Items.Count);
        foreach (var item in _overlay.Items)
        {
            positions.Add((item.X, item.Y));
        }
        return positions;
    }

    /// <summary>Looks up <paramref name="sensorId"/> within <paramref name="device"/>'s
    /// live sensor group and formats it by its <see cref="HardwareSensor.Type"/>; "--"
    /// if the group is unknown or the id isn't currently reported.</summary>
    private string ResolveSensorValue(string device, string sensorId, SensorSnapshotSources sources = default)
    {
        // "fps" needs IFpsProvider, which SensorSnapshotResolver does not depend
        // on, so it stays resolved here directly; every other device routes
        // through the shared resolver (also used by the deck monitoring tiles).
        var sensor = device == "fps"
            ? FindById(_fps.GetComponent().Sensors, sensorId)
            : SensorSnapshotResolver.Resolve(_sensors, device, sensorId, sources);
        return sensor is null ? "--" : FormatSensorValue(sensor);
    }

    private static HardwareSensor? FindById(IReadOnlyList<HardwareSensor> sensors, string id)
    {
        for (var i = 0; i < sensors.Count; i++)
        {
            if (sensors[i].Id == id)
            {
                return sensors[i];
            }
        }
        return null;
    }

    // Units match the monitoring widgets' own formatting for each LHM sensor type
    // (LibreHardwareSensorProvider.MapSensorType/MapUnits); unmapped types fall
    // back to the unformatted value with no unit. Data (e.g. memory used/available)
    // is GB-scale; SmallData (e.g. GPU VRAM) is MB-scale - these are distinct LHM
    // units, not interchangeable formatting of the same magnitude. Framerate/FrameTime
    // come from IFpsProvider, not LHM; the web Tryx overlay picker formats "fps"
    // sensors identically, so these two cases must match it exactly.
    private static string FormatSensorValue(HardwareSensor sensor) => sensor.Type switch
    {
        "Temperature" => $"{(int)Math.Round(sensor.Value)}°C",
        "Load" => $"{(int)Math.Round(sensor.Value)}%",
        "Clock" or "Frequency" => $"{(int)Math.Round(sensor.Value)}MHz",
        "Voltage" => $"{sensor.Value.ToString("0.00", CultureInfo.InvariantCulture)}V",
        "Data" => $"{sensor.Value.ToString("0.0", CultureInfo.InvariantCulture)}GB",
        "SmallData" => $"{(int)Math.Round(sensor.Value)}MB",
        "Power" => $"{(int)Math.Round(sensor.Value)}W",
        "Fan" => $"{(int)Math.Round(sensor.Value)}RPM",
        "Throughput" or "Rate" => FormatThroughput(sensor.Value),
        "Framerate" => $"{(int)Math.Round(sensor.Value)}fps",
        "FrameTime" => $"{sensor.Value.ToString("0.0", CultureInfo.InvariantCulture)}ms",
        _ => sensor.Value.ToString(CultureInfo.InvariantCulture),
    };

    // LHM Throughput sensors (network up/down) report bytes/sec; appending "MB/s" to the raw
    // value misreads it by orders of magnitude. Scale to the largest fitting unit, matching the
    // web monitoring widgets' formatter (panel/widgets/monitoring/page/shared.ts); no space
    // before the unit, like the other overlay values.
    private static string FormatThroughput(float bytesPerSec) =>
        bytesPerSec >= 1024f * 1024f
            ? $"{(bytesPerSec / 1024f / 1024f).ToString("0.0", CultureInfo.InvariantCulture)}MB/s"
            : bytesPerSec >= 1024f
                ? $"{(bytesPerSec / 1024f).ToString("0.0", CultureInfo.InvariantCulture)}KB/s"
                : $"{(int)Math.Round(bytesPerSec)}B/s";

    private static int ParseHexColorRgb(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length < 6)
        {
            return 0;
        }
        if (!byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, null, out var r)) { r = 0; }
        if (!byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, null, out var g)) { g = 0; }
        if (!byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, null, out var b)) { b = 0; }
        return (r << 16) | (g << 8) | b;
    }

    private IReadOnlyList<HardwareSensor> GetPrimaryGpuSensors()
    {
        // Match MonitoringBroadcaster: discrete-first ordering; use the first discrete GPU.
        var gpus = _sensors.GetGpus();
        for (var i = 0; i < gpus.Count; i++)
        {
            if (!gpus[i].Integrated)
            {
                return gpus[i].Sensors;
            }
        }
        return gpus.Count > 0 ? gpus[0].Sensors : Array.Empty<HardwareSensor>();
    }

    private static int RoundSensor(IReadOnlyList<HardwareSensor> sensors, string type, string? nameContains)
        => (int)Math.Round(FindSensor(sensors, type, nameContains)?.Value ?? 0f);

    // Cores park independently; max is the headline clock across all matching sensors.
    private static int MaxClock(IReadOnlyList<HardwareSensor> sensors, string nameContains)
    {
        var max = 0f;
        for (var i = 0; i < sensors.Count; i++)
        {
            var s = sensors[i];
            if (string.Equals(s.Type, "Clock", StringComparison.OrdinalIgnoreCase)
                && s.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0
                && s.Value > max)
            {
                max = s.Value;
            }
        }
        return (int)Math.Round(max);
    }

    private static HardwareSensor? FindSensor(IReadOnlyList<HardwareSensor> sensors, string type, string? nameContains)
    {
        for (var i = 0; i < sensors.Count; i++)
        {
            var s = sensors[i];
            if (!string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (nameContains is null ||
                s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }
        return null;
    }

    // ── Cooler: the small fan on the pump head ──
    // Writes ride the heartbeat and go out only when the target changes, as Kanali's Smart Mode
    // does, so a curve engine driving every second costs one frame per change. The panel's own fan
    // config is read before Nexus first writes and is written back on release. The cooler fields
    // below are touched under _txGate, except the target and baseline, which take _coolerLock.

    // Kanali reads the speeds every 1.5 s; one request per 2 s keeps the heartbeat tick short.
    internal TimeSpan CoolerPollInterval { get; set; } = TimeSpan.FromSeconds(2);
    // Kanali waits 300 ms for the status notification before reading its cache.
    private const int CoolerReplyTimeoutMs = 300;
    // Unanswered status requests before polling stops for this connection, so a firmware without
    // the cooler does not cost a reply timeout on every poll. Unanswered config reads before the
    // fan is driven without the panel's own config to hand back.
    private const int CoolerMaxMisses = 3;
    // How long a release waits for a tick or a control write in flight to let go of _txGate.
    private const int CoolerReleaseGateWaitMs = 2000;

    private readonly object _coolerLock = new();
    private int? _coolerTarget;
    private TryxMediaList.CoolerFanConfig? _coolerBaseline;
    private bool _coolerWritten;
    private bool _coolerRestorePending;
    private int _coolerConfigMisses;
    private int? _coolerSentPower;
    private int _coolerMisses;
    private long _coolerPollDueMs;

    /// <summary>True when the connected model has the software-driven small fan.</summary>
    public bool HasCooler => TryxPanoramaProtocol.HasCoolerFan(State.ProductId);

    /// <summary>The fixed power Nexus drives the small fan at, or null while the panel keeps its own config.</summary>
    public int? CoolerFanTarget
    {
        get { lock (_coolerLock) return _coolerTarget; }
    }

    /// <summary>Drives the small fan at <paramref name="percent"/> from the next heartbeat tick on.</summary>
    public void SetCoolerFanTarget(int percent)
    {
        lock (_coolerLock) _coolerTarget = Math.Clamp(percent, 0, 100);
    }

    /// <summary>Hands the small fan back the config the panel had before Nexus drove it. Sent at once
    /// rather than on the next tick: ReleaseAll also runs at shutdown, after the last tick. Waits a
    /// bounded time for a write in flight instead of skipping, since at shutdown no tick follows.</summary>
    public void ReleaseCoolerFan()
    {
        lock (_coolerLock) _coolerTarget = null;
        if (!IsConnected) return;
        if (!Monitor.TryEnter(_txGate, CoolerReleaseGateWaitMs))
        {
            ServiceLog.Warn("[tryx] panel busy, VRM fan release left to the next heartbeat tick");
            return;
        }
        try { ApplyCoolerTarget(); }
        finally { Monitor.Exit(_txGate); }
    }

    // A fresh connection asks for the speeds again and re-sends the target, since the panel may
    // have reset its fan while it re-enumerated. The last readings stay until new ones arrive.
    private void ResetCoolerSession()
    {
        _coolerConfigMisses = 0;
        _coolerSentPower = null;
        _coolerMisses = 0;
        _coolerPollDueMs = 0;
    }

    private void TickCooler()
    {
        if (!HasCooler) return;
        // Both reads carry the panel serial from the connect-time device_info reply. No write goes
        // out while the config read may still be answered, so the baseline is the panel's own
        // config: a read that times out is retried on the next ticks, and only after
        // CoolerMaxMisses of them is the fan driven without one.
        var sn = PanelSerial;
        if (string.IsNullOrEmpty(sn)) return;
        bool needBaseline;
        lock (_coolerLock) needBaseline = _coolerBaseline is null && !_coolerWritten;
        var holdWrites = false;
        if (needBaseline && _coolerConfigMisses < CoolerMaxMisses)
        {
            holdWrites = !ReadCoolerBaseline(sn) && _coolerConfigMisses < CoolerMaxMisses;
        }
        PollCoolerStatus(sn);
        if (!holdWrites) ApplyCoolerTarget();
    }

    private bool ReadCoolerBaseline(string sn)
    {
        var config = Request(TryxRkProtocol.BuildGetCoolerConfig(sn), TryxRkProtocol.CoolerConfigReplyField,
            r => TryxMediaList.ParseCoolerFanConfig(r), CoolerReplyTimeoutMs);
        if (config is not { } c)
        {
            if (++_coolerConfigMisses == CoolerMaxMisses)
            {
                ServiceLog.Warn($"[tryx] panel left {CoolerMaxMisses} fan config requests unanswered; a released fan keeps the last power Nexus set");
            }
            return false;
        }
        lock (_coolerLock) _coolerBaseline = c;
        State.CoolerFanPower ??= c.FixedPower;
        return true;
    }

    private void PollCoolerStatus(string sn)
    {
        var now = NowMs();
        if (_coolerMisses >= CoolerMaxMisses || now < _coolerPollDueMs) return;
        _coolerPollDueMs = now + (long)CoolerPollInterval.TotalMilliseconds;
        var status = Request(TryxRkProtocol.BuildGetCoolerStatus(sn), TryxRkProtocol.CoolerStatusReplyField,
            r => TryxMediaList.ParseCoolerStatus(r), CoolerReplyTimeoutMs);
        if (status is not { } s)
        {
            if (++_coolerMisses == CoolerMaxMisses)
            {
                ServiceLog.Warn($"[tryx] panel left {CoolerMaxMisses} cooler status requests unanswered; fan readings stop until it reconnects");
            }
            return;
        }
        _coolerMisses = 0;
        if (s.FanRpm <= TryxRkProtocol.CoolerFanRpmCeiling) State.CoolerFanRpm = s.FanRpm;
        State.CoolerPumpRpm = s.PumpRpm;
    }

    private void ApplyCoolerTarget()
    {
        int? target;
        TryxMediaList.CoolerFanConfig? baseline;
        lock (_coolerLock)
        {
            target = _coolerTarget;
            baseline = _coolerBaseline;
        }
        if (target is { } t)
        {
            if (_coolerSentPower == t || !SendOnly(TryxRkProtocol.BuildCoolerFixedPower(t))) return;
            _coolerSentPower = t;
            _coolerRestorePending = true;
            lock (_coolerLock) _coolerWritten = true;
            State.CoolerFanPower = t;
            return;
        }
        if (!_coolerRestorePending) return;
        if (baseline is { } b && !SendOnly(TryxRkProtocol.BuildCoolerConfig(b.Raw))) return;
        // Without a baseline there is nothing to hand back: the fan keeps the last power Nexus set.
        _coolerRestorePending = false;
        _coolerSentPower = null;
        if (baseline is { } restored) State.CoolerFanPower = restored.FixedPower;
    }

    private bool SendOnly(byte[] request)
    {
        // Every device write serializes on _txGate (reentrant), so a route-initiated control
        // frame (brightness/preset/overlay) cannot land between a transfer's BEGIN/DATA/COMMIT
        // frames - the transfer holds _txGate for its whole burst and this re-enters it.
        lock (_txGate)
        {
        if (!EnsureConnected()) return false;
        var transport = _transport!;
        try
        {
            transport.Write(request);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            // The transport already retried a transient write on the held handle,
            // so reaching here is a persistent failure (e.g. the panel was
            // unplugged). Drop the transport so the next EnsureConnected rediscovers
            // and reopens - the retry-on-the-handle above is what prevents the old
            // reopen-on-every-transient-failure churn.
            ServiceLog.Error($"[tryx] write failed: {ex.GetType().Name}: {ex.Message}");
            Disconnect();
            return false;
        }
        }
    }

    // One-shot commands (preset / brightness / screen / overlay-set) must survive the
    // panel's ~60s USB self-re-enumeration: a write that lands during the drop fails
    // and releases the transport; once the device re-appears (~0.2-1s) a retry via
    // EnsureConnected reopens it and succeeds. Without this a single user action that
    // coincides with a re-enumeration silently no-ops (and can leave the panel mid-
    // load). The 1 Hz heartbeat / overlay-refresh path uses SendOnly directly - it
    // re-sends on the next tick, so it needs no retry here; the heartbeat's slideshow
    // advance does go through here (a missed select is not re-sent until the next hold).
    // After a re-enumeration drop the panel re-appears on the bus almost at once but
    // is not write-ready for another ~0.2-1s (observed: "write failed" -> "connected"
    // ~200ms later in the service log, and the reopen's config write can still fail
    // past that). Presence alone is a false ready-signal, so settle a fixed span
    // between resends rather than polling discovery. Six attempts span ~3s, longer
    // than any single recovery window seen.
    private const int SendMaxAttempts = 6;
    private const int SendRetrySettleMs = 500;

    private bool SendReliable(byte[] request)
    {
        for (var attempt = 0; attempt < SendMaxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                if (_disposed) return false;
                Thread.Sleep(SendRetrySettleMs);
            }
            if (SendOnly(request)) return true;
        }
        return false;
    }

    private static void RunAdb(string adbPath, string arguments, int timeoutMs, out string stdout, out string stderr)
    {
        stdout = string.Empty;
        stderr = string.Empty;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(adbPath) ?? string.Empty,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return;
            // Drain both pipes concurrently before waiting; a child that fills the
            // OS pipe buffer would otherwise deadlock against a read-after-wait.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { /* best effort */ }
                stderr = "timed out";
                return;
            }
            stdout = outTask.GetAwaiter().GetResult();
            stderr = errTask.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            stderr = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

}
