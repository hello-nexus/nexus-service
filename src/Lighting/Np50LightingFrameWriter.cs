using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the NP50 hub. Mirrors what
/// <see cref="Rgb.RgbBridge"/> does for OpenRGB devices, except instead of
/// PushFrameAsync over the OpenRGB SDK we batch by Nexus Link port and
/// call <see cref="Np50Hub.WriteLighting"/>.
///
/// The engine fires <see cref="LightingEngine.OnFrame"/> at ~30 fps after
/// <see cref="LightingEngine.SampleDevicesFromCanvas"/> has filled each
/// DeviceFrame's per-LED RGB bytes. We walk <see cref="LightingEngine.Devices"/>,
/// pick the np50:* frames in id order, assemble one buffer per port (with
/// the 6-LED hub logo prefixed onto port 1's stream), and write each port
/// in a single command. Brightness / disabled / identify are honored
/// against the same settings store OpenRGB devices read from.
/// </summary>
public sealed class Np50LightingFrameWriter : IHostedService, IDisposable
{
    private const int IdentifyFlashHalfPeriodMs = 250;
    private const string LogoIdSuffix = ":logo";
    private const string PortIdInfix = ":port";

    /// <summary>
    /// Writer tick period. 33 ms = 30 Hz, matching the engine's default
    /// frame interval, so we push at the same rate the canvas sampler
    /// updates. We drive our own timer (rather than subscribing to
    /// <see cref="LightingEngine.OnFrame"/>) so disabled zones still
    /// receive blank-out frames when no effect is active - otherwise
    /// "turn off" silently strands the hub at its last lit state.
    /// </summary>
    private const int TickPeriodMs = 33;

    private readonly LightingEngine _engine;
    private readonly Np50Hub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly FeatureGates _gates;

    // Per-port pending buffers, allocated lazily on first write so we don't
    // hold storage for empty ports.
    private readonly RgbColor[]?[] _portBuffers = new RgbColor[Np50Protocol.PortCount][];

