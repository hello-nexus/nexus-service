using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes the engine's per-LED output for the HydroShift LCD 360R fans. The writes share the
/// panel's handle with the LCD stream, so the rate is capped and an unchanged frame is only
/// resent as a slow reassert.
/// </summary>
public sealed class HydroShiftLcdLightingFrameWriter : BackgroundService
{
    /// <summary>Caps the write rate; each write holds the hub lock the frame chunks also need.</summary>
    internal const int TickMs = 50;

    /// <summary>A firmware that dropped a write is healed within this window even when the look is static.</summary>
    internal const int ReassertMs = 2500;

    private const int IdentifyFlashHalfPeriodMs = 250;

    private const int LedBytes = LianLiAioProtocol.HydroShiftFanLedCount * 3;

    private readonly LightingEngine _engine;
    private readonly HydroShiftFanLeds _leds;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly FeatureGates _gates;
    private readonly byte[] _payload = new byte[LedBytes];
    private readonly byte[] _last = new byte[LedBytes];
    private long _lastSentMs = long.MinValue / 2;
    private bool _haveLast;

    public HydroShiftLcdLightingFrameWriter(
        LightingEngine engine, HydroShiftFanLeds leds, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
    {
        _engine = engine;
        _leds = leds;
        _store = store;
        _identify = identify;
        _gates = gates ?? FeatureGates.AllEnabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick(Environment.TickCount64);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[{HydroShiftLcdLightingProvider.DeviceId}] lighting tick failed: {ex.Message}");
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal void Tick(long nowMs)
    {
        _leds.Poll();
        var id = HydroShiftLcdLightingProvider.FansZoneId;
        var settings = _store.Load();
        // Uncontrolled: stop writing so the fans keep whatever they last showed.
        if (!_gates.Lighting || !_leds.IsConnected || settings.Devices.UncontrolledLightingDevices.Contains(id))
        {
            _haveLast = false;
            return;
        }
        DeviceFrame? frame = null;
        foreach (var candidate in _engine.Devices)
        {
            if (candidate.Id == id) { frame = candidate; break; }
        }
        if (frame is null || frame.LedCount < LianLiAioProtocol.HydroShiftFanLedCount)
        {
            return;
        }
        Array.Clear(_payload);
        if (_identify.TryGetActive(id, DateTime.UtcNow.Ticks, out var startTicks))
        {
            var elapsedMs = (DateTime.UtcNow.Ticks - startTicks) / TimeSpan.TicksPerMillisecond;
            if ((elapsedMs / IdentifyFlashHalfPeriodMs) % 2 == 0)
            {
                Array.Fill(_payload, (byte)255);
            }
        }
        else if (!settings.Devices.DisabledLightingDevices.Contains(id))
        {
            settings.Devices.LightingDevicePrefs.TryGetValue(id, out var pref);
            var brightness = MasterBrightness.Resolve(settings.Lighting).Combine(Math.Clamp(pref?.Brightness ?? 100, 0, 100) / 100.0);
            var adjust = DeviceColorAdjust.For(pref);
            var src = frame.LedBytes;
            for (int i = 0; i < LedBytes && i + 2 < src.Length; i += 3)
            {
                adjust.Apply(src[i], src[i + 1], src[i + 2], brightness, out var r, out var g, out var b);
                _payload[i] = r;
                _payload[i + 1] = g;
                _payload[i + 2] = b;
            }
        }
        if (_haveLast && nowMs - _lastSentMs < ReassertMs && _payload.AsSpan().SequenceEqual(_last))
        {
            return;
        }
        if (_leds.Write(_payload))
        {
            _payload.CopyTo(_last, 0);
            _haveLast = true;
            _lastSentMs = nowMs;
        }
        else
        {
            _haveLast = false;
        }
    }
}
