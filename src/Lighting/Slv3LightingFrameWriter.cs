using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Streams engine frames to every bound SLV3 wireless fan chain (and Strimer
/// Wireless cable) as a single-frame RF_RgbSync animation - an "OpenRGB-style
/// / live direct mode". There is no
/// firmware ROM-effect catalog exposed for wireless fans: every tick composes
/// each chain's resolved zone frames into a fan-major buffer (the family's
/// wire LED count per fan; a Strimer's whole cable) and pushes it through
/// <see cref="Slv3Hub.SendRgbFrame"/> only when the buffer content changed,
/// or when the chain's RX-reported effect_index has had time to echo the last
/// push and still disagrees (the push was lost, or the chain reset). L-Connect
/// uploads an effect once and re-sends it only until the echo matches; a
/// static frame here behaves the same, and the air stays quiet between changes.
/// </summary>
public sealed class Slv3LightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;

    // Floor between pushes to one chain: a push is the hub's spaced header
    // repeats plus the data parts, and N chains split the air N ways so the
    // telemetry beacon and the device-list poll keep their share.
    private const int MinPushIntervalMs = 100;

    // How long after a push the RX-reported effect_index is given to catch up
    // (a few device-list polls) before a mismatch counts as a lost push and is
    // re-sent. Comparing before the echo can arrive re-sent every unchanged
    // static frame on every tick (Y70 USBPcap 2026-09-04).
    private const int DriftConfirmWindowMs = 1500;

    // Static single-frame animation: the interval only matters if the
    // firmware were looping multiple frames, which direct mode never sends.
    private const int IntervalMs = 100;

    // Floor between two upload attempts of a Strimer's pre-rendered animation:
    // a max-size upload is dozens of RF payloads, so a brightness drag sends
    // its settled value instead of every step, and a failing upload retries at
    // this pace instead of every tick.
    private const int PresetMinPushIntervalMs = 500;

    // SegmentFrameComposer already applies the zone's LightingDevicePrefs
    // brightness and the global brightness to the composed colors, so this
    // pass-through leaves Slv3RgbFrame's brightness formula a no-op and lets
    // its power cap alone act on the final wire bytes.
    private const int PassThroughBrightnessPercent = 100;

    private readonly LightingEngine _engine;
    private readonly Slv3Hub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly Slv3LightingDeviceProvider _provider;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private RgbColor[][] _segmentBuffers = Array.Empty<RgbColor[]>();
    private RgbColor[] _wireBuffer = Array.Empty<RgbColor>();

    // Per-MAC last pushed state: content hash + the effect_index we sent, so
    // a dropped push (RX-reported effect_index drifts from ours) resends
    // even when the desired color content is unchanged.
    private readonly Dictionary<string, (int Hash, string EffectIndexHex)> _lastSent = new();

    // Per-fan tick of the last RGB push. Intermediate frames are dropped, not
    // queued - the next eligible tick sends whatever is current.
    private readonly Dictionary<string, long> _lastPushTicks = new();

    public Slv3LightingFrameWriter(
        LightingEngine engine, Slv3Hub hub, IConfigStore store, Np50IdentifyTracker identify, Slv3LightingDeviceProvider provider,
        Func<long>? nowTicks = null, FeatureGates? gates = null)
    {
        _engine = engine;
        _hub = hub;
        _store = store;
        _identify = identify;
        _provider = provider;
        _nowTicks = nowTicks ?? (() => DateTime.UtcNow.Ticks);
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    private readonly Func<long> _nowTicks;
    private readonly FeatureGates _gates;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch { /* shutdown best-effort */ }
        }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose() => StopAsync(default).GetAwaiter().GetResult();

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickPeriodMs));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tickStart = Environment.TickCount64;
                Tick();
                _healthTickMaxMs = Math.Max(_healthTickMaxMs, Environment.TickCount64 - tickStart);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[lianli-wireless-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    internal void Tick()
    {
        if (!_gates.Lighting)
        {
            FreezeRollingChains();
            return;
        }
        if (!_hub.IsConnected)
        {
            _lastSent.Clear();
            _lastPushTicks.Clear();
            _clock.Clear();
            DropSharedWindow();
            _argbRequests.Clear();
            return;
        }
        var settings = _store.Load();
        // A chain on its motherboard input has no engine cards, so this runs before the device check.
        ReconcileMotherboardArgb(settings, _nowTicks() / TimeSpan.TicksPerMillisecond);

        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var globalBrightness = MasterBrightness.Resolve(settings);
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var nowTicks = _nowTicks();

        var structures = _provider.BuildStructures();
        var liveMacs = new HashSet<string>(structures.Count);

        var minPushIntervalMs = MinPushIntervalMs * Math.Max(1, structures.Count);
        var nowMs = UtcNowMs();
        AdvanceSharedWindow(nowMs);
        _rollingIds.Clear();
        _planChains = 0;
        _planLeds = 0;
        _liveZones.Clear();
        _liveEqual.Clear();
        var rollingChains = 0;
        string? pushMac = null;
        var pushAgeMs = -1L;
        var pushHash = 0;
        var pushLeds = 0;

        foreach (var structure in structures)
        {
            var macHex = Slv3LightingDeviceProvider.MacFromDeviceId(structure.DeviceId);
            if (macHex.Length == 0) continue;
            liveMacs.Add(macHex);

            var zones = ZoneResolution.Resolve(structure, settings);
            if (ZoneResolution.IsFullyUncontrolled(zones, uncontrolled))
            {
                // Every zone of this chain is uncontrolled: stop streaming to it
                // so its reactive/onboard mode can take over.
                FreezeRollingChain(macHex);
                continue;
            }
            // A window still uploading would land after anything sent now and bury it.
            if (_rolling.TryGetValue(macHex, out var uploading) && uploading.Upload is { IsCompleted: false })
            {
                uploading.Visited = true;
                foreach (var z in zones) _rollingIds.Add(z.Id);
                NotePlanned(structure);
                rollingChains++;
                continue;
            }
            // A HydroShift II streams live even under a preset stored before presets excluded it.
            if (!Slv3LightingDeviceProvider.IsHydroShiftStructure(structure)
                && settings.Devices.LianLiWireless.Chains.TryGetValue(macHex, out var chainLighting)
                && chainLighting.Mode != LianLiWirelessChainLighting.ModeCustom
                && TickPreset(macHex, structure, zones, disabled, chainLighting, globalBrightness, nowTicks))
            {
                continue;
            }

            if (TickRolling(macHex, structure, zones, disabled, uncontrolled, prefs, globalBrightness, nowTicks, nowMs))
            {
                rollingChains++;
                continue;
            }

            SegmentFrameComposer.EnsureBuffers(structure, ref _segmentBuffers);
            SegmentFrameComposer.Compose(
                structure, zones, devices, disabled, uncontrolled, prefs, globalBrightness, 1.0, nowTicks, _identify, _segmentBuffers, _engine.Live, stageLive: true);
            _liveZones[macHex] = zones;

            var totalLeds = FillWireBuffer(macHex, structure);
            if (totalLeds == 0) continue;

            var frameSpan = _wireBuffer.AsSpan(0, totalLeds);
            var hash = ComputeHash(frameSpan);

            // A dictionary miss defaults the tuple's string component to null,
            // not "" - IsNullOrEmpty covers both the miss and a not-yet-sent state.
            _lastSent.TryGetValue(macHex, out var last);
            var hasLastPush = _lastPushTicks.TryGetValue(macHex, out var lastPush);
            var sinceLastPushMs = hasLastPush ? (nowTicks - lastPush) / TimeSpan.TicksPerMillisecond : long.MaxValue;
            var confirmed = FindConfirmedEffectIndex(macHex);
            var driftedSinceLastConfirm = !string.IsNullOrEmpty(last.EffectIndexHex)
                && confirmed.Length > 0
                && sinceLastPushMs >= DriftConfirmWindowMs
                && !string.Equals(confirmed, last.EffectIndexHex, StringComparison.OrdinalIgnoreCase);
            if (last.Hash == hash && !driftedSinceLastConfirm)
            {
                // Unchanged since the last push, so the chain already shows it.
                _liveEqual.Add(macHex);
                continue;
            }

            if (sinceLastPushMs < minPushIntervalMs)
            {
                continue;
            }

            if (sinceLastPushMs > pushAgeMs)
            {
                pushMac = macHex;
                pushAgeMs = sinceLastPushMs;
                pushHash = hash;
                pushLeds = totalLeds;
                if (_pushBuffer.Length < totalLeds) _pushBuffer = new RgbColor[totalLeds];
                frameSpan.CopyTo(_pushBuffer);
            }
        }

        ApplyWindowPlan(nowMs);
        RequestSharedWindow(nowMs);

        var livePushed = false;
        // A push blocks this thread on its header gaps, so one per tick (oldest
        // first) keeps later ticks inside a window's join range.
        if (pushMac is not null)
        {
            var pushed = _hub.SendRgbFrame(pushMac, _pushBuffer.AsSpan(0, pushLeds), PassThroughBrightnessPercent, IntervalMs, out var sentEffectIndexHex);
            livePushed = pushed;
            // Stamped on failure too, or a failing chain stays oldest and takes every slot.
            _lastPushTicks[pushMac] = nowTicks;
            if (pushed)
            {
                _lastSent[pushMac] = (pushHash, sentEffectIndexHex);
            }
        }
        // Only the chain actually pushed (or already showing the same bytes) publishes; the rest keep their last output on show.
        foreach (var (mac, liveZones) in _liveZones)
        {
            if ((livePushed && mac == pushMac) || _liveEqual.Contains(mac))
            {
                _engine.Live.CommitZones(liveZones);
            }
            else
            {
                _engine.Live.TouchZones(liveZones);
            }
        }
        LogHealth(nowMs, rollingChains);

        // Chains that left rolling this tick (preset, uncontrolled, gone) forget their window.
        List<string>? idle = null;
        foreach (var (mac, st) in _rolling)
        {
            if (st.Visited)
            {
                st.Visited = false;
                continue;
            }
            CollectUpload(mac, st);
            if (st.Upload is null) (idle ??= new()).Add(mac);
        }
        if (idle is not null)
        {
            foreach (var mac in idle) _rolling.Remove(mac);
        }

        if (_lastSent.Count > liveMacs.Count)
        {
            var stale = new List<string>();
            foreach (var mac in _lastSent.Keys)
            {
                if (!liveMacs.Contains(mac)) stale.Add(mac);
            }
            foreach (var mac in stale)
            {
                _lastSent.Remove(mac);
                _lastPushTicks.Remove(mac);
            }
        }
    }

    // Rolling windows. Longer loops hitched the chain's frame decoder.
    internal const int WindowFrames = 30;
    internal const int WindowIntervalTicks = 53;
    private const int WindowPeriodMs = 450;
    private const int PendingTimeoutMs = 3 * WindowPeriodMs;
    // RF payloads per second a round of uploads may average; see LatePassAirBudgetPerSecond for the measured loss point.
    private const double WindowAirPerSecond = 200;
    // Air cost of one upload: header repeats plus two data passes, parts per LED measured on neonrain.
    private const double WindowHeaderCost = 8;
    private const double WindowPartsPerLed = 0.47;
    private const double MinRenderPeriodMs = 150;
    // A round of uploads must finish within this much of one window's playback.
    private const double WindowCycleMs = 750;
    private const int MaxWindowStretch = 6;
    private const int HealthLogPeriodMs = 60_000;
    private const int HealthAirSampleMs = 250;
    // A lower plan must hold this long, so a chain missing from one poll does not reset every chain.
    private const int StretchDownHoldMs = 10_000;
    // Live frames until a chain's brightness, prefs or power have settled, so a
    // window never bakes a change in late.
    private const int StateSettleMs = 300;

    // RF clock fit over recent GetMac samples. The RX clock runs near 1.6 ticks
    // per ms; a slope outside the bounds or a sample off the fit by more than
    // the residual limit means a reset clock or a stepped host clock.
    private const int ClockSampleMs = 250;
    private const int ClockSamples = 24;
    private const int MinClockSamples = 4;
    private const double MinTicksPerMs = 1.5;
    private const double MaxTicksPerMs = 1.7;
    private const double MaxClockResidualTicks = 64;

    // Last-frame margin: a chain goes live this many frames before its window would wrap.
    private const int WindowWrapMarginFrames = 2;
    // Render starts ahead of the request. An upload must wait until this first
    // frame is already playing: before that, the fan's loop index points at the
    // window's last (future) frames. Both margins are time, so they shrink in
    // frames as the interval stretches and a render stays usable within its period.
    private const double WindowLeadMs = 100;
    // Allow for clock-fit error at the boundary before replacing the old loop.
    private const double WindowStartSafetyMs = 66;
    private const double BaseFrameMs = WindowIntervalTicks / 1.6;
    internal static int LeadFramesFor(int stretch) => Math.Max(1, (int)Math.Round(WindowLeadMs / (BaseFrameMs * stretch)));
    internal static int SafetyFramesFor(int stretch) => Math.Max(1, (int)Math.Ceiling(WindowStartSafetyMs / (BaseFrameMs * stretch)));
    private int WindowLeadFrames => LeadFramesFor(_windowStretch);
    private int WindowStartSafetyFrames => SafetyFramesFor(_windowStretch);
    // A clock sample off the fit counts as an outlier; this many in a row mean the clock itself moved.
    private const int ClockOutliersToReset = 3;

    // Per chain: what it plays and the upload in flight.
    internal sealed class ChainRolling
    {
        public int StateSig;
        public double LiveUntilMs;
        public long WindowFirstFrame = long.MinValue;
        public long UploadedWindowId;
        public Task<bool>? Upload;
        public long UploadFirstFrame;
        public byte[]? UploadPayload;
        public int UploadLedCount;
        public byte[]? WindowPayload;
        public int WindowLedCount;
        public bool Failing;
        public bool Visited;
        public double LastUploadStartMs;
        public bool Joinable;
        public int UploadStretch;
        // Written by the upload task, read after it completes.
        public Slv3Hub.WindowUploadResult UploadResult;
        public int UploadDivisor = 1;
        public double RetryAtMs;
    }

    // Frame-count divisors a chain may step down through when its window compresses past the firmware cap.
    internal const int MaxWindowDivisor = 3;
    // A chain that cannot fit even at the floor is not retried for this long.
    private const double OversizeRetryMs = 10000;
    private readonly Dictionary<string, int> _windowDivisors = new();
    private readonly HashSet<string> _oversizeLogged = new();

    /// <summary>
    /// Resamples a full window (loop slot = absolute frame mod WindowFrames, first absolute frame
    /// <paramref name="firstFrame"/>) to WindowFrames / divisor frames, each playing divisor times longer,
    /// so the loop still spans the same wall-clock time. Loop slot = (frame / divisor) mod frame count.
    /// </summary>
    internal static byte[] ReduceWindow(byte[] full, int ledCount, long firstFrame, int divisor)
    {
        var count = WindowFrames / divisor;
        var stride = ledCount * 3;
        var reduced = new byte[count * stride];
        var firstGroup = (firstFrame + divisor - 1) / divisor;
        for (var i = 0; i < count; i++)
        {
            var group = firstGroup + i;
            var j = (int)Math.Clamp(group * divisor + (divisor - 1) / 2 - firstFrame, 0, WindowFrames - 1);
            Buffer.BlockCopy(full, WindowSlot(firstFrame + j) * stride, reduced, (int)(group % count) * stride, stride);
        }
        return reduced;
    }

    // Starts at the chain's remembered divisor and steps down while the firmware cap refuses the payload.
    internal async Task<bool> UploadWindowAsync(string macHex, ChainRolling st, byte[] full, int ledCount, long firstFrame, int startDivisor, double interval)
    {
        var result = Slv3Hub.WindowUploadResult.TooLarge;
        var used = startDivisor;
        for (var d = startDivisor; d <= MaxWindowDivisor; d++)
        {
            used = d;
            var payload = d == 1 ? full : ReduceWindow(full, ledCount, firstFrame, d);
            result = await _hub.SendRgbWindowResultAsync(macHex, payload, ledCount, WindowFrames / d, interval * d, PassThroughBrightnessPercent).ConfigureAwait(false);
            if (result != Slv3Hub.WindowUploadResult.TooLarge) break;
        }
        st.UploadResult = result;
        st.UploadDivisor = used;
        return result == Slv3Hub.WindowUploadResult.Sent;
    }

    // One look-ahead render per window serves every chain: all chains play on
    // the same RX clock and frame grid.
    private AheadRequest? _windowPending;
    private long _windowPendingFirstFrame;
    private double _windowRequestedAtMs;
    private double _nextWindowRequestMs;
    private DeviceFrame[][]? _windowFrames;
    private string[] _windowIds = Array.Empty<string>();
    private long _windowFirstFrame;
    private long _windowId;
    private long _windowStartedId;
    private int _windowStretch = 1;
    private double _windowRequestPeriodMs = WindowPeriodMs;
    private readonly Dictionary<string, IReadOnlyList<ResolvedZone>> _liveZones = new();
    private readonly HashSet<string> _liveEqual = new();
    private int _planChains;
    private int _planLeds;
    private RgbColor[] _pushBuffer = Array.Empty<RgbColor>();
    private double _healthNextLogMs;
    private int _healthUploadsOk;
    private int _healthUploadsFailed;
    private int _healthRenders;
    private int _healthUntaken;
    private double _healthNextAirMs;
    private double _stretchDownSinceMs;
    private int _healthAirMax;
    private long _healthTickMaxMs;
    private readonly HashSet<string> _rollingIds = new();

    private readonly Dictionary<string, ChainRolling> _rolling = new();
    private readonly List<(double Utc, double Rf)> _clock = new();
    private double _nextClockSampleMs;
    private int _clockOutliers;

    private static double UtcNowMs() => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / (double)TimeSpan.TicksPerMillisecond;

    /// <summary>Least-squares rf = a + b * utcMs; false until enough samples give a plausible slope.</summary>
    internal static bool TryFitClock(IReadOnlyList<(double Utc, double Rf)> samples, out double a, out double b)
    {
        a = 0;
        b = 0;
        if (samples.Count < MinClockSamples) return false;
        double mx = 0, my = 0;
        foreach (var (u, r) in samples) { mx += u; my += r; }
        mx /= samples.Count;
        my /= samples.Count;
        double sxy = 0, sxx = 0;
        foreach (var (u, r) in samples) { sxy += (u - mx) * (r - my); sxx += (u - mx) * (u - mx); }
        if (sxx <= 0) return false;
        b = sxy / sxx;
        a = my - b * mx;
        return b > MinTicksPerMs && b < MaxTicksPerMs;
    }

    /// <summary>Frame stretch and ms per round of uploads, so the round stays under the air budget and inside one window.</summary>
    internal static (int Stretch, double CycleMs) WindowPlan(int chains, int leds)
    {
        var airMs = (WindowHeaderCost * chains + WindowPartsPerLed * leds) * 1000 / WindowAirPerSecond;
        var cycleMs = Math.Max(Math.Max(WindowPeriodMs, airMs), MinRenderPeriodMs * chains);
        return (Math.Clamp((int)Math.Ceiling(cycleMs / WindowCycleMs), 1, MaxWindowStretch), cycleMs);
    }

    private void NotePlanned(DeviceStructure structure)
    {
        _planChains++;
        foreach (var segment in structure.Segments) _planLeds += segment.LedCount;
    }

    private void ApplyWindowPlan(double nowMs)
    {
        if (_planChains == 0) return;
        var (stretch, cycleMs) = WindowPlan(_planChains, _planLeds);
        _windowRequestPeriodMs = cycleMs / _planChains;
        if (stretch >= _windowStretch) _stretchDownSinceMs = 0;
        if (stretch == _windowStretch) return;
        if (stretch < _windowStretch)
        {
            if (_stretchDownSinceMs == 0) _stretchDownSinceMs = nowMs;
            if (nowMs - _stretchDownSinceMs < StretchDownHoldMs) return;
            _stretchDownSinceMs = 0;
        }
        ServiceLog.Info($"[lianli-wireless] plan stretch={stretch} cycleMs={cycleMs:F0} chains={_planChains} leds={_planLeds}");
        _windowStretch = stretch;
        DropSharedWindow();
        // Frame numbers and coverage are in units of the old interval.
        foreach (var st in _rolling.Values) st.WindowFirstFrame = long.MinValue;
    }

    private void LogHealth(double nowMs, int rollingChains)
    {
        if (_planChains == 0) return;
        if (nowMs >= _healthNextAirMs)
        {
            _healthNextAirMs = nowMs + HealthAirSampleMs;
            _healthAirMax = Math.Max(_healthAirMax, _hub.RecentAirSends());
        }
        if (nowMs < _healthNextLogMs) return;
        // A first call or a return from idle has no full period to report.
        if (_healthNextLogMs > 0 && nowMs - _healthNextLogMs < HealthLogPeriodMs)
        {
            ServiceLog.Info($"[lianli-wireless] health chains={_planChains} rolling={rollingChains} live={_planChains - rollingChains} uploads ok={_healthUploadsOk} fail={_healthUploadsFailed} renders={_healthRenders} untaken={_healthUntaken} airMax={_healthAirMax} tickMax={_healthTickMaxMs}");
        }
        _healthNextLogMs = nowMs + HealthLogPeriodMs;
        _healthUploadsOk = _healthUploadsFailed = _healthRenders = _healthUntaken = _healthAirMax = 0;
        _healthTickMaxMs = 0;
    }

    private double WindowInterval => WindowIntervalTicks * _windowStretch;

    /// <summary>Wall-clock ms at the middle of each window frame, first frame <paramref name="firstFrame"/>.</summary>
    internal static long[] WindowTickTimes(double a, double b, long firstFrame, double intervalTicks = WindowIntervalTicks)
    {
        var ticks = new long[WindowFrames];
        for (var k = 0; k < WindowFrames; k++)
        {
            ticks[k] = (long)Math.Round(((firstFrame + k + 0.5) * intervalTicks - a) / b);
        }
        return ticks;
    }

    /// <summary>The loop slot a chain plays for absolute frame <paramref name="frame"/>.</summary>
    internal static int WindowSlot(long frame) => (int)(frame % WindowFrames);

    private bool TryCurrentFrame(double nowMs, out long frame)
    {
        frame = 0;
        if (!TryFitClock(_clock, out var a, out var b)) return false;
        frame = (long)Math.Floor((a + b * nowMs) / WindowInterval);
        return true;
    }

    private void SampleClock(double nowMs)
    {
        if (nowMs < _nextClockSampleMs) return;
        _nextClockSampleMs = nowMs + ClockSampleMs;
        if (!_hub.TryReadRfClock(out var rf, out var utc)) return;
        if (_clock.Count > 0 && rf < _clock[^1].Rf)
        {
            // Frame numbers from the old clock would never reach a join range.
            _clock.Clear();
            DropSharedWindow();
        }
        else if (TryFitClock(_clock, out var a, out var b) && Math.Abs(rf - (a + b * utc)) > MaxClockResidualTicks)
        {
            if (++_clockOutliers < ClockOutliersToReset) return;
            _clock.Clear();
            DropSharedWindow();
        }
        _clockOutliers = 0;
        _clock.Add((utc, rf));
        if (_clock.Count > ClockSamples) _clock.RemoveAt(0);
    }

    private static int ChainStateSig(
        IReadOnlyList<string> ids, List<string> disabled, List<string> uncontrolled,
        Dictionary<string, LightingDevicePreference> prefs, MasterLevel globalBrightness)
    {
        var hc = new HashCode();
        hc.Add(globalBrightness);
        foreach (var id in ids)
        {
            hc.Add(disabled.Contains(id));
            hc.Add(uncontrolled.Contains(id));
            if (prefs.TryGetValue(id, out var p))
            {
                hc.Add(p.Brightness);
                hc.Add(p.Hue);
                hc.Add(p.Saturation);
                hc.Add(p.AdjustRed);
                hc.Add(p.AdjustGreen);
                hc.Add(p.AdjustBlue);
                hc.Add(p.AdjustTemperature);
                hc.Add(p.AdjustSaturation);
                hc.Add(p.ColorOrder);
            }
        }
        return hc.ToHashCode();
    }

    // Publishes a finished look-ahead render as the current shared window. A
    // render whose first half already played is dropped (its slots would be
    // stale once the loop wraps) and re-requested at once.
    private void AdvanceSharedWindow(double nowMs)
    {
        var req = _windowPending;
        if (req is null) return;
        if (!req.Done.IsSet)
        {
            if (nowMs - _windowRequestedAtMs > PendingTimeoutMs) DropSharedWindow();
            return;
        }
        _windowPending = null;
        if (req.Frames is null || !TryCurrentFrame(nowMs, out var current)
            || current - _windowPendingFirstFrame > WindowFrames / 2)
        {
            _nextWindowRequestMs = 0;
            return;
        }
        _windowFrames = req.Frames;
        _windowIds = req.DeviceIds;
        _windowFirstFrame = _windowPendingFirstFrame;
        _windowId++;
    }

    // Asks for the next window over every zone that rolled this tick.
    private void RequestSharedWindow(double nowMs)
    {
        if (_rollingIds.Count == 0)
        {
            DropSharedWindow();
            return;
        }
        SampleClock(nowMs);
        if (_windowPending is not null || nowMs < _nextWindowRequestMs) return;
        // A render no chain has taken yet stays up until it leaves its join range.
        if (_windowFrames is not null && _windowStartedId != _windowId
            && TryCurrentFrame(nowMs, out var playing) && playing < _windowFirstFrame + WindowFrames / 2)
        {
            return;
        }
        if (!TryFitClock(_clock, out var a, out var b)) return;
        _healthRenders++;
        if (_windowFrames is not null && _windowStartedId != _windowId) _healthUntaken++;
        _windowPendingFirstFrame = (long)Math.Floor((a + b * nowMs) / WindowInterval) + WindowLeadFrames;
        _windowRequestedAtMs = nowMs;
        _nextWindowRequestMs = nowMs + _windowRequestPeriodMs;
        var ids = new string[_rollingIds.Count];
        _rollingIds.CopyTo(ids);
        _windowPending = _engine.RequestAhead(ids, WindowTickTimes(a, b, _windowPendingFirstFrame, WindowInterval));
    }

    private void DropSharedWindow()
    {
        _windowPending?.Abandon();
        _windowPending = null;
        _windowFrames = null;
        _nextWindowRequestMs = 0;
    }

    internal void CollectUpload(string macHex, ChainRolling st)
    {
        if (st.Upload is not { IsCompleted: true } upload) return;
        st.Upload = null;
        var ok = upload.IsCompletedSuccessfully && upload.Result;
        var result = !upload.IsCompletedSuccessfully ? Slv3Hub.WindowUploadResult.Failed : st.UploadResult;
        if (result == Slv3Hub.WindowUploadResult.TooLarge)
        {
            // The firmware's size cap is a property of the content, not a link fault: live frames carry the chain.
            st.RetryAtMs = UtcNowMs() + OversizeRetryMs;
            st.Failing = false;
            if (_oversizeLogged.Add(macHex))
            {
                ServiceLog.Info($"[lianli-wireless-lighting-writer] {macHex} window does not fit the firmware cap even at {WindowFrames / MaxWindowDivisor} frames, streaming live");
            }
            return;
        }
        if (ok && st.UploadDivisor != _windowDivisors.GetValueOrDefault(macHex, 1))
        {
            _windowDivisors[macHex] = st.UploadDivisor;
            ServiceLog.Info($"[lianli-wireless-lighting-writer] {macHex} window stepped down to {WindowFrames / st.UploadDivisor} frames to fit the firmware cap");
        }
        if (ok)
        {
            _healthUploadsOk++;
            // A reduced loop starts on a frame group boundary.
            var d = st.UploadDivisor;
            st.WindowFirstFrame = st.UploadStretch == _windowStretch ? (st.UploadFirstFrame + d - 1) / d * d : long.MinValue;
            st.WindowPayload = st.UploadPayload;
            st.WindowLedCount = st.UploadLedCount;
            // The chain now plays the window, so the next live frame must go out even if it matches the last one sent.
            _lastSent.Remove(macHex);
        }
        else
        {
            _healthUploadsFailed++;
            if (!st.Failing) Console.Error.WriteLine($"[lianli-wireless-lighting-writer] {macHex} window upload failed");
        }
        st.Failing = !ok;
        var chainKey = Slv3LightingDeviceProvider.DeviceIdFor(macHex);
        if (ok) Nexus.Service.Lighting.LightingWriteHealth.Shared.NoteSuccess(chainKey, "lianli-wireless");
        else Nexus.Service.Lighting.LightingWriteHealth.Shared.NoteFailure(chainKey, "lianli-wireless");
    }

    /// <summary>
    /// Plays the engine's effect on a chain as rolling uploaded windows: the
    /// chain shows frame floor(rfClock / interval) mod N, so every window slot
    /// holds the effect rendered at the wall-clock time that slot will be on
    /// screen. False when the frame must stream live instead: the effect is not
    /// a pure function of time, something changed and has not settled, or the
    /// chain has no window that still covers the current frame.
    /// </summary>
    private bool TickRolling(
        string macHex, DeviceStructure structure, IReadOnlyList<ResolvedZone> zones,
        List<string> disabled, List<string> uncontrolled, Dictionary<string, LightingDevicePreference> prefs,
        MasterLevel globalBrightness, long nowTicks, double nowMs)
    {
        if (!_rolling.TryGetValue(macHex, out var st))
        {
            st = new ChainRolling();
            _rolling[macHex] = st;
        }
        st.Visited = true;
        CollectUpload(macHex, st);
        var ids = new List<string>(zones.Count);
        foreach (var z in zones)
        {
            if (!ids.Contains(z.Id)) ids.Add(z.Id);
        }
        var identifying = false;
        foreach (var id in ids)
        {
            identifying |= _identify.TryGetActive(id, nowTicks, out _);
        }
        var sig = ChainStateSig(ids, disabled, uncontrolled, prefs, globalBrightness);
        if (sig != st.StateSig || identifying)
        {
            st.StateSig = sig;
            st.LiveUntilMs = nowMs + StateSettleMs;
        }
        var canAhead = _engine.CanRenderAhead(ids);
        // Counted while settling too, so one chain's slider does not change everyone's plan.
        if (canAhead) NotePlanned(structure);
        if (nowMs < st.LiveUntilMs || !canAhead)
        {
            st.WindowFirstFrame = long.MinValue;
            st.Joinable = false;
            return false;
        }
        st.Joinable = _windowFrames is not null && CoversChain(ids);
        foreach (var id in ids)
        {
            _rollingIds.Add(id);
        }

        // One chain per render, longest-waiting first, spreads a round's uploads
        // over the air. A chain joining late skips a window that is mostly played already.
        if (st.Joinable && st.UploadedWindowId != _windowId && _windowStartedId != _windowId
            && TryCurrentFrame(nowMs, out var joinFrame)
            && joinFrame >= _windowFirstFrame + WindowStartSafetyFrames
            && joinFrame < _windowFirstFrame + WindowFrames / 2
            && nowMs >= st.RetryAtMs
            && IsNextToUpload(st))
        {
            st.UploadedWindowId = _windowId;
            _windowStartedId = _windowId;
            st.LastUploadStartMs = nowMs;
            if (ComposeWindow(macHex, structure, zones, disabled, uncontrolled, prefs, globalBrightness, nowTicks, out var payload, out var ledCount))
            {
                var divisor = _windowDivisors.GetValueOrDefault(macHex, 1);
                var first = _windowFirstFrame;
                st.UploadFirstFrame = first;
                st.UploadPayload = payload;
                st.UploadLedCount = ledCount;
                st.UploadStretch = _windowStretch;
                var interval = WindowInterval;
                // Off the writer thread, so other chains keep streaming while the upload runs.
                st.Upload = Task.Run(() => UploadWindowAsync(macHex, st, payload, ledCount, first, divisor, interval));
                return true;
            }
        }
        return st.WindowFirstFrame != long.MinValue
            && TryCurrentFrame(nowMs, out var current)
            && current >= st.WindowFirstFrame
            && current < st.WindowFirstFrame + WindowFrames - WindowWrapMarginFrames;
    }

    private bool IsNextToUpload(ChainRolling chain)
    {
        foreach (var other in _rolling.Values)
        {
            if (other.Joinable && other.Upload is not { IsCompleted: false } && other.LastUploadStartMs < chain.LastUploadStartMs)
            {
                return false;
            }
        }
        return true;
    }

    private void FreezeRollingChains()
    {
        if (_rolling.Count == 0) return;
        foreach (var mac in new List<string>(_rolling.Keys))
        {
            FreezeRollingChain(mac);
        }
    }

    // A chain that stops being streamed would loop its last window forever, so
    // it gets the frame it shows right now as a static frame instead.
    private void FreezeRollingChain(string macHex)
    {
        if (!_rolling.TryGetValue(macHex, out var st)) return;
        if (st.Upload is { IsCompleted: false })
        {
            st.Visited = true;
            return;
        }
        CollectUpload(macHex, st);
        _rolling.Remove(macHex);
        // Outside its window's coverage the chain was already streaming live frames.
        if (st.WindowFirstFrame == long.MinValue || st.WindowPayload is not { } payload
            || !TryCurrentFrame(UtcNowMs(), out var current)
            || current < st.WindowFirstFrame || current >= st.WindowFirstFrame + WindowFrames - WindowWrapMarginFrames)
        {
            return;
        }
        var colors = new RgbColor[st.WindowLedCount];
        var o = WindowSlot(current) * st.WindowLedCount * 3;
        for (var i = 0; i < colors.Length; i++, o += 3)
        {
            colors[i] = new RgbColor(payload[o], payload[o + 1], payload[o + 2]);
        }
        if (_hub.SendRgbFrame(macHex, colors, PassThroughBrightnessPercent, IntervalMs, out var effectIndexHex))
        {
            _lastSent[macHex] = (ComputeHash(colors), effectIndexHex);
            _lastPushTicks[macHex] = _nowTicks();
        }
    }

    private bool CoversChain(List<string> ids)
    {
        foreach (var id in ids)
        {
            if (Array.IndexOf(_windowIds, id) < 0) return false;
        }
        return true;
    }

    private bool ComposeWindow(
        string macHex, DeviceStructure structure, IReadOnlyList<ResolvedZone> zones,
        List<string> disabled, List<string> uncontrolled, Dictionary<string, LightingDevicePreference> prefs,
        MasterLevel globalBrightness, long nowTicks, out byte[] payload, out int ledCount)
    {
        payload = Array.Empty<byte>();
        ledCount = 0;
        for (var k = 0; k < WindowFrames; k++)
        {
            SegmentFrameComposer.EnsureBuffers(structure, ref _segmentBuffers);
            SegmentFrameComposer.Compose(
                structure, zones, _windowFrames![k], disabled, uncontrolled, prefs, globalBrightness, 1.0, nowTicks, _identify, _segmentBuffers);
            var total = FillWireBuffer(macHex, structure);
            if (total <= 0) return false;
            if (payload.Length == 0)
            {
                ledCount = total;
                payload = new byte[WindowFrames * total * 3];
            }
            var slot = WindowSlot(_windowFirstFrame + k);
            for (var i = 0; i < total; i++)
            {
                var o = (slot * total + i) * 3;
                payload[o] = _wireBuffer[i].R;
                payload[o + 1] = _wireBuffer[i].G;
                payload[o + 2] = _wireBuffer[i].B;
            }
        }
        return true;
    }

    // Fan-major wire layout of the composed segment buffers; 0 when the chain's
    // geometry is not known yet.
    private int FillWireBuffer(string macHex, DeviceStructure structure)
    {
        if (Slv3LightingDeviceProvider.IsSingleSegmentStructure(structure))
        {
            var cable = _segmentBuffers[0];
            EnsureWireBuffer(cable.Length);
            cable.CopyTo(_wireBuffer, 0);
            return cable.Length;
        }
        var fanInfo = FindFanInfo(macHex);
        if (fanInfo is null) return 0;
        if (Slv3LightingDeviceProvider.FlexFamilyOf(fanInfo) is { } flex)
        {
            return FillFlexWireBuffer(macHex, structure, flex);
        }
        // Ring lengths must match the provider's structure for this chain or
        // the fan-major interleave misaligns.
        var innerLen = Slv3LightingDeviceProvider.RingLedsFor(fanInfo);
        var outerLen = Slv3LightingDeviceProvider.OuterRingLedsFor(fanInfo);
        if (innerLen <= 0) return 0;
        var ledsPerFan = innerLen + outerLen;
        var fanCount = structure.Segments[Slv3LightingDeviceProvider.InnerSegment].LedCount / innerLen;
        if (fanCount <= 0) return 0;
        var totalLeds = fanCount * ledsPerFan;
        EnsureWireBuffer(totalLeds);
        var inner = _segmentBuffers[Slv3LightingDeviceProvider.InnerSegment];
        var outer = _segmentBuffers[Slv3LightingDeviceProvider.OuterSegment];
        for (var f = 0; f < fanCount; f++)
        {
            var baseIdx = f * ledsPerFan;
            for (var i = 0; i < innerLen; i++)
            {
                _wireBuffer[baseIdx + i] = inner[f * innerLen + i];
            }
            for (var i = 0; i < outerLen; i++)
            {
                _wireBuffer[baseIdx + innerLen + i] = outer[f * outerLen + i];
            }
        }
        return totalLeds;
    }

    // Zone-major segment buffers scattered into the fan-major wire buffer by the wired-Flex zone maps.
    private int FillFlexWireBuffer(string macHex, DeviceStructure structure, FlexFamily flex)
    {
        var zones = Slv3LightingDeviceProvider.FlexZonesOf(flex);
        var first = FlexProtocol.ZoneWireIndexes(flex, zones[0].Key).Count;
        if (first <= 0) return 0;
        var fanCount = structure.Segments[Slv3LightingDeviceProvider.InnerSegment].LedCount / first;
        var perFan = FlexProtocol.LedsPerFan(flex);
        if (fanCount <= 0) return 0;
        var totalLeds = fanCount * perFan;
        EnsureWireBuffer(totalLeds);
        var swap = flex == FlexFamily.TlFlex && _store.Load().Devices.FlexUnits.TryGetValue(macHex, out var look) && look.UpDownSwap;
        return ScatterFlexZones(flex, fanCount, _segmentBuffers, _wireBuffer, swap) ? totalLeds : 0;
    }

    // False when the buffers do not match the geometry: the structure and the fan list are read at different moments.
    internal static bool ScatterFlexZones(FlexFamily flex, int fanCount, RgbColor[][] zoneBuffers, RgbColor[] wire, bool swap = false)
    {
        var zones = Slv3LightingDeviceProvider.FlexZonesOf(flex);
        var perFan = FlexProtocol.LedsPerFan(flex);
        if (zoneBuffers.Length < zones.Length || wire.Length < fanCount * perFan)
        {
            return false;
        }
        for (var z = 0; z < zones.Length; z++)
        {
            var map = FlexProtocol.ZoneWireIndexes(flex, zones[z].Key);
            var buffer = zoneBuffers[z];
            if (buffer.Length < fanCount * map.Count)
            {
                return false;
            }
            for (var f = 0; f < fanCount; f++)
            {
                for (var k = 0; k < map.Count; k++)
                {
                    var idx = swap ? (map[k] + (perFan / 2)) % perFan : map[k];
                    wire[(f * perFan) + idx] = buffer[(f * map.Count) + k];
                }
            }
        }
        return true;
    }

    // Retry spacing and cap for the motherboard ARGB switch: the hub already
    // re-sends each request until the chain echoes it, so this only covers a
    // chain whose flag never follows (firmware without the switch).
    private const long ArgbRetryMs = 5_000;
    private const int MaxArgbRequests = 3;
    private readonly Dictionary<string, (bool Want, long AtMs, int Sent)> _argbRequests = new(StringComparer.Ordinal);

    // Brings each bound chain's motherboard ARGB flag to the user's saved
    // choice; a chain with no saved choice is left as it is.
    private void ReconcileMotherboardArgb(NexusSettings settings, long nowMs)
    {
        var chains = settings.Devices.LianLiWireless.Chains;
        var fans = _hub.State.Fans;
        if (_argbRequests.Count > 0)
        {
            // A chain that left the list gets a fresh budget when it returns.
            List<string>? gone = null;
            foreach (var mac in _argbRequests.Keys)
            {
                if (Array.FindIndex(fans, f => f.Mac == mac && f.BoundToUs && !f.Stale) < 0) (gone ??= new()).Add(mac);
            }
            if (gone is not null) foreach (var mac in gone) _argbRequests.Remove(mac);
        }
        foreach (var fan in fans)
        {
            if (!fan.BoundToUs || fan.Stale) continue;
            if (!chains.TryGetValue(fan.Mac, out var chain) || chain.MotherboardArgb is not { } want) continue;
            if (fan.PlayingMotherboardArgb == want)
            {
                _argbRequests.Remove(fan.Mac);
                continue;
            }
            // A reboot, identify or AIO switch waiting for its echo would be overwritten.
            if (_hub.HasOtherPendingCommand(fan.Mac, Slv3Protocol.RfArgbSyncSwitch)) continue;
            _argbRequests.TryGetValue(fan.Mac, out var last);
            var fresh = last.Sent == 0 || last.Want != want;
            if (!fresh && (last.Sent >= MaxArgbRequests || nowMs - last.AtMs < ArgbRetryMs)) continue;
            if (_hub.SetMotherboardArgb(fan.Mac, want))
            {
                _argbRequests[fan.Mac] = (want, nowMs, fresh ? 1 : last.Sent + 1);
                // Leaving the input, the chain shows nothing of ours until re-sent.
                if (!want) _lastSent.Remove(fan.Mac);
            }
        }
    }

    private Slv3FanInfo? FindFanInfo(string macHex)
    {
        foreach (var fan in _hub.State.Fans)
        {
            if (string.Equals(fan.Mac, macHex, StringComparison.OrdinalIgnoreCase))
            {
                return fan;
            }
        }
        return null;
    }

    /// <summary>
    /// Uploads a chain's pre-rendered animation when its settings, brightness
    /// or power changed, or when the echo shows the last upload was lost; the
    /// chain plays it on its own in between. False when the mode cannot be
    /// rendered, so the caller streams the engine frames instead.
    /// </summary>
    private bool TickPreset(
        string macHex, DeviceStructure structure, IReadOnlyList<ResolvedZone> zones, List<string> disabled,
        LianLiWirelessChainLighting lighting, MasterLevel globalBrightness, long nowTicks)
    {
        var fan = FindFanInfo(macHex);
        if (fan is null)
        {
            return true;
        }
        // An identify flash is composed into the engine frames, so it streams.
        foreach (var zone in zones)
        {
            if (_identify.TryGetActive(zone.Id, nowTicks, out _))
            {
                return false;
            }
        }
        var isStrimer = Slv3LightingDeviceProvider.IsStrimerStructure(structure);
        var (lanes, ledsPerLane) = Slv3Protocol.StrimerGeometryFor((byte)fan.DevType);
        var family = Slv3Protocol.ClassifyFanFamily((byte)fan.FanType);
        var fanCount = isStrimer
            ? 0
            : Slv3LightingDeviceProvider.FanCountOf(structure, fan);
        // A beacon with every port empty classifies the chain Unknown for one
        // poll; the uploaded loop keeps playing rather than being overwritten.
        if (!isStrimer && family == Slv3FanFamily.Unknown)
        {
            return true;
        }
        // CL fans pair a center with an outer ring of a different length,
        // which the uniform two-ring chain layout does not describe.
        var ledCount = isStrimer ? lanes * ledsPerLane : fanCount * Slv3Protocol.LedsPerFanFor(family);
        if (ledCount <= 0 || (!isStrimer && (Slv3Protocol.IsClFamily(family) || fanCount > Slv3FanEffects.MaxFans)))
        {
            return false;
        }

        var poweredOff = zones.Count > 0;
        foreach (var zone in zones)
        {
            if (!disabled.Contains(zone.Id))
            {
                poweredOff = false;
                break;
            }
        }
        // Already a product with master in both modes; the multiplier setting does not change it.
        var brightnessPercent = poweredOff || _engine.Blackout
            ? 0
            : (int)Math.Round(Math.Clamp(lighting.Brightness, 0, 4) * 25 * globalBrightness.Level);

        // One animation plays on the whole chain, so the first non-RGB zone order stands for it.
        string? presetOrder = null;
        var presetPrefs = _store.Load().Devices.LightingDevicePrefs;
        foreach (var zone in zones)
        {
            var zoneOrder = DeviceColorAdjust.OrderOf(presetPrefs, zone.Id);
            if (DeviceColorAdjust.NormalizeOrder(zoneOrder) is not (null or "RGB"))
            {
                presetOrder = zoneOrder;
                break;
            }
        }
        var sig = PresetSignature(lighting, brightnessPercent, ledCount, presetOrder);
        _lastSent.TryGetValue(macHex, out var last);
        var sinceLastPushMs = _lastPushTicks.TryGetValue(macHex, out var lastPush)
            ? (nowTicks - lastPush) / TimeSpan.TicksPerMillisecond
            : long.MaxValue;
        // A stale chain's echo is frozen while the RX reports no list, not a lost upload.
        var confirmed = fan.Stale ? "" : fan.EffectIndex;
        var lost = !string.IsNullOrEmpty(last.EffectIndexHex)
            && confirmed.Length > 0
            && sinceLastPushMs >= DriftConfirmWindowMs
            && !string.Equals(confirmed, last.EffectIndexHex, StringComparison.OrdinalIgnoreCase);
        if ((last.Hash == sig && !lost) || sinceLastPushMs < PresetMinPushIntervalMs)
        {
            return true;
        }

        Slv3StrimerAnimation animation;
        if (brightnessPercent == 0)
        {
            animation = new Slv3StrimerAnimation { Frames = new byte[ledCount * 3], FrameCount = 1, IntervalMs = IntervalMs };
        }
        else
        {
            try
            {
                animation = isStrimer
                    ? RenderStrimerPreset(lighting, lanes, ledsPerLane)
                    : Slv3FanEffects.Render(family, lighting.Mode, fanCount, lighting.Speed, lighting.Direction, ParseColors(lighting.Colors), lighting.Merge);
            }
            catch (ArgumentException)
            {
                return false;
            }
            if (DeviceColorAdjust.NormalizeOrder(presetOrder) is not (null or "RGB"))
            {
                // The renderer's frames may be shared, so permute a copy.
                var permuted = (byte[])animation.Frames.Clone();
                for (var i = 0; i + 2 < permuted.Length; i += 3)
                {
                    DeviceColorAdjust.ReorderChannels(presetOrder, ref permuted[i], ref permuted[i + 1], ref permuted[i + 2]);
                }
                animation = new Slv3StrimerAnimation { Frames = permuted, FrameCount = animation.FrameCount, IntervalMs = animation.IntervalMs };
            }
        }

        _lastPushTicks[macHex] = nowTicks;
        if (_hub.SendRgbAnimation(
                macHex, animation.Frames, ledCount, animation.FrameCount, animation.IntervalMs, brightnessPercent, out var sentEffectIndexHex))
        {
            _lastSent[macHex] = (sig, sentEffectIndexHex);
        }
        return true;
    }

    private static Slv3StrimerAnimation RenderStrimerPreset(LianLiWirelessChainLighting lighting, int lanes, int ledsPerLane)
    {
        if (lighting.Mode == LianLiWirelessChainLighting.ModePerLane)
        {
            var laneSettings = new List<(string Key, int Direction, RgbColor Color)>(lanes);
            for (var i = 0; i < lanes; i++)
            {
                var lane = i < lighting.Lanes.Count ? lighting.Lanes[i] : new LianLiWirelessLane();
                laneSettings.Add((lane.Mode, lane.Direction, ParseColor(lane.Color)));
            }
            return Slv3StrimerEffects.RenderPerLane(lanes, ledsPerLane, lighting.Speed, laneSettings);
        }
        return Slv3StrimerEffects.Render(lighting.Mode, lanes, ledsPerLane, lighting.Speed, lighting.Direction, ParseColors(lighting.Colors));
    }

    private static List<RgbColor> ParseColors(List<string> hexes)
    {
        var colors = new List<RgbColor>(hexes.Count);
        foreach (var hex in hexes)
        {
            colors.Add(ParseColor(hex));
        }
        return colors;
    }

    private static RgbColor ParseColor(string hex)
    {
        var s = hex.StartsWith('#') ? hex[1..] : hex;
        return s.Length == 6 && int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)
            ? new RgbColor((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new RgbColor(0, 0, 0);
    }

    private static int PresetSignature(LianLiWirelessChainLighting lighting, int brightnessPercent, int ledCount, string? order)
    {
        var hc = new HashCode();
        hc.Add(lighting.Mode);
        hc.Add(lighting.Speed);
        hc.Add(lighting.Direction);
        hc.Add(lighting.Merge);
        hc.Add(brightnessPercent);
        hc.Add(ledCount);
        hc.Add(order);
        foreach (var c in lighting.Colors)
        {
            hc.Add(c);
        }
        foreach (var lane in lighting.Lanes)
        {
            hc.Add(lane.Mode);
            hc.Add(lane.Direction);
            hc.Add(lane.Color);
        }
        return hc.ToHashCode();
    }

    private void EnsureWireBuffer(int totalLeds)
    {
        if (_wireBuffer.Length < totalLeds)
        {
            _wireBuffer = new RgbColor[totalLeds];
        }
    }

    private string FindConfirmedEffectIndex(string macHex)
    {
        var fans = _hub.State.Fans;
        for (var i = 0; i < fans.Length; i++)
        {
            if (string.Equals(fans[i].Mac, macHex, StringComparison.OrdinalIgnoreCase))
            {
                return fans[i].Stale ? "" : fans[i].EffectIndex;
            }
        }
        return "";
    }

    private static int ComputeHash(ReadOnlySpan<RgbColor> leds)
    {
        var hc = new HashCode();
        for (var i = 0; i < leds.Length; i++)
        {
            hc.Add(leds[i].R);
            hc.Add(leds[i].G);
            hc.Add(leds[i].B);
        }
        return hc.ToHashCode();
    }
}
