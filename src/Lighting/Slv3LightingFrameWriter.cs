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
        if (!_gates.Lighting) return;
        if (!_hub.IsConnected)
        {
            _lastSent.Clear();
            _lastPushTicks.Clear();
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
                continue;
            }
            if (settings.Devices.LianLiWireless.Chains.TryGetValue(macHex, out var chainLighting)
                && chainLighting.Mode != LianLiWirelessChainLighting.ModeCustom
                && TickPreset(macHex, structure, zones, disabled, chainLighting, globalBrightness, nowTicks))
            {
                continue;
            }

            if (Slv3Rolling.Enabled && TickRolling(macHex, structure, zones, disabled, uncontrolled, prefs, globalBrightness, nowTicks))
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

    private sealed class RollingState
    {
        public AheadRequest? Pending;
        public long FirstFrame;
        public double RequestedAtMs;
        public double NextRequestMs;
        public int Uploads;
        public double RenderMsSum;
        public double UploadMsSum;
        public double MaxLagMs;
    }

    private readonly Dictionary<string, RollingState> _rolling = new();
    private readonly List<(double Utc, double Rf)> _clock = new();
    private double _nextClockSampleMs;

    private static double UtcNowMs() => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / (double)TimeSpan.TicksPerMillisecond;

    // rf = A + B * utcMs over the last few GetMac samples.
    private bool TryClockFit(out double a, out double b)
    {
        a = 0;
        b = 0;
        if (_clock.Count < 4) return false;
        double mx = 0, my = 0;
        foreach (var (u, r) in _clock) { mx += u; my += r; }
        mx /= _clock.Count;
        my /= _clock.Count;
        double sxy = 0, sxx = 0;
        foreach (var (u, r) in _clock) { sxy += (u - mx) * (r - my); sxx += (u - mx) * (u - mx); }
        if (sxx <= 0) return false;
        b = sxy / sxx;
        a = my - b * mx;
        return b > 1.5 && b < 1.7;
    }

    private void SampleClock(double nowMs)
    {
        if (nowMs < _nextClockSampleMs) return;
        _nextClockSampleMs = nowMs + 250;
        if (_hub.TryReadRfClock(out var rf, out var utc))
        {
            if (_clock.Count > 0 && rf < _clock[^1].Rf) _clock.Clear();
            _clock.Add((utc, rf));
            if (_clock.Count > 24) _clock.RemoveAt(0);
        }
    }

    /// <summary>
    /// Plays the engine's effect on a chain as rolling uploaded windows: the
    /// chain shows frame floor(rfClock / interval) mod N, so every window slot
    /// holds the effect rendered at the wall-clock time that slot will be on
    /// screen. False when the effect cannot be rendered ahead (live stream).
    /// </summary>
    private bool TickRolling(
        string macHex, DeviceStructure structure, IReadOnlyList<ResolvedZone> zones,
        List<string> disabled, List<string> uncontrolled, Dictionary<string, LightingDevicePreference> prefs,
        float globalBrightness, long nowTicks)
    {
        var now = UtcNowMs();
        SampleClock(now);
        if (!_rolling.TryGetValue(macHex, out var st))
        {
            st = new RollingState();
            _rolling[macHex] = st;
        }
        var interval = Slv3Rolling.IntervalTicks;
        var n = Slv3Rolling.Frames;
        if (st.Pending is null)
        {
            if (now < st.NextRequestMs) return true;
            if (!TryClockFit(out var a, out var b)) return false;
            var ids = new List<string>();
            foreach (var z in zones) if (!ids.Contains(z.Id)) ids.Add(z.Id);
            st.FirstFrame = (long)Math.Floor((a + b * now) / interval);
            var ticks = new long[n];
            for (var k = 0; k < n; k++)
            {
                ticks[k] = (long)Math.Round(((st.FirstFrame + k + 0.5) * interval - a) / b + Slv3Rolling.OffsetMs);
            }
            st.RequestedAtMs = now;
            st.NextRequestMs = now + Slv3Rolling.PeriodMs;
            st.Pending = _engine.RequestAhead(ids.ToArray(), ticks);
            return true;
        }
        if (!st.Pending.Done.IsSet) return true;
        var req = st.Pending;
        st.Pending = null;
        if (req.Frames is null) return false;

        byte[]? window = null;
        var ledCount = 0;
        for (var k = 0; k < n; k++)
        {
            SegmentFrameComposer.EnsureBuffers(structure, ref _segmentBuffers);
            SegmentFrameComposer.Compose(
                structure, zones, req.Frames[k], disabled, uncontrolled, prefs, globalBrightness, 1.0, nowTicks, _identify, _segmentBuffers);
            var total = FillWireBuffer(macHex, structure);
            if (total <= 0) return false;
            ledCount = total;
            window ??= new byte[n * total * 3];
            var slot = (int)((st.FirstFrame + k) % n);
            for (var i = 0; i < total; i++)
            {
                var o = (slot * total + i) * 3;
                window[o] = _wireBuffer[i].R;
                window[o + 1] = _wireBuffer[i].G;
                window[o + 2] = _wireBuffer[i].B;
            }
        }
        var t0 = UtcNowMs();
        var ok = _hub.SendRgbAnimation(macHex, window!, ledCount, n, interval, PassThroughBrightnessPercent, out var effHex);
        var t1 = UtcNowMs();
        if (Slv3Rolling.Verbose)
        {
            var stride = ledCount * 3;
            var distinct = 1;
            for (var k = 1; k < n; k++)
            {
                if (!window.AsSpan(k * stride, stride).SequenceEqual(window.AsSpan((k - 1) * stride, stride))) distinct++;
            }
            TryClockFit(out var fa, out var fb);
            double maxRes = 0;
            foreach (var (u, r) in _clock) maxRes = Math.Max(maxRes, Math.Abs(r - (fa + fb * u)));
            Console.WriteLine(
                $"[lianli-wireless-rolling] up end={DateTime.UnixEpoch.AddMilliseconds(t1):HH:mm:ss.fff} req={DateTime.UnixEpoch.AddMilliseconds(st.RequestedAtMs):HH:mm:ss.fff} f0={st.FirstFrame} slot0={st.FirstFrame % n} distinct={distinct}/{n} render={req.RenderMs:F1} send={t1 - t0:F0} ok={ok} eff={effHex} rate={fb:F5} res={maxRes:F1} ticks0={req.TickMs[0] % 100000}");
        }
        st.Uploads++;
        st.RenderMsSum += req.RenderMs;
        st.UploadMsSum += t1 - t0;
        st.MaxLagMs = Math.Max(st.MaxLagMs, t1 - st.RequestedAtMs);
        if (!ok || st.Uploads % 20 == 0)
        {
            var windowMs = n * interval * 0.625;
            Console.WriteLine(
                $"[lianli-wireless-rolling] {macHex} uploads={st.Uploads} ok={ok} render={st.RenderMsSum / st.Uploads:F1}ms/{n}f upload={st.UploadMsSum / st.Uploads:F0}ms maxLag={st.MaxLagMs:F0}ms window={windowMs:F0}ms");
            st.MaxLagMs = 0;
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

/// <summary>Prototype switches for rolling-window playback (dev route only).</summary>
public static class Slv3Rolling
{
    public static volatile bool Enabled;
    public static volatile int IntervalTicks = 53;
    public static volatile int Frames = 30;
    public static volatile int PeriodMs = 500;
    public static volatile int OffsetMs;
    public static volatile bool Verbose;
}
