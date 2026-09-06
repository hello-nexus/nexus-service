using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Smart;

/// <summary>
/// Streams engine canvas color to the network lights while an effect runs.
/// Unlike the serial/HID hub writers (which push every tick to keep hardware
/// refreshed), this only streams when <see cref="LightingEngine.CurrentEffect"/>
/// is active - network devices hold their last state, and hammering them when
/// idle wastes bandwidth and trips rate limits. Per-device coalescing + rate
/// ceilings live in <see cref="NetworkSendThrottle"/> (via the provider), so a
/// 33 ms tick here can't outrun a slow bridge. When an effect stops, lamps are
/// returned to their configured static color once.
/// </summary>
public sealed class SmartLightFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;

    private readonly LightingEngine _engine;
    private readonly SmartLightProvider _provider;
    private readonly IConfigStore _store;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _wasStreaming;
    private readonly FeatureGates _gates;

    public SmartLightFrameWriter(LightingEngine engine, SmartLightProvider provider, IConfigStore store, FeatureGates? gates = null)
    {
        _engine = engine;
        _provider = provider;
        _store = store;
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
                ServiceLog.Warn($"[smart-lights-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Tick()
    {
        if (!_gates.Lighting) return;
        if (_engine.CurrentEffect is null)
        {
            if (_wasStreaming)
            {
                _provider.StopStreamingSessions();
                _provider.RestoreStatic();
                _wasStreaming = false;
            }
            // Streamed-static devices (Govee razer/DreamView) only hold a color
            // while frames keep flowing - push every tick; the throttle paces the
            // wire to each device's interval. No-op when none are controlled.
            _provider.MaintainStreamedStatic();
            return;
        }

        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var global = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var streamed = false;

        for (var i = 0; i < devices.Length; i++)
        {
            var frame = devices[i];
            if (!_provider.Owns(frame.Id)) continue;
            if (frame.LedCount <= 0) continue;
            if (disabled.Contains(frame.Id)) continue;
            if (uncontrolled.Contains(frame.Id)) continue;

            var devBrightness = prefs.TryGetValue(frame.Id, out var pref) ? pref.Brightness : 100;
            var b01 = Math.Min(Math.Clamp(devBrightness, 0, 100) / 100f, global);
            // A bulb is exactly the device that will not match a strip, so the
            // colour trim has to reach here too. Brightness stays out of the
            // trimmed bytes: the provider applies b01 itself.
            var adjust = DeviceColorAdjust.For(pref);
            var bytes = adjust.IsIdentity ? frame.LedBytes : Trim(frame.LedBytes, frame.LedCount, adjust);
            _provider.SubmitEffectFrame(frame.Id, bytes, frame.LedCount, b01);
            streamed = true;
        }

        // Send this tick's batch to any session streamers (Hue Entertainment).
        _provider.FlushStreaming();
        if (streamed) _wasStreaming = true;
    }

    // Reused across ticks so a trimmed frame allocates nothing after the first.
    private byte[] _trimBuffer = Array.Empty<byte>();

    private ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> src, int ledCount, DeviceColorAdjust adjust)
    {
        var need = ledCount * 3;
        if (need > src.Length) need = src.Length - src.Length % 3;
        if (_trimBuffer.Length < need) _trimBuffer = new byte[need];
        for (var i = 0; i + 2 < need; i += 3)
        {
            adjust.Apply(src[i], src[i + 1], src[i + 2], 1.0, out var r, out var g, out var b);
            _trimBuffer[i] = r;
            _trimBuffer[i + 1] = g;
            _trimBuffer[i + 2] = b;
        }
        return new ReadOnlySpan<byte>(_trimBuffer, 0, need);
    }

}
