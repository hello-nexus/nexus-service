using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Platform;
using Nexus.Service.Persistence;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Lighting;

/// <summary>
/// Drives the Galahad II LCD pump head through the panel hub that already owns MI_01.
/// Canvas frames use the twelve-position per-LED report; selected firmware effects keep
/// using the separate 0x83 zone command.
/// </summary>
public sealed class Galahad2LcdLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;

    private readonly LightingEngine _engine;
    private readonly JpegPanelHub _hub;
    private readonly IConfigStore _store;
    private readonly Galahad2LcdLightingDeviceProvider _provider;
    private readonly FeatureGates _gates;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private RgbColor[][] _segmentBuffers = Array.Empty<RgbColor[]>();
    private readonly byte[] _wireColors = new byte[PerLedColorBytes];
    private readonly byte[] _lastWireColors = new byte[PerLedColorBytes];
    private bool _hasLastCanvas;
    private bool _lastWasCanvas;
    private byte? _lastCanvasBrightness;
    private int? _lastFirmwareSig;
    private bool _loggedNoCanvasFrame;
    private bool _loggedCanvasFailure;
    private bool _loggedCanvasSuccess;
    private bool _loggedFirmwareFailure;
    private bool _loggedFirmwareSuccess;

    private const int PerLedColorBytes = Galahad2LcdLightingDeviceProvider.PumpLedCount * 3;

    public Galahad2LcdLightingFrameWriter(
        LightingEngine engine,
        JpegPanelHub hub,
        IConfigStore store,
        Galahad2LcdLightingDeviceProvider provider,
        FeatureGates? gates = null)
    {
        _engine = engine;
        _hub = hub;
        _store = store;
        _provider = provider;
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
            catch { }
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
                Console.Error.WriteLine($"[galahad2-lcd-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
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
        if (!_gates.Lighting)
        {
            return;
        }
        if (!_hub.IsConnected)
        {
            ResetCaches();
            return;
        }

        var settings = _store.Load();
        var lighting = settings.Devices.Galahad2Lighting;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var brightnessRaw = (byte)Math.Clamp(
            (int)Math.Round(Math.Min((double)lighting.Brightness, globalBrightness * 4.0)), 0, 4);
        var structure = _provider.BuildStructure();
        var zones = ZoneResolution.Resolve(structure, settings);
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        if (ZoneResolution.IsFullyUncontrolled(zones, uncontrolled))
        {
            ResetCaches();
            return;
        }

        if (lighting.Mode == "canvas")
        {
            if (_engine.Devices.Length == 0)
            {
                if (!_loggedNoCanvasFrame)
                {
                    ServiceLog.Warn("[lianli-galahad2-lcd] pump RGB skipped: lighting engine has no pump frame");
                    _loggedNoCanvasFrame = true;
                }
                ResetCaches();
                return;
            }
            _lastFirmwareSig = null;
            TickCanvas(structure, zones, settings, globalBrightness, brightnessRaw);
            return;
        }

        var mode = Galahad2LightingModes.Find(lighting.Mode);
        if (mode is null)
        {
            return;
        }
        var signature = ComputeFirmwareSig(lighting, brightnessRaw);
        if (_lastFirmwareSig.HasValue && _lastFirmwareSig.Value == signature)
        {
            return;
        }

        _lastWasCanvas = false;
        _lastCanvasBrightness = null;
        if (_hub.SendPumpZoneLighting(
            // The LCD's pump channel is scope 0; its two visible sides mirror this channel.
            ring: 0,
            mode: mode.WireByte,
            brightness: brightnessRaw,
            speed: (byte)Math.Clamp(lighting.Speed, 0, 4),
            direction: (byte)Math.Clamp(lighting.Direction, 0, 1),
            colors: Galahad2LightingFrameWriter.BuildColorBytes(lighting, mode)))
        {
            _lastFirmwareSig = signature;
            if (!_loggedFirmwareSuccess)
            {
                ServiceLog.Info($"[lianli-galahad2-lcd] pump RGB firmware mode active ({lighting.Mode})");
                _loggedFirmwareSuccess = true;
            }
        }
        else if (!_loggedFirmwareFailure)
        {
            ServiceLog.Warn("[lianli-galahad2-lcd] pump RGB firmware write rejected");
            _loggedFirmwareFailure = true;
        }
    }

    private void TickCanvas(
        DeviceStructure structure,
        IReadOnlyList<ResolvedZone> zones,
        NexusSettings settings,
        float globalBrightness,
        byte brightnessRaw)
    {
        SegmentFrameComposer.EnsureBuffers(structure, ref _segmentBuffers);
        SegmentFrameComposer.Compose(
            structure,
            zones,
            _engine.Devices,
            settings.Devices.DisabledLightingDevices,
            settings.Devices.UncontrolledLightingDevices,
            settings.Devices.LightingDevicePrefs,
            globalBrightness,
            masterMul: 1.0,
            nowTicks: DateTime.UtcNow.Ticks,
            identify: null,
            _segmentBuffers);

        var colors = _segmentBuffers[Galahad2LcdLightingDeviceProvider.PumpSegment];
        _wireColors.AsSpan().Clear();
        for (var i = 0; i < colors.Length; i++)
        {
            _wireColors[i * 3] = colors[i].R;
            _wireColors[i * 3 + 1] = colors[i].G;
            _wireColors[i * 3 + 2] = colors[i].B;
        }

        var brightnessChanged = !_lastCanvasBrightness.HasValue || _lastCanvasBrightness.Value != brightnessRaw;
        var needsBootstrap = !_lastWasCanvas || brightnessChanged;
        if (!needsBootstrap && _hasLastCanvas && _wireColors.AsSpan().SequenceEqual(_lastWireColors))
        {
            return;
        }
        // The controller ignores 0x14 until an A-command has put the pump channel in host RGB mode.
        if (needsBootstrap
            && !_hub.SendPumpZoneLighting(
                ring: 0,
                mode: 0x03,
                brightness: brightnessRaw,
                speed: 0,
                direction: 0,
                colors: _wireColors.AsSpan(0, 3)))
        {
            if (!_loggedCanvasFailure)
            {
                ServiceLog.Warn("[lianli-galahad2-lcd] pump RGB bootstrap write rejected (0x83)");
                _loggedCanvasFailure = true;
            }
            return;
        }
        if (!_hub.SendPumpPerLed(_wireColors))
        {
            if (!_loggedCanvasFailure)
            {
                ServiceLog.Warn("[lianli-galahad2-lcd] pump RGB per-LED write rejected (0x14)");
                _loggedCanvasFailure = true;
            }
            return;
        }
        if (!_loggedCanvasSuccess)
        {
            ServiceLog.Info("[lianli-galahad2-lcd] pump RGB canvas active (0x83 + 0x14, 12 LEDs)");
            _loggedCanvasSuccess = true;
        }
        _wireColors.AsSpan().CopyTo(_lastWireColors);
        _hasLastCanvas = true;
        _lastWasCanvas = true;
        _lastCanvasBrightness = brightnessRaw;
    }

    private void ResetCaches()
    {
        _hasLastCanvas = false;
        _lastWasCanvas = false;
        _lastCanvasBrightness = null;
        _lastFirmwareSig = null;
    }

    private static int ComputeFirmwareSig(Galahad2LightingSettings settings, byte brightnessRaw)
    {
        var hash = new HashCode();
        hash.Add(settings.Mode);
        hash.Add(settings.Speed);
        hash.Add(settings.Direction);
        hash.Add(brightnessRaw);
        hash.Add(settings.InnerColor);
        hash.Add(settings.OuterColor);
        foreach (var color in settings.Colors)
        {
            hash.Add(color);
        }
        return hash.ToHashCode();
    }
}