    public Np50LightingFrameWriter(LightingEngine engine, Np50Hub hub, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
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
                Console.Error.WriteLine($"[np50-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    // Per-frame staging structures. Allocated once, cleared each tick.
    private readonly List<DeviceFrame>[] _stripsByPort =
    {
        new List<DeviceFrame>(),
        new List<DeviceFrame>(),
        new List<DeviceFrame>(),
    };
    private DeviceFrame? _logoFrame;

    internal void Tick()
    {
        if (!_gates.Lighting) return;
        if (!_hub.IsConnected) return;
        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var devicePrefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var nowTicks = DateTime.UtcNow.Ticks;

        // Classify each NP50 device frame this tick. We expect the engine's
        // device array to be the union of OpenRGB frames and our contributor
        // frames; the latter are already in port-then-daisy-chain order from
        // Np50LightingDeviceProvider.BuildFrames, so a single pass preserves
        // that order without sorting.
        _logoFrame = null;
        foreach (var l in _stripsByPort) l.Clear();
        var anyNp50Frame = false;
        var hubFullyUncontrolled = true;
        for (var i = 0; i < devices.Length; i++)
        {
            var dev = devices[i];
            if (!IsNp50Id(dev.Id)) continue;
            if (dev.LedCount <= 0) continue;
            anyNp50Frame = true;
            if (hubFullyUncontrolled && !uncontrolled.Contains(dev.Id)) hubFullyUncontrolled = false;
            var (port, isLogo) = ClassifyId(dev.Id);
            if (isLogo) { _logoFrame = dev; continue; }
            if (port < 1 || port > Np50Protocol.PortCount) continue;
            _stripsByPort[port - 1].Add(dev);
        }

        // Every zone uncontrolled: leave the hub alone entirely so firmware /
        // vendor lighting can take over. No port writes at all this tick.
        if (anyNp50Frame && hubFullyUncontrolled) return;

        // Always emit the full 4-port cycle (HYTE's CoolingHubBaseController.SendToHardware
        // iterates devicePort 0..3 regardless of which channels are populated).
        // Empty ports get a 7-byte header padded to 90 bytes by BuildLightingStream.
        // Skipping empty ports leaves firmware 2.0.5.1's lighting latch
        // un-committed (strips dark even with non-zero wire data).
        for (var p = 1; p <= Np50Protocol.LightingCyclePortCount; p++)
        {
            int total = 0;
            var strips = p <= Np50Protocol.PortCount ? _stripsByPort[p - 1] : null;
            if (p == 1 && _logoFrame is not null) total += Np50LightingDeviceProvider.LogoLedCount;
            if (strips is not null) foreach (var s in strips) total += s.LedCount;

            if (total > 0)
            {
                EnsurePortCapacity(p, total);
                var dstIdx = 0;
                if (p == 1 && _logoFrame is not null)
                {
                    CopyIntoBuffer(_portBuffers[p - 1]!, dstIdx, _logoFrame,
                        Math.Min(_logoFrame.LedCount, Np50LightingDeviceProvider.LogoLedCount),
                        settings, disabled, uncontrolled, devicePrefs, globalBrightness, nowTicks);
                    dstIdx += Np50LightingDeviceProvider.LogoLedCount;
                }
                if (strips is not null)
                {
                    foreach (var strip in strips)
                    {
                        CopyIntoBuffer(_portBuffers[p - 1]!, dstIdx, strip, strip.LedCount,
                            settings, disabled, uncontrolled, devicePrefs, globalBrightness, nowTicks);
                        dstIdx += strip.LedCount;
                    }
                }
                var span = new ReadOnlySpan<RgbColor>(_portBuffers[p - 1], 0, total);
                _hub.WriteLighting(p, span);
            }
            else
            {
                // Empty port: still send the framing-only header. BuildLightingStream
                // pads to 90 bytes which is what the firmware expects per HYTE.
                _hub.WriteLighting(p, ReadOnlySpan<RgbColor>.Empty);
            }
        }
    }

    private void CopyIntoBuffer(RgbColor[] dst, int dstStart, DeviceFrame frame, int writeLen,
        NexusSettings settings, IReadOnlyList<string> disabled, IReadOnlyList<string> uncontrolled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness, long nowTicks)
    {
        var brightnessMul = ComputeBrightnessMul(frame.Id, disabled, uncontrolled, prefs, globalBrightness, out var adjust);
        var hasIdentify = TryGetActiveIdentify(frame.Id, nowTicks, out var startTicks);
        FillBufferSlice(dst, dstStart, frame.LedBytes, writeLen, brightnessMul, adjust, hasIdentify, startTicks, nowTicks);
    }

    // ── Helpers ──

    private static bool IsNp50Id(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("np50:", StringComparison.Ordinal);

    private static (int port, bool isLogo) ClassifyId(string id)
    {
        if (id.EndsWith(LogoIdSuffix, StringComparison.Ordinal)) return (0, true);
        var portIdx = id.IndexOf(PortIdInfix, StringComparison.Ordinal);
        if (portIdx < 0) return (0, false);
        // Format: np50:<serial>:port<N>:dev<M>
        var after = id.AsSpan(portIdx + PortIdInfix.Length);
        var colon = after.IndexOf(':');
        if (colon <= 0) return (0, false);
        return int.TryParse(after.Slice(0, colon), out var port) ? (port, false) : (0, false);
    }

    private double ComputeBrightnessMul(string id, IReadOnlyList<string> disabled, IReadOnlyList<string> uncontrolled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs, float globalBrightness,
        out DeviceColorAdjust adjust)
    {
        adjust = DeviceColorAdjust.Identity;
        if (disabled.Count > 0)
        {
            foreach (var d in disabled) if (d == id) return 0.0;
        }
        if (uncontrolled.Count > 0)
        {
            foreach (var u in uncontrolled) if (u == id) return 0.0;
        }
        // One lookup feeds both the brightness and the colour trim.
        int devBrightness;
        try
        {
            if (prefs.TryGetValue(id, out var pref) && pref is not null)
            {
                devBrightness = pref.Brightness;
                adjust = DeviceColorAdjust.For(pref);
            }
            else
            {
                devBrightness = 100;
            }
        }
        catch (InvalidOperationException) { devBrightness = 100; }
        return Math.Min(Math.Clamp(devBrightness, 0, 100) / 100.0, globalBrightness);
    }

    private bool TryGetActiveIdentify(string id, long nowTicks, out long startTicks)
        => _identify.TryGetActive(id, nowTicks, out startTicks);

    internal static void FillBufferSlice(RgbColor[] dst, int dstStart, ReadOnlySpan<byte> src, int ledCount,
        double brightnessMul, DeviceColorAdjust adjust, bool hasIdentify, long identifyStartTicks, long nowTicks)
    {
        if (hasIdentify)
        {
            var elapsedMs = (nowTicks - identifyStartTicks) / TimeSpan.TicksPerMillisecond;
            var on = (elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0;
            var c = on ? new RgbColor(255, 255, 255) : new RgbColor(0, 0, 0);
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++) dst[dstStart + i] = c;
            return;
        }
        if (brightnessMul <= 0.0)
        {
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++) dst[dstStart + i] = default;
            return;
        }
        if (!adjust.IsIdentity)
        {
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length) break;
                adjust.Apply(src[off], src[off + 1], src[off + 2], brightnessMul,
                    out var ar, out var ag, out var ab);
                dst[dstStart + i] = new RgbColor(ar, ag, ab);
            }
            return;
        }
        if (brightnessMul >= 0.999)
        {
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length) break;
                dst[dstStart + i] = new RgbColor(src[off], src[off + 1], src[off + 2]);
            }
            return;
        }
        for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++)
        {
            var off = i * 3;
            if (off + 2 >= src.Length) break;
            dst[dstStart + i] = new RgbColor(
                (byte)(src[off] * brightnessMul),
                (byte)(src[off + 1] * brightnessMul),
                (byte)(src[off + 2] * brightnessMul));
        }
    }

    private void EnsurePortCapacity(int port, int total)
    {
        var idx = port - 1;
        var buf = _portBuffers[idx];
        if (buf is null || buf.Length < total)
        {
            _portBuffers[idx] = new RgbColor[Math.Max(total, 64)];
        }
    }
}
