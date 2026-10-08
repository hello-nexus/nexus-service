using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Streams engine output to the HydroShift II OLED Curved edge LEDs. The board holds a frame
/// until the next, so only changed frames go out.
/// </summary>
public sealed class HydroShift2CurveLightingFrameWriter : BackgroundService
{
    private const int TickMs = 33;

    private readonly LightingEngine _engine;
    private readonly HydroShift2CurveBoard _board;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly FeatureGates _gates;
    private readonly DeviceStructure _structure = HydroShift2CurveLightingDeviceProvider.BuildStructure();
    private readonly byte[] _rgb = new byte[HydroShift2CurveProtocol.LedCount * 3];
    private RgbColor[][] _segBuf = Array.Empty<RgbColor[]>();

    public HydroShift2CurveLightingFrameWriter(
        LightingEngine engine, HydroShift2CurveBoard board, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
    {
        _engine = engine;
        _board = board;
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
                ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] lighting tick failed: {ex.Message}");
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

    private void Tick()
    {
        if (!_gates.Lighting || !_board.IsAvailable) return;
        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var settings = _store.Load();
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var zones = ZoneResolution.Resolve(_structure, settings);
        // Left alone entirely, the board keeps whatever another app last sent.
        if (ZoneResolution.IsFullyUncontrolled(zones, uncontrolled)) return;

        SegmentFrameComposer.EnsureBuffers(_structure, ref _segBuf);
        SegmentFrameComposer.Compose(
            _structure, zones, devices, settings.Devices.DisabledLightingDevices, uncontrolled,
            settings.Devices.LightingDevicePrefs, MasterBrightness.Effective(settings.Lighting), 1.0,
            DateTime.UtcNow.Ticks, _identify, _segBuf);

        var leds = _segBuf[0];
        for (int i = 0; i < HydroShift2CurveProtocol.LedCount && i < leds.Length; i++)
        {
            _rgb[i * 3] = leds[i].R;
            _rgb[(i * 3) + 1] = leds[i].G;
            _rgb[(i * 3) + 2] = leds[i].B;
        }
        _board.SetLeds(_rgb);
    }
}
