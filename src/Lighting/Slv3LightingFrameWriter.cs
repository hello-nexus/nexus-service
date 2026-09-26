using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
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
            try { Tick(); }
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
            return;
        }
        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var settings = _store.Load();
        var globalBrightness = MasterBrightness.Effective(settings.Lighting);
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
                continue;
            }
            if (settings.Devices.LianLiWireless.Chains.TryGetValue(macHex, out var chainLighting)
                && chainLighting.Mode != LianLiWirelessChainLighting.ModeCustom
                && TickPreset(macHex, structure, zones, disabled, chainLighting, globalBrightness, nowTicks))
            {
                continue;
            }

            if (TickRolling(macHex, structure, zones, disabled, uncontrolled, prefs, globalBrightness, nowTicks, nowMs))
            {
                continue;
            }

            SegmentFrameComposer.EnsureBuffers(structure, ref _segmentBuffers);
            SegmentFrameComposer.Compose(
                structure, zones, devices, disabled, uncontrolled, prefs, globalBrightness, 1.0, nowTicks, _identify, _segmentBuffers);

            int totalLeds;
            if (Slv3LightingDeviceProvider.IsStrimerStructure(structure))
            {
                // The cable's one segment is the wire buffer, in wire order.
                var cable = _segmentBuffers[0];
                totalLeds = cable.Length;
                EnsureWireBuffer(totalLeds);
                cable.CopyTo(_wireBuffer, 0);
            }
            else
            {
                // Ring length is family-dependent; it must match the provider's
                // structure for this chain or the fan-major interleave below
                // misaligns.
                var fanInfo = FindFanInfo(macHex);
                if (fanInfo is null) continue;
                var ringLen = Slv3LightingDeviceProvider.RingLedsFor(fanInfo);
                var ledsPerFan = ringLen * 2;
                var fanCount = structure.Segments[Slv3LightingDeviceProvider.InnerSegment].LedCount / ringLen;
                if (fanCount <= 0) continue;
                totalLeds = fanCount * ledsPerFan;
                EnsureWireBuffer(totalLeds);

                var inner = _segmentBuffers[Slv3LightingDeviceProvider.InnerSegment];
                var outer = _segmentBuffers[Slv3LightingDeviceProvider.OuterSegment];
                for (var f = 0; f < fanCount; f++)
                {
                    var baseIdx = f * ledsPerFan;
                    for (var i = 0; i < ringLen; i++)
                    {
                        _wireBuffer[baseIdx + i] = inner[f * ringLen + i];
                        _wireBuffer[baseIdx + ringLen + i] = outer[f * ringLen + i];
                    }
                }
            }

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
                continue;
            }

            if (sinceLastPushMs < minPushIntervalMs)
            {
                continue;
            }

            if (_hub.SendRgbFrame(macHex, frameSpan, PassThroughBrightnessPercent, IntervalMs, out var sentEffectIndexHex))
            {
                _lastSent[macHex] = (hash, sentEffectIndexHex);
                _lastPushTicks[macHex] = nowTicks;
            }
        }

        RequestSharedWindow(nowMs);

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

    // Rolling windows. Longer loops hitched the chain's frame decoder; one frame
    // lasts one engine frame in RF clock ticks; the upload period stays under
    // half a window so one lost upload is still covered.
    internal const int WindowFrames = 30;
    internal const int WindowIntervalTicks = 53;
    private const int WindowPeriodMs = 450;
    private const int PendingTimeoutMs = 3 * WindowPeriodMs;
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
    // A window starts this many frames after the request, no later than the
    // quickest render-plus-upload lands, so no slot plays before it is sent.
    private const int WindowLeadFrames = 3;
    // A clock sample off the fit counts as an outlier; this many in a row mean the clock itself moved.
    private const int ClockOutliersToReset = 3;

    // Per chain: what it plays and the upload in flight.
    private sealed class ChainRolling
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

    /// <summary>Wall-clock ms at the middle of each window frame, first frame <paramref name="firstFrame"/>.</summary>
    internal static long[] WindowTickTimes(double a, double b, long firstFrame)
    {
        var ticks = new long[WindowFrames];
        for (var k = 0; k < WindowFrames; k++)
        {
            ticks[k] = (long)Math.Round(((firstFrame + k + 0.5) * WindowIntervalTicks - a) / b);
        }
        return ticks;
    }

    /// <summary>The loop slot a chain plays for absolute frame <paramref name="frame"/>.</summary>
    internal static int WindowSlot(long frame) => (int)(frame % WindowFrames);

    private bool TryCurrentFrame(double nowMs, out long frame)
    {
        frame = 0;
        if (!TryFitClock(_clock, out var a, out var b)) return false;
        frame = (long)Math.Floor((a + b * nowMs) / WindowIntervalTicks);
        return true;
    }

    private void SampleClock(double nowMs)
    {
        if (nowMs < _nextClockSampleMs) return;
        _nextClockSampleMs = nowMs + ClockSampleMs;
        if (!_hub.TryReadRfClock(out var rf, out var utc)) return;
        if (_clock.Count > 0 && rf < _clock[^1].Rf)
        {
            _clock.Clear();
        }
        else if (TryFitClock(_clock, out var a, out var b) && Math.Abs(rf - (a + b * utc)) > MaxClockResidualTicks)
        {
            if (++_clockOutliers < ClockOutliersToReset) return;
            _clock.Clear();
        }
        _clockOutliers = 0;
        _clock.Add((utc, rf));
        if (_clock.Count > ClockSamples) _clock.RemoveAt(0);
    }

    private static int ChainStateSig(
        IReadOnlyList<string> ids, List<string> disabled, List<string> uncontrolled,
        Dictionary<string, LightingDevicePreference> prefs, float globalBrightness)
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
        if (!TryFitClock(_clock, out var a, out var b)) return;
        _windowPendingFirstFrame = (long)Math.Floor((a + b * nowMs) / WindowIntervalTicks) + WindowLeadFrames;
        _windowRequestedAtMs = nowMs;
        _nextWindowRequestMs = nowMs + WindowPeriodMs;
        var ids = new string[_rollingIds.Count];
        _rollingIds.CopyTo(ids);
        _windowPending = _engine.RequestAhead(ids, WindowTickTimes(a, b, _windowPendingFirstFrame));
    }

    private void DropSharedWindow()
    {
        _windowPending?.Abandon();
        _windowPending = null;
        _windowFrames = null;
        _nextWindowRequestMs = 0;
    }

    private void CollectUpload(string macHex, ChainRolling st)
    {
        if (st.Upload is not { IsCompleted: true } upload) return;
        st.Upload = null;
        var ok = upload.IsCompletedSuccessfully && upload.Result;
        if (ok)
        {
            st.WindowFirstFrame = st.UploadFirstFrame;
            st.WindowPayload = st.UploadPayload;
            st.WindowLedCount = st.UploadLedCount;
            // The chain now plays the window, so the next live frame must go out even if it matches the last one sent.
            _lastSent.Remove(macHex);
        }
        else if (!st.Failing)
        {
            Console.Error.WriteLine($"[lianli-wireless-lighting-writer] {macHex} window upload failed");
        }
        st.Failing = !ok;
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
        float globalBrightness, long nowTicks, double nowMs)
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
        if (nowMs < st.LiveUntilMs || !_engine.CanRenderAhead(ids))
        {
            st.WindowFirstFrame = long.MinValue;
            return false;
        }
        foreach (var id in ids)
        {
            _rollingIds.Add(id);
        }

        // A chain joining late skips a window that is mostly played already.
        if (_windowFrames is not null && st.UploadedWindowId != _windowId && CoversChain(ids)
            && TryCurrentFrame(nowMs, out var playing) && playing < _windowFirstFrame + WindowFrames / 2)
        {
            st.UploadedWindowId = _windowId;
            if (ComposeWindow(macHex, structure, zones, disabled, uncontrolled, prefs, globalBrightness, nowTicks, out var payload, out var ledCount))
            {
                st.UploadFirstFrame = _windowFirstFrame;
                st.UploadPayload = payload;
                st.UploadLedCount = ledCount;
                // Off the writer thread, so other chains keep streaming while the upload runs.
                st.Upload = Task.Run(() => _hub.SendRgbWindowAsync(macHex, payload, ledCount, WindowFrames, WindowIntervalTicks, PassThroughBrightnessPercent));
                return true;
            }
        }
        return st.WindowFirstFrame != long.MinValue
            && TryCurrentFrame(nowMs, out var current)
            && current < st.WindowFirstFrame + WindowFrames - WindowWrapMarginFrames;
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
        float globalBrightness, long nowTicks, out byte[] payload, out int ledCount)
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
        if (Slv3LightingDeviceProvider.IsStrimerStructure(structure))
        {
            var cable = _segmentBuffers[0];
            EnsureWireBuffer(cable.Length);
            cable.CopyTo(_wireBuffer, 0);
            return cable.Length;
        }
        var fanInfo = FindFanInfo(macHex);
        if (fanInfo is null) return 0;
        var ringLen = Slv3LightingDeviceProvider.RingLedsFor(fanInfo);
        var ledsPerFan = ringLen * 2;
        var fanCount = structure.Segments[Slv3LightingDeviceProvider.InnerSegment].LedCount / ringLen;
        if (fanCount <= 0) return 0;
        var totalLeds = fanCount * ledsPerFan;
        EnsureWireBuffer(totalLeds);
        var inner = _segmentBuffers[Slv3LightingDeviceProvider.InnerSegment];
        var outer = _segmentBuffers[Slv3LightingDeviceProvider.OuterSegment];
        for (var f = 0; f < fanCount; f++)
        {
            var baseIdx = f * ledsPerFan;
            for (var i = 0; i < ringLen; i++)
            {
                _wireBuffer[baseIdx + i] = inner[f * ringLen + i];
                _wireBuffer[baseIdx + ringLen + i] = outer[f * ringLen + i];
            }
        }
        return totalLeds;
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
        LianLiWirelessChainLighting lighting, float globalBrightness, long nowTicks)
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
            : structure.Segments[Slv3LightingDeviceProvider.InnerSegment].LedCount / Slv3LightingDeviceProvider.RingLedsFor(fan);
        // A beacon with every port empty classifies the chain Unknown for one
        // poll; the uploaded loop keeps playing rather than being overwritten.
        if (!isStrimer && family == Slv3FanFamily.Unknown)
        {
            return true;
        }
        // CL fans pair a center with an outer ring of a different length,
        // which the uniform two-ring chain layout does not describe.
        var ledCount = isStrimer ? lanes * ledsPerLane : fanCount * Slv3Protocol.LedsPerFanFor(family);
        if (ledCount <= 0 || (!isStrimer && (family == Slv3FanFamily.Cl || fanCount > Slv3FanEffects.MaxFans)))
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
        var brightnessPercent = poweredOff || _engine.Blackout
            ? 0
            : (int)Math.Round(Math.Clamp(lighting.Brightness, 0, 4) * 25 * globalBrightness);

        var sig = PresetSignature(lighting, brightnessPercent, ledCount);
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

    private static int PresetSignature(LianLiWirelessChainLighting lighting, int brightnessPercent, int ledCount)
    {
        var hc = new HashCode();
        hc.Add(lighting.Mode);
        hc.Add(lighting.Speed);
        hc.Add(lighting.Direction);
        hc.Add(lighting.Merge);
        hc.Add(brightnessPercent);
        hc.Add(ledCount);
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
