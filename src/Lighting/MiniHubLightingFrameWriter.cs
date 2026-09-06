using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hyte.MiniHub;
using Nexus.Service.Persistence;
using MiniHubColor = Nexus.Service.Peripherals.Hyte.MiniHub.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the MiniHub. Same pattern as
/// <see cref="Np50LightingFrameWriter"/>: own 30 Hz timer so disabled
/// zones still receive blank frames when no effect is running; reads
/// brightness / disabled / identify from the shared settings store.
/// One <c>WriteLighting</c> call per LED channel per tick.
/// </summary>
public sealed class MiniHubLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;
    private const int IdentifyFlashHalfPeriodMs = 250;

    private readonly LightingEngine _engine;
    private readonly MiniHubHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private readonly MiniHubColor[]?[] _portBuffers = new MiniHubColor[MiniHubProtocol.LedPortCount][];

    private readonly FeatureGates _gates;

    public MiniHubLightingFrameWriter(LightingEngine engine, MiniHubHub hub, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
    {
        _engine = engine; _hub = hub; _store = store; _identify = identify;
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
            { Console.Error.WriteLine($"[minihub-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (!_gates.Lighting) return;
        if (!_hub.IsConnected) return;
        var devices = _engine.Devices;
        if (devices.Length == 0) return;
        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var nowTicks = DateTime.UtcNow.Ticks;

        var hubId = _hub.DeviceId;

        // Every port uncontrolled: leave the whole hub alone so it drops back to
        // its firmware animation, same as never pushing at all.
        if (uncontrolled.Count > 0
            && uncontrolled.Contains($"{hubId}:port1") && uncontrolled.Contains($"{hubId}:port2")
            && uncontrolled.Contains($"{hubId}:port3") && uncontrolled.Contains($"{hubId}:port4"))
        {
            return;
        }

        // Push every channel every tick - even channels with zero declared
        // LEDs get a padded blank frame, which the hub firmware honours by
        // blacking out the strip. The per-channel padded buffer comes from
        // MiniHubProtocol.BuildLightingStream, so each WriteLighting call
        // emits exactly 307 bytes (channel 4) or 157 bytes (channels 1-3)
        // regardless of how many LEDs the user has wired.
        TryPushZone(devices, $"{hubId}:port1", channel: 1, disabled, uncontrolled, prefs, globalBrightness, nowTicks);
        TryPushZone(devices, $"{hubId}:port2", channel: 2, disabled, uncontrolled, prefs, globalBrightness, nowTicks);
        TryPushZone(devices, $"{hubId}:port3", channel: 3, disabled, uncontrolled, prefs, globalBrightness, nowTicks);
        TryPushZone(devices, $"{hubId}:port4", channel: 4, disabled, uncontrolled, prefs, globalBrightness, nowTicks);
    }

    private void TryPushZone(DeviceFrame[] devices, string id, int channel,
        System.Collections.Generic.IReadOnlyList<string> disabled,
        System.Collections.Generic.IReadOnlyList<string> uncontrolled,
        System.Collections.Generic.IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness, long nowTicks)
    {
        DeviceFrame? frame = null;
        for (var i = 0; i < devices.Length; i++)
        { if (devices[i].Id == id) { frame = devices[i]; break; } }
        // Always push something - even a zero-LED frame goes out as a fully
        // zero-padded buffer, which BuildLightingStream then sends to the
        // hub. Skipping a channel means the hub eventually drops back to
        // its firmware animation on that strip. Cheap: BuildLightingStream
        // produces a fixed 157/307-byte frame regardless of declared count.
        var ledCount = frame is null ? 0 : frame.LedCount;
        var brightnessMul = ComputeBrightnessMul(id, disabled, uncontrolled, prefs, globalBrightness, out var adjust);
        var hasIdentify = _identify.TryGetActive(id, nowTicks, out var startTicks);

        var idx = channel - 1;
        var buf = _portBuffers[idx];
        if (buf is null || buf.Length < Math.Max(ledCount, 1))
            _portBuffers[idx] = new MiniHubColor[Math.Max(ledCount, 64)];
        var dst = _portBuffers[idx]!;
        if (ledCount > 0 && frame is not null)
        {
            FillBufferSlice(dst, 0, frame.LedBytes, ledCount, brightnessMul, adjust, hasIdentify, startTicks, nowTicks);
        }
        _hub.WriteLighting(channel, new ReadOnlySpan<MiniHubColor>(dst, 0, ledCount));
    }

    private static double ComputeBrightnessMul(string id,
        System.Collections.Generic.IReadOnlyList<string> disabled,
        System.Collections.Generic.IReadOnlyList<string> uncontrolled,
        System.Collections.Generic.IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness,
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

    private static void FillBufferSlice(MiniHubColor[] dst, int dstStart, ReadOnlySpan<byte> src, int ledCount,
        double brightnessMul, DeviceColorAdjust adjust, bool hasIdentify, long identifyStartTicks, long nowTicks)
    {
        if (hasIdentify)
        {
            var elapsedMs = (nowTicks - identifyStartTicks) / TimeSpan.TicksPerMillisecond;
            var on = (elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0;
            var c = on ? new MiniHubColor(255, 255, 255) : new MiniHubColor(0, 0, 0);
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
                dst[dstStart + i] = new MiniHubColor(ar, ag, ab);
            }
            return;
        }
        if (brightnessMul >= 0.999)
        {
            for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length) break;
                dst[dstStart + i] = new MiniHubColor(src[off], src[off + 1], src[off + 2]);
            }
            return;
        }
        for (var i = 0; i < ledCount && dstStart + i < dst.Length; i++)
        {
            var off = i * 3;
            if (off + 2 >= src.Length) break;
            dst[dstStart + i] = new MiniHubColor(
                (byte)(src[off] * brightnessMul),
                (byte)(src[off + 1] * brightnessMul),
                (byte)(src[off + 2] * brightnessMul));
        }
    }
}
