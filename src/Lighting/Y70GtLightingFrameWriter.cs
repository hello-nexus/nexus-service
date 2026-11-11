using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to the Y70 GT backlight. Same pattern as
/// <see cref="QSeriesLightingFrameWriter"/>: own frame-rate timer so a disabled card
/// still receives blank frames, with brightness / disabled / identify taken
/// from the shared settings store.
/// </summary>
public sealed class Y70GtLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;
    private const int IdentifyFlashHalfPeriodMs = 250;

    private readonly LightingEngine _engine;
    private readonly Y70DisplayHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly FeatureGates _gates;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly byte[] _buffer = new byte[Y70DisplayProtocol.BacklightLedCount * 3];

    public Y70GtLightingFrameWriter(LightingEngine engine, Y70DisplayHub hub, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
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
            { Console.Error.WriteLine($"[y70gt-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    internal void Tick()
    {
        if (!_gates.Lighting) return;
        if (!_hub.HasBacklight) return;

        var settings = _store.Load();
        var id = _hub.BacklightDeviceId;
        if (string.IsNullOrEmpty(id) || settings.Devices.UncontrolledLightingDevices.Contains(id)) return;

        DeviceFrame? frame = null;
        var devices = _engine.Devices;
        for (var i = 0; i < devices.Length; i++)
        {
            if (devices[i].Id == id) { frame = devices[i]; break; }
        }

        Array.Clear(_buffer);
        if (frame is not null)
        {
            var brightnessMul = QSeriesLightingFrameWriter.ComputeBrightnessMul(
                id, settings.Devices.DisabledLightingDevices, settings.Devices.LightingDevicePrefs,
                MasterBrightness.Resolve(settings), out var adjust);
            var nowTicks = DateTime.UtcNow.Ticks;
            if (_identify.TryGetActive(id, nowTicks, out var startTicks))
            {
                var elapsedMs = (nowTicks - startTicks) / TimeSpan.TicksPerMillisecond;
                if ((elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0) Array.Fill(_buffer, (byte)255);
            }
            else if (brightnessMul > 0.0)
            {
                Fill(_buffer, frame.LedBytes, Math.Min(frame.LedCount, Y70DisplayProtocol.BacklightLedCount), brightnessMul, adjust);
            }
            _engine.Live.Report(id, frame.LedBytes, Math.Min(frame.LedCount, Y70DisplayProtocol.BacklightLedCount), brightnessMul, adjust,
                _identify.TryGetActive(id, nowTicks, out var liveStart), liveStart, nowTicks, IdentifyFlashHalfPeriodMs);
        }
        _hub.WriteBacklight(_buffer);
    }

    internal static void Fill(byte[] dst, ReadOnlySpan<byte> src, int ledCount, double brightnessMul, DeviceColorAdjust adjust)
    {
        for (var i = 0; i < ledCount; i++)
        {
            var off = i * 3;
            if (off + 2 >= src.Length) break;
            if (adjust.IsIdentity)
            {
                dst[off] = (byte)(src[off] * brightnessMul);
                dst[off + 1] = (byte)(src[off + 1] * brightnessMul);
                dst[off + 2] = (byte)(src[off + 2] * brightnessMul);
            }
            else
            {
                adjust.Apply(src[off], src[off + 1], src[off + 2], brightnessMul, out var r, out var g, out var b);
                dst[off] = r; dst[off + 1] = g; dst[off + 2] = b;
            }
        }
    }
}
