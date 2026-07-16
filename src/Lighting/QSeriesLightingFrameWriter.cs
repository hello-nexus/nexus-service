using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hyte.QSeriesCooler;
using Nexus.Service.Persistence;
using QColor = Nexus.Service.Peripherals.Hyte.MiniHub.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the HYTE Q-series cooler. Same pattern as
/// <see cref="CnvsLightingFrameWriter"/>: own 30 Hz timer so disabled / effect-less
/// zones still receive blank frames; honour brightness / disabled / identify from
/// the shared settings store; one <see cref="QSeriesCoolerHub.WriteLighting"/> per tick.
///
/// Unlike CNVS there's no firmware-animation-off handshake: putting the cooler in
/// software RGB control mode (asserted inside the hub's WriteLighting on the first
/// frame after each connect) already suppresses the firmware animation.
/// </summary>
public sealed class QSeriesLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;
    private const int IdentifyFlashHalfPeriodMs = 250;

    private readonly LightingEngine _engine;
    private readonly QSeriesCoolerHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private QColor[]? _buffer;

    public QSeriesLightingFrameWriter(LightingEngine engine, QSeriesCoolerHub hub, IConfigStore store, Np50IdentifyTracker identify)
    {
        _engine = engine; _hub = hub; _store = store; _identify = identify;
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
            { Console.Error.WriteLine($"[qseries-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (!_hub.IsReadyForStreaming) return;

        var settings = _store.Load();
        var id = _hub.DeviceId;
        if (settings.Devices.UncontrolledLightingDevices.Contains(id)) return;

        var devices = _engine.Devices;
        DeviceFrame? frame = null;
        for (var i = 0; i < devices.Length; i++)
        { if (devices[i].Id == id) { frame = devices[i]; break; } }

        if (frame is null)
        {
            // No Q-series frame in the engine yet (lighting page never opened, or
            // RgbBridge hasn't rebuilt since connect). Keep the LEDs owned by us
            // with a blank push so the firmware can't re-assert its boot animation.
            PushBlank();
            return;
        }

        var disabled = settings.Devices.DisabledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var nowTicks = DateTime.UtcNow.Ticks;

        var brightnessMul = ComputeBrightnessMul(id, disabled, prefs, globalBrightness);
        var hasIdentify = _identify.TryGetActive(id, nowTicks, out var startTicks);

        var ledCount = Math.Min(frame.LedCount, QSeriesCoolerHub.LedCount);
        EnsureCapacity(QSeriesCoolerHub.LedCount);
        var dst = _buffer!;
        FillBufferSlice(dst, frame.LedBytes, ledCount, brightnessMul, hasIdentify, startTicks, nowTicks);
        for (var i = ledCount; i < QSeriesCoolerHub.LedCount; i++) dst[i] = default;

        _hub.WriteLighting(new ReadOnlySpan<QColor>(dst, 0, QSeriesCoolerHub.LedCount));
    }

    private void PushBlank()
    {
        EnsureCapacity(QSeriesCoolerHub.LedCount);
        var dst = _buffer!;
        for (var i = 0; i < QSeriesCoolerHub.LedCount; i++) dst[i] = default;
        _hub.WriteLighting(new ReadOnlySpan<QColor>(dst, 0, QSeriesCoolerHub.LedCount));
    }

    private void EnsureCapacity(int n)
    {
        if (_buffer is null || _buffer.Length < n) _buffer = new QColor[Math.Max(n, QSeriesCoolerHub.LedCount)];
    }

    private static double ComputeBrightnessMul(string id,
        IReadOnlyList<string> disabled,
        IReadOnlyDictionary<string, LightingDevicePreference> prefs,
        float globalBrightness)
    {
        if (disabled.Count > 0)
        {
            foreach (var d in disabled) if (d == id) return 0.0;
        }
        int devBrightness;
        try { devBrightness = prefs.TryGetValue(id, out var pref) ? pref.Brightness : 100; }
        catch (InvalidOperationException) { devBrightness = 100; }
        return Math.Min(Math.Clamp(devBrightness, 0, 100) / 100.0, globalBrightness);
    }

    private static void FillBufferSlice(QColor[] dst, ReadOnlySpan<byte> src, int ledCount,
        double brightnessMul, bool hasIdentify, long identifyStartTicks, long nowTicks)
    {
        if (hasIdentify)
        {
            var elapsedMs = (nowTicks - identifyStartTicks) / TimeSpan.TicksPerMillisecond;
            var on = (elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0;
            var c = on ? new QColor(255, 255, 255) : new QColor(0, 0, 0);
            for (var i = 0; i < ledCount && i < dst.Length; i++) dst[i] = c;
            return;
        }
        if (brightnessMul <= 0.0)
        {
            for (var i = 0; i < ledCount && i < dst.Length; i++) dst[i] = default;
            return;
        }
        // src is RGB triples (engine order); QColor(R,G,B) - the hub emits GRB on the wire.
        if (brightnessMul >= 0.999)
        {
            for (var i = 0; i < ledCount && i < dst.Length; i++)
            {
                var off = i * 3;
                if (off + 2 >= src.Length) break;
                dst[i] = new QColor(src[off], src[off + 1], src[off + 2]);
            }
            return;
        }
        for (var i = 0; i < ledCount && i < dst.Length; i++)
        {
            var off = i * 3;
            if (off + 2 >= src.Length) break;
            dst[i] = new QColor(
                (byte)(src[off] * brightnessMul),
                (byte)(src[off + 1] * brightnessMul),
                (byte)(src[off + 2] * brightnessMul));
        }
    }
}
