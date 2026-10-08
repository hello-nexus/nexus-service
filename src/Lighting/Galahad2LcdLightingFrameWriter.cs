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

/// <summary>Sends the ring's Lighting page colour to the Galahad II LCD when it changes.</summary>
public sealed class Galahad2LcdLightingFrameWriter : BackgroundService
{
    private const int TickMs = 200;

    private readonly LightingEngine _engine;
    private readonly Galahad2LcdAio _aio;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly FeatureGates _gates;
    private int _last = -1;

    public Galahad2LcdLightingFrameWriter(
        LightingEngine engine, Galahad2LcdAio aio, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
    {
        _engine = engine;
        _aio = aio;
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
                Tick();
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[{Galahad2LcdLightingProvider.DeviceId}] lighting tick failed: {ex.Message}");
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

    internal void Tick()
    {
        var id = Galahad2LcdLightingProvider.RingZoneId;
        var settings = _store.Load();
        // Uncontrolled: stop writing so the ring keeps whatever it last showed.
        if (!_gates.Lighting || !_aio.IsConnected || settings.Devices.UncontrolledLightingDevices.Contains(id))
        {
            _last = -1;
            return;
        }
        DeviceFrame? frame = null;
        foreach (var candidate in _engine.Devices)
        {
            if (candidate.Id == id) { frame = candidate; break; }
        }
        if (frame is null || frame.LedCount < 1)
        {
            return;
        }
        byte r = 0, g = 0, b = 0;
        if (_identify.TryGetActive(id, DateTime.UtcNow.Ticks, out _))
        {
            r = g = b = 255;
        }
        else if (!settings.Devices.DisabledLightingDevices.Contains(id))
        {
            settings.Devices.LightingDevicePrefs.TryGetValue(id, out var pref);
            var brightness = Math.Min(Math.Clamp(pref?.Brightness ?? 100, 0, 100) / 100.0, MasterBrightness.Effective(settings.Lighting));
            var src = frame.LedBytes;
            DeviceColorAdjust.For(pref).Apply(src[0], src[1], src[2], brightness, out r, out g, out b);
        }
        var color = (r << 16) | (g << 8) | b;
        if (color == _last)
        {
            return;
        }
        if (_aio.SetRing(r, g, b))
        {
            _last = color;
        }
    }
}
