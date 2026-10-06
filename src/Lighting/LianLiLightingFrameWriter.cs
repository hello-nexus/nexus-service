using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Writes each engine frame to the Lian Li Uni Hub as it is published (custom
/// mode) or commits firmware animations once on settings change (all other modes).
/// </summary>
public sealed class LianLiLightingFrameWriter : IHostedService, IDisposable
{
    // Tick cadence while the engine is stopped and publishes nothing.
    private const int IdleTickMs = 100;

    // Settle between writes; SL-Infinity custom streaming is paced per frame
    // instead (SlInfinityFrameIntervalMs).
    private const int InterWriteSettleMs = 1;

    // SL-Infinity fw 1.4 shows a streamed frame only through each channel's
    // commit; past its rate it falls back to ~2 visible updates/s. Frame
    // interval by streamed port count, from the Y70 camera fan chase
    // (2026-10-04): one port shows every frame to 12 fps and collapses at 20;
    // two ports show ~7 of 10 without collapsing; four ports show 5 and
    // collapse at 10.
    private static int SlInfinityFrameIntervalMs(int ports) => ports switch
    {
        <= 1 => 83,
        2 => 100,
        _ => 200,
    };

    // Engine frames land a few ms either side of their period; without slack a
    // frame due on a frame boundary slips a whole frame and the cadence wobbles.
    private const int PacingSlackMs = 5;

    // SL-Infinity fw 1.4 ignores a commit sent under ~5 ms after merge-off (Y70 camera sweep: 0 ms stuck, 5 ms+ exits).
    private const int MergeOffSettleMs = 20;

    // hidraw write() and SET_REPORT ioctls return after the USB transfer completes, so Linux needs no settle.
    private static void Settle()
    {
        if (!OperatingSystem.IsLinux())
        {
            Thread.Sleep(InterWriteSettleMs);
        }
    }

    // Hub writes are synchronous HidD_SetFeature, each taking LianLiHub._lock that
    // fan control also takes, so a rejected commit backs off rather than
    // re-acquiring it once per channel write every tick against a silent hub.
    private const int RetryBaseMs = 1000;
    private const int RetryMaxMs = 30_000;

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

    private readonly LightingEngine _engine;
    private readonly LianLiHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly AutoResetEvent _framePublished = new(false);

    private RgbColor[][] _segmentBuffers = Array.Empty<RgbColor[]>();

    // Last firmware-mode signature the hardware accepted.
    private int? _lastFirmwareSig;

    // Signature currently being attempted, so a retry is told apart from a settings change.
    private int? _pendingFirmwareSig;

    // Independent windows: a hub that rejects its attach commands must not gate
    // custom-mode streaming, which does not depend on them landing.
    private readonly RetryWindow _initRetry = new();
    private readonly RetryWindow _commitRetry = new();
    private readonly RetryWindow _argbSyncRetry = new();

    // False until the attach-time commands (merge off, per-port quantity) have
    // gone out for the current connection; families with a per-frame start
    // carry the quantity in every frame instead.
    private bool _hubInitialised;
    // ARGB-sync state last accepted by the hub; false after attach, so a hub
    // never put on ARGB sync sees no extra write.
    private bool _argbSyncSent;
    // The hub drops commits for a moment after the sync-off release, so output
    // holds, then the mode is re-committed at each mark.
    private static readonly long[] ArgbReleaseRecommitMs = { 600, 1500, 3000 };
    private long _argbReleasedAtMs;
    private int _argbRecommitsDone = ArgbReleaseRecommitMs.Length;

    private bool _customMergeCleared;
    private int _resumeEpochSeen;
    private long _lastCustomFrameMs = long.MinValue / 2;

    // Per-device resolved zones for the firmware-mode sig/commit pair, reused
    // each tick so they resolve once instead of once per call site.
    private readonly List<IReadOnlyList<ResolvedZone>> _firmwareZonesByDevice = new();
    private readonly List<IReadOnlyList<ResolvedZone>> _customZones = new();

    // Raw RGB scratch for one channel, sized for the largest per-fan ring across families.
    private readonly byte[] _channelBuf =
        new byte[LianLiProtocol.MaxFansPerPort * LianLiProtocol.MaxLedsPerFanPerChannel * 3];

    // True while timeBeginPeriod(1) is active; matches the custom-streaming lifetime.
    private bool _highResTimer;

    private readonly FeatureGates _gates;

    public LianLiLightingFrameWriter(LightingEngine engine, LianLiHub hub, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
    {
        _engine = engine;
        _hub = hub;
        _store = store;
        _identify = identify;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _engine.FramePublished += OnFramePublished;
        _loop = Task.Factory.StartNew(() => Run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _engine.FramePublished -= OnFramePublished;
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch { /* shutdown best-effort */ }
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        if (_highResTimer && OperatingSystem.IsWindows())
        {
            timeEndPeriod(1);
            _highResTimer = false;
        }
    }

    public void Dispose()
    {
        StopAsync(default).GetAwaiter().GetResult();
        _framePublished.Dispose();
    }

    private void OnFramePublished()
    {
        // The engine may still invoke a handler list it read before StopAsync unsubscribed.
        try { _framePublished.Set(); }
        catch (ObjectDisposedException) { }
    }

    // Paced by the engine rather than a timer of its own: two free-running
    // clocks of the same period drift into phase, and there each tick re-sends
    // the previous frame and then skips one.
    private void Run(CancellationToken ct)
    {
        var wake = new[] { _framePublished, ct.WaitHandle };
        while (!ct.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[lianli-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { WaitHandle.WaitAny(wake, IdleTickMs); }
            catch (ObjectDisposedException) { break; }
        }
    }

    internal void Tick()
    {
        if (!_gates.Lighting) return;
        if (!_hub.IsConnected)
        {
            if (_highResTimer && OperatingSystem.IsWindows())
            {
                timeEndPeriod(1);
                _highResTimer = false;
            }
            ForgetHubState();
            return;
        }
        var resumeEpoch = _hub.ResumeEpoch;
        if (resumeEpoch != _resumeEpochSeen)
        {
            _resumeEpochSeen = resumeEpoch;
            ForgetHubState();
        }
        var settings = _store.Load();
        var ls = settings.Devices.LianLiLighting;
        var profile = _hub.Profile;
        var argbSync = profile.PlaysArgbInput(ls.ArgbSync);
        if (_argbSyncSent != argbSync && !_argbSyncRetry.BackingOff(NowMs()))
        {
            if (_hub.SendArgbSync(argbSync))
            {
                NoteSuccess(_argbSyncRetry, "ARGB sync");
                _argbSyncSent = argbSync;
                // Leaving ARGB sync, the release left every channel black: re-commit.
                _lastFirmwareSig = null;
                _customMergeCleared = false;
                if (!argbSync)
                {
                    _argbReleasedAtMs = NowMs();
                    _argbRecommitsDone = 0;
                }
            }
            else
            {
                NoteFailure(_argbSyncRetry, "ARGB sync");
            }
        }
        if (!argbSync && _argbRecommitsDone < ArgbReleaseRecommitMs.Length)
        {
            var sinceRelease = NowMs() - _argbReleasedAtMs;
            if (sinceRelease < ArgbReleaseRecommitMs[0]) return;
            if (sinceRelease >= ArgbReleaseRecommitMs[_argbRecommitsDone])
            {
                _argbRecommitsDone++;
                _lastFirmwareSig = null;
                _customMergeCleared = false;
            }
        }
        if (argbSync)
        {
            if (_highResTimer && OperatingSystem.IsWindows())
            {
                timeEndPeriod(1);
                _highResTimer = false;
            }
            return;
        }

        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var globalBrightness = MasterBrightness.Effective(settings.Lighting);

        // A rejected init is retried on its own window but never aborts the tick:
        // pre-retry this was fire-and-forget, and custom mode streams without it.
        if (!_hubInitialised && !_initRetry.BackingOff(NowMs()))
        {
            if (InitialiseHub(profile, settings.Devices.LianLi))
            {
                NoteSuccess(_initRetry, "hub init");
                _hubInitialised = true;
            }
            else
            {
                NoteFailure(_initRetry, "hub init");
            }
        }
        var comp = LianLiZoneSupport.ReadComposition(settings, _hub.DeviceId);
        var composed = LianLiZoneSupport.Compose(_hub.DeviceId, profile, comp, settings.Devices.LianLi);

        if (ls.Mode == "custom")
        {
            // Raise OS timer resolution so the inter-write settles of the families
            // that keep them are ~1 ms, not the default ~15 ms. Held only while
            // streaming; lowered on mode switch or hub detach.
            if (!_highResTimer && OperatingSystem.IsWindows())
            {
                timeBeginPeriod(1);
                _highResTimer = true;
            }
            // Reset firmware sig so the next firmware-mode switch re-commits,
            // and its backoff with it so a stale deadline cannot gate that switch.
            _lastFirmwareSig = null;
            _pendingFirmwareSig = null;
            _commitRetry.Reset();
            // A merged firmware effect outlives per-channel custom commits too.
            if (!_customMergeCleared && profile.SupportsMerge && _hub.SendStopMerge())
            {
                Thread.Sleep(MergeOffSettleMs);
                _customMergeCleared = true;
            }
            TickCustom(settings, devices, globalBrightness, composed, profile);
            return;
        }
        _customMergeCleared = false;

        if (_highResTimer && OperatingSystem.IsWindows())
        {
            timeEndPeriod(1);
            _highResTimer = false;
        }

        // A key persisted for another family (the route rejects new ones) falls
        // back to static, the one commit every Uni hub accepts.
        var mode = LianLiLightingModes.Find(profile.Family, ls.Mode) ?? LianLiLightingModes.Find(profile.Family, "static");
        if (mode == null)
        {
            return;
        }

        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        _firmwareZonesByDevice.Clear();
        foreach (var device in composed)
        {
            _firmwareZonesByDevice.Add(ZoneResolution.Resolve(device.Structure, settings));
        }

        var sig = ComputeFirmwareSig(ls, globalBrightness, composed, profile, settings.Devices.DisabledLightingDevices, uncontrolled, _firmwareZonesByDevice);
        if (_lastFirmwareSig.HasValue && sig == _lastFirmwareSig.Value)
        {
            return;
        }

        if (_pendingFirmwareSig != sig)
        {
            // New settings, not a retry: apply without serving out the old backoff.
            _pendingFirmwareSig = sig;
            _commitRetry.Reset();
        }
        else if (_commitRetry.BackingOff(NowMs()))
        {
            return;
        }

        // One merged animation cannot honour per-port control, so any excluded port keeps the per-port commit.
        var merged = ls.Merge && mode.MergesOn(profile)
            && !AnyDeviceExcluded(composed, settings);
        var committed = merged
            ? CommitMergedMode(ls, mode, globalBrightness, composed, profile, settings.Devices.DisabledLightingDevices, settings.Devices.LianLi)
            : CommitFirmwareMode(ls, mode, globalBrightness, composed, profile, settings.Devices.DisabledLightingDevices, uncontrolled, _firmwareZonesByDevice);
        if (committed)
        {
            NoteSuccess(_commitRetry, "firmware commit");
            _lastFirmwareSig = sig;
        }
        else
        {
            NoteFailure(_commitRetry, "firmware commit");
        }
    }

    // The hub loses commanded LED state on USB re-enumeration and across sleep,
    // so forget what it was sent: the next tick re-initialises it and re-commits
    // the firmware mode even when settings are unchanged. The cooling path
    // re-asserts for the same reason.
    private void ForgetHubState()
    {
        _lastFirmwareSig = null;
        _pendingFirmwareSig = null;
        _hubInitialised = false;
        _argbSyncSent = false;
        _argbRecommitsDone = ArgbReleaseRecommitMs.Length;
        _argbSyncRetry.Reset();
        _customMergeCleared = false;
        _initRetry.Reset();
        _commitRetry.Reset();
    }

    /// <summary>Test seam: tests advance the backoff clock instead of sleeping out the cap.</summary>
    internal Func<long> NowMs { get; set; } = static () => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

    /// <summary>Widening retry window in milliseconds: RetryBaseMs doubling per failure, capped at RetryMaxMs.</summary>
    private sealed class RetryWindow
    {
        private int _streak;
        private long _retryAtMs;

        public int Streak => _streak;

        public bool BackingOff(long nowMs) => _streak > 0 && nowMs < _retryAtMs;

        public void Fail(long nowMs)
        {
            _streak++;
            // Shift clamp is an overflow bound only; RetryMaxMs is the one cap.
            _retryAtMs = nowMs + Math.Min((long)RetryBaseMs << Math.Min(_streak - 1, 30), RetryMaxMs);
        }

        public void Reset()
        {
            _streak = 0;
            _retryAtMs = 0;
        }
    }

    /// <summary>Logs once per streak so a hub that never answers cannot fill the log at tick rate.</summary>
    private void NoteFailure(RetryWindow window, string what)
    {
        window.Fail(NowMs());
        if (window.Streak == 1)
        {
            Console.Error.WriteLine(
                $"[lianli-lighting-writer] {what} rejected by the hub; retrying with backoff (max {RetryMaxMs / 1000}s)");
        }
    }

    private void NoteSuccess(RetryWindow window, string what)
    {
        if (window.Streak > 0)
        {
            Console.WriteLine(
                $"[lianli-lighting-writer] {what} applied after {window.Streak} rejected attempt(s)");
            window.Reset();
        }
    }

    /// <summary>Attach-time commands; returns false on the first rejected write so the caller retries the whole sequence.</summary>
    private bool InitialiseHub(in LianLiFanProfile profile, LianLiSettings fans)
    {
        if (profile.ClearMergeOnAttach)
        {
            if (!_hub.SendStopMerge()) return false;
            Settle();
        }
        if (profile.StartActionPerFrame) return true;
        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            if (!_hub.SetQuantity(p, LianLiZoneSupport.ClampFans(fans.GetFans(p), profile))) return false;
            Settle();
        }
        return true;
    }

    private void TickCustom(
        NexusSettings settings,
        DeviceFrame[] devices,
        float globalBrightness,
        List<ComposedDevice> composed,
        in LianLiFanProfile profile)
    {
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var nowTicks = DateTime.UtcNow.Ticks;

        _customZones.Clear();
        var ports = 0;
        foreach (var device in composed)
        {
            var zones = ZoneResolution.Resolve(device.Structure, settings);
            _customZones.Add(zones);
            if (!ZoneResolution.IsFullyUncontrolled(zones, uncontrolled)) ports++;
        }
        var paced = profile.Family == LianLiFanFamily.SlInfinity;
        if (paced)
        {
            var nowMs = NowMs();
            if (nowMs - _lastCustomFrameMs < SlInfinityFrameIntervalMs(ports) - PacingSlackMs) return;
            _lastCustomFrameMs = nowMs;
        }

        for (var d = 0; d < composed.Count; d++)
        {
            var device = composed[d];
            var structure = device.Structure;
            var zones = _customZones[d];
            if (ZoneResolution.IsFullyUncontrolled(zones, uncontrolled))
            {
                // Every zone of this fan group is uncontrolled: leave its two
                // channels alone entirely rather than streaming a black frame.
                continue;
            }
            SegmentFrameComposer.EnsureBuffers(structure, ref _segmentBuffers);
            SegmentFrameComposer.Compose(
                structure, zones, devices, disabled, uncontrolled, prefs, globalBrightness, 1.0, nowTicks, _identify, _segmentBuffers);

            for (var seg = 0; seg < structure.Segments.Count; seg++)
            {
                var buf = _segmentBuffers[seg];
                var byteCount = buf.Length * 3;
                for (var i = 0; i < buf.Length; i++)
                {
                    var c = buf[i];
                    var off = i * 3;
                    _channelBuf[off]     = c.R;
                    _channelBuf[off + 1] = c.G;
                    _channelBuf[off + 2] = c.B;
                }
                foreach (var ch in device.SegmentChannels[seg])
                {
                    if (profile.StartActionPerFrame)
                    {
                        _hub.SendStartAction(ch / profile.ChannelsPerPort, profile.MaxFans);
                        if (!paced) Settle();
                    }
                    _hub.SendColorData(ch, _channelBuf.AsSpan(0, byteCount));
                    if (!paced) Settle();
                    _hub.SendEffectCommit(ch);
                    if (!paced) Settle();
                }
            }

            // Latch PER DEVICE, not once per tick. The sync applies the frame for
            // the port it follows; with a single port a trailing sync looked
            // equivalent, but as soon as a second port is populated only the
            // last-addressed one latched and the other fell back to the
            // firmware's ~0.6 Hz internal repaint - the whole rig then reads as
            // roughly 1 Hz once a fan is moved to a second port.
            _hub.SendFrameSync();
            if (!paced) Settle();
        }
    }

    /// <summary>Commits the firmware animation on every controlled channel; returns false on the first rejected write, leaving the signature unlatched so the next attempt re-sends every channel.</summary>
    private bool CommitFirmwareMode(
        LianLiLightingSettings ls,
        LianLiModeInfo mode,
        float globalBrightness,
        List<ComposedDevice> composed,
        in LianLiFanProfile profile,
        IReadOnlyList<string> disabled,
        IReadOnlyList<string> uncontrolled,
        List<IReadOnlyList<ResolvedZone>> zonesByDevice)
    {
        var speedByte = LianLiLightingModes.SpeedCodes[Math.Clamp(ls.Speed, 0, 4)];
        var dirByte = LianLiLightingModes.DirectionByte(ls.Direction);

        // Per-channel commits do not exit a merged effect; only merge-off does (the identity merge order does not).
        if (profile.SupportsMerge)
        {
            if (!_hub.SendStopMerge()) return false;
            Thread.Sleep(MergeOffSettleMs);
        }

        for (var i = 0; i < composed.Count; i++)
        {
            var device = composed[i];
            if (ZoneResolution.IsFullyUncontrolled(zonesByDevice[i], uncontrolled))
            {
                // Every zone of this fan group is uncontrolled: skip its channel
                // commits entirely rather than committing at brightness zero.
                continue;
            }

            var numFans = NumFansForDevice(device, profile);
            var brightnessByte = DeviceBrightnessByte(device, ls, globalBrightness, disabled);

            for (var seg = 0; seg < device.SegmentChannels.Count; seg++)
            {
                foreach (var ch in device.SegmentChannels[seg])
                {
                    if (mode.WholeFan && profile.ChannelsPerPort == 2 && (ch & 1) == 1) continue;
                    // Inner and outer rings hold different per-fan counts, so the
                    // palette is refilled per channel rather than once per device.
                    var ledsPerFan = profile.LedsPerFanForChannel(ch);
                    var effectByte = mode.EffectByte;
                    var perFan = effectByte is LianLiProtocol.EffectStatic or LianLiProtocol.EffectBreathing;
                    var byteCount = FillPaletteBuffer(_channelBuf, mode, ls.Colors, numFans, ledsPerFan, perFan, profile.PaletteSlotsPerFan);
                    if (profile.StartActionPerFrame)
                    {
                        if (!_hub.SendStartAction(ch / profile.ChannelsPerPort, numFans)) return false;
                        Settle();
                    }
                    if (!_hub.SendColorData(ch, _channelBuf.AsSpan(0, byteCount))) return false;
                    Settle();
                    if (!_hub.SendModeCommit(ch, effectByte, speedByte, dirByte, brightnessByte)) return false;
                    Settle();
                }
            }
        }

        // The per-channel commits alone leave the firmware rendering the
        // previous speed/brightness; this latches them. Once for the whole
        // apply, after every port, exactly as L-Connect does.
        if (!_hub.SendFrameSync()) return false;
        Settle();
        return true;
    }

    /// <summary>
    /// One animation across every port. Firmware sequence: port order, each
    /// port's fan count, every channel but 0 parked dark, then the palette and
    /// merged effect on channel 0, with no frame sync after. Returns false on
    /// the first rejected write.
    /// </summary>
    private bool CommitMergedMode(
        LianLiLightingSettings ls,
        LianLiModeInfo mode,
        float globalBrightness,
        List<ComposedDevice> composed,
        in LianLiFanProfile profile,
        IReadOnlyList<string> disabled,
        LianLiSettings fans)
    {
        if (composed.Count == 0) return true;
        var anchor = composed[0];
        foreach (var device in composed)
        {
            if (CarriesChannel(device, 0))
            {
                anchor = device;
                break;
            }
        }

        var speedByte = LianLiLightingModes.SpeedCodes[Math.Clamp(ls.Speed, 0, 4)];
        var dirByte = LianLiLightingModes.DirectionByte(ls.Direction);
        var brightnessByte = DeviceBrightnessByte(anchor, ls, globalBrightness, disabled);

        if (!_hub.SendMergeOrder()) return false;
        Settle();
        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            if (!_hub.SetQuantity(p, LianLiZoneSupport.ClampFans(fans.GetFans(p), profile))) return false;
            Settle();
        }
        for (var ch = LianLiProtocol.PortCount * profile.ChannelsPerPort - 1; ch >= 1; ch--)
        {
            var off = LianLiLightingModes.BrightnessCodes[0];
            if (!_hub.SendModeCommit(ch, LianLiProtocol.EffectMergeIdle, LianLiProtocol.SpeedDefault, LianLiProtocol.DirectionDefault, off)) return false;
            Settle();
        }
        var paletteBytes = FillPaletteBuffer(_channelBuf, mode, ls.Colors, profile.MaxFans, profile.LedsPerFanForChannel(0), perFan: false, profile.PaletteSlotsPerFan);
        if (!_hub.SendColorData(0, _channelBuf.AsSpan(0, paletteBytes))) return false;
        Settle();
        if (!_hub.SendModeCommit(0, mode.MergedEffectByte, speedByte, dirByte, brightnessByte)) return false;
        Settle();
        return true;
    }

    /// <summary>True when a fan group is disabled or fully uncontrolled; the lighting route hides Merge on the same test.</summary>
    internal static bool AnyDeviceExcluded(List<ComposedDevice> composed, NexusSettings settings)
    {
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        foreach (var device in composed)
        {
            if (IsDeviceDisabled(device, disabled)
                || ZoneResolution.IsFullyUncontrolled(ZoneResolution.Resolve(device.Structure, settings), uncontrolled))
            {
                return true;
            }
        }
        return false;
    }

    private static bool CarriesChannel(ComposedDevice device, int channel)
    {
        foreach (var channels in device.SegmentChannels)
        {
            foreach (var ch in channels)
            {
                if (ch == channel) return true;
            }
        }
        return false;
    }

    private static int NumFansForDevice(ComposedDevice device, in LianLiFanProfile profile)
    {
        if (device.Structure.Segments.Count == 0 || profile.InnerLedsPerFan <= 0)
        {
            return profile.MaxFans;
        }
        // Segment 0 is the inner (or only) ring, so its count divides by that per-fan value.
        var fans = device.Structure.Segments[0].LedCount / profile.InnerLedsPerFan;
        return Math.Clamp(fans, 1, profile.MaxFans);
    }

    private static byte DeviceBrightnessByte(
        ComposedDevice device,
        LianLiLightingSettings ls,
        float globalBrightness,
        IReadOnlyList<string> disabled)
    {
        if (globalBrightness <= 0f)
        {
            return LianLiLightingModes.BrightnessCodes[0];
        }
        if (IsDeviceDisabled(device, disabled))
        {
            return LianLiLightingModes.BrightnessCodes[0];
        }
        var idx = (int)Math.Round(Math.Min((double)ls.Brightness, globalBrightness * 4.0));
        return LianLiLightingModes.BrightnessCodes[Math.Clamp(idx, 0, 4)];
    }

    private static bool IsDeviceDisabled(ComposedDevice device, IReadOnlyList<string> disabled)
    {
        var prefix = device.Structure.DeviceId + ":";
        var id = device.Structure.DeviceId;
        for (var i = 0; i < disabled.Count; i++)
        {
            var d = disabled[i];
            if (d == id || d.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    // perFan: colour i fills fan i's whole ring (cycling when fewer colours than
    // fans). Otherwise each fan carries the palette in its own run of slots,
    // missing colours black. No user colours means the mode's defaults. Input
    // colours are RGB; SendColorData applies the R,B,G wire swap. Returns the
    // bytes filled.
    private static int FillPaletteBuffer(byte[] buf, LianLiModeInfo mode, IReadOnlyList<string> colors, int numFans, int ledsPerFan, bool perFan, int slotsPerFan)
    {
        Array.Clear(buf);
        var byteCount = perFan ? numFans * ledsPerFan * 3 : numFans * slotsPerFan * 3;
        if (mode.ColorsMax == 0)
        {
            return byteCount;
        }
        var source = colors.Count > 0 ? colors : mode.DefaultColors;
        var count = Math.Min(source.Count, mode.ColorsMax);
        if (count == 0)
        {
            return byteCount;
        }

        for (var fanIdx = 0; fanIdx < numFans; fanIdx++)
        {
            if (perFan)
            {
                var (r, g, b) = ParseHexColor(source[fanIdx % count]);
                for (var led = 0; led < ledsPerFan; led++)
                {
                    Put(buf, fanIdx * ledsPerFan + led, r, g, b);
                }
                continue;
            }
            for (var j = 0; j < slotsPerFan && j < count; j++)
            {
                var (r, g, b) = ParseHexColor(source[j]);
                Put(buf, fanIdx * slotsPerFan + j, r, g, b);
            }
        }
        return byteCount;
    }

    private static void Put(byte[] buf, int index, byte r, byte g, byte b)
    {
        var off = index * 3;
        if (off + 2 >= buf.Length) return;
        buf[off] = r;
        buf[off + 1] = g;
        buf[off + 2] = b;
    }

    private static (byte r, byte g, byte b) ParseHexColor(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length < 6)
        {
            return (0, 0, 0);
        }
        if (!byte.TryParse(s.AsSpan(0, 2), NumberStyles.HexNumber, null, out var r)) { r = 0; }
        if (!byte.TryParse(s.AsSpan(2, 2), NumberStyles.HexNumber, null, out var g)) { g = 0; }
        if (!byte.TryParse(s.AsSpan(4, 2), NumberStyles.HexNumber, null, out var b)) { b = 0; }
        return (r, g, b);
    }

    private static int ComputeFirmwareSig(
        LianLiLightingSettings ls,
        float globalBrightness,
        List<ComposedDevice> composed,
        in LianLiFanProfile profile,
        IReadOnlyList<string> disabled,
        IReadOnlyList<string> uncontrolled,
        List<IReadOnlyList<ResolvedZone>> zonesByDevice)
    {
        var hc = new HashCode();
        hc.Add(ls.Mode);
        hc.Add(ls.Speed);
        hc.Add(ls.Direction);
        hc.Add(ls.Merge);
        foreach (var c in ls.Colors)
        {
            hc.Add(c);
        }
        for (var i = 0; i < composed.Count; i++)
        {
            var device = composed[i];
            var skipped = ZoneResolution.IsFullyUncontrolled(zonesByDevice[i], uncontrolled);
            hc.Add(skipped);
            if (skipped)
            {
                continue;
            }
            hc.Add(NumFansForDevice(device, profile));
            var brightnessByte = DeviceBrightnessByte(device, ls, globalBrightness, disabled);
            for (var seg = 0; seg < device.SegmentChannels.Count; seg++)
            {
                foreach (var ch in device.SegmentChannels[seg])
                {
                    hc.Add(ch);
                    hc.Add(brightnessByte);
                }
            }
        }
        return hc.ToHashCode();
    }
}
