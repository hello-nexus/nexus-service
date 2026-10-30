using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Corsair.Galleon;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting;

/// <summary>Pushes engine output to the Galleon 100 SD on a timer while a software effect runs; with no effect (or the unit handed to firmware) the board is returned to hardware mode once.</summary>
public sealed class GalleonSdLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33;

    private readonly LightingEngine _engine;
    private readonly GalleonSdHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly FeatureGates _gates;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _wasStreaming;
    private RgbColor[][] _buffers = Array.Empty<RgbColor[]>();

    public GalleonSdLightingFrameWriter(LightingEngine engine, GalleonSdHub hub, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
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
        _hub.ReleaseToHardware();
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
                ServiceLog.Error($"[galleon-sd-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    internal void Tick()
    {
        if (!_gates.Lighting) return;
        var unit = _hub.Attached;
        if (unit is null)
        {
            _wasStreaming = false;
            return;
        }

        if (_engine.CurrentEffectName == "none")
        {
            if (_wasStreaming)
            {
                _wasStreaming = false;
                _hub.ReleaseToHardware();
            }
            return;
        }

        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var settings = _store.Load();
        var structure = GalleonSdLightingDeviceProvider.BuildStructure(unit);
        var zones = ZoneResolution.Resolve(structure, settings);
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        if (ZoneResolution.IsFullyUncontrolled(zones, uncontrolled))
        {
            _wasStreaming = false;
            _hub.ReleaseToHardware();
            return;
        }
        _wasStreaming = true;

        SegmentFrameComposer.EnsureBuffers(structure, ref _buffers);
        var touched = SegmentFrameComposer.Compose(
            structure, zones, devices, settings.Devices.DisabledLightingDevices, uncontrolled,
            settings.Devices.LightingDevicePrefs, MasterBrightness.Resolve(settings), 1.0,
            DateTime.UtcNow.Ticks, _identify, _buffers);
        if (touched[GalleonSdLightingDeviceProvider.LedsSegment])
        {
            _hub.WriteFrame(_buffers[GalleonSdLightingDeviceProvider.LedsSegment]);
        }
    }
}
