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
/// Streams engine frames to every bound SLV3 wireless fan chain as a
/// single-frame RF_RgbSync animation - the plan's "OpenRGB-style / live
/// direct mode" (plans/lianli-wireless-support.md section 2). There is no
/// firmware ROM-effect catalog exposed for wireless fans: every tick composes
/// each chain's resolved zone frames into a fan-major buffer (the family's
/// wire LED count per fan) and pushes it through
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
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var nowTicks = _nowTicks();

        var structures = _provider.BuildStructures();
        var liveMacs = new HashSet<string>(structures.Count);

        var minPushIntervalMs = MinPushIntervalMs * Math.Max(1, structures.Count);

        Slv3FanInfo? FindFanInfo(string macHex)
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
            SegmentFrameComposer.EnsureBuffers(structure, ref _segmentBuffers);
            SegmentFrameComposer.Compose(
                structure, zones, devices, disabled, uncontrolled, prefs, globalBrightness, 1.0, nowTicks, _identify, _segmentBuffers);

            // Ring length is family-dependent; it must match the provider's
            // structure for this chain or the fan-major interleave below
            // misaligns.
            var fanInfo = FindFanInfo(macHex);
            if (fanInfo is null) continue;
            var ringLen = Slv3LightingDeviceProvider.RingLedsFor(fanInfo);
            var ledsPerFan = ringLen * 2;
            var fanCount = structure.Segments[Slv3LightingDeviceProvider.InnerSegment].LedCount / ringLen;
            if (fanCount <= 0) continue;
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
                return fans[i].EffectIndex;
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
