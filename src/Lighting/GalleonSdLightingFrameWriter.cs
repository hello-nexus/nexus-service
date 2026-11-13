using System;
using System.Collections.Generic;
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

        var settings = _store.Load();
        var saved = settings.Devices.SaveToDeviceLightingDevices.Contains(unit.DeviceId);
        if (!saved) _hub.SetSavedToDevice(false);

        if (!saved && _engine.CurrentEffectName == "none")
        {
            if (_wasStreaming)
            {
                _wasStreaming = false;
                _hub.ReleaseToHardware();
            }
            return;
        }

        var devices = _engine.Devices;
        if (!saved && devices.Length == 0) return;

        var structure = GalleonSdLightingDeviceProvider.BuildStructure(unit);
        var zones = ZoneResolution.Resolve(structure, settings);
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        if (ZoneResolution.IsFullyUncontrolled(zones, uncontrolled))
        {
            _wasStreaming = false;
            _hub.SetSavedToDevice(false);
            _hub.ReleaseToHardware();
            return;
        }

        // Saved to the keyboard: the board stays in hardware mode and no live frame is sent. The composed frame
        // goes to the hub, which writes it into onboard layers once it holds still. No effect means no frame, so
        // whatever the keyboard already holds is left alone.
        if (saved)
        {
            _wasStreaming = false;
            var frame = _engine.CurrentEffectName != "none" && devices.Length > 0
                ? Compose(structure, zones, devices, settings, uncontrolled)
                : null;
            _hub.SetSavedToDevice(true, frame is null ? default : frame);
            return;
        }
        _wasStreaming = true;

        var live = Compose(structure, zones, devices, settings, uncontrolled);
        if (live is not null) _hub.WriteFrame(live);
    }

    private RgbColor[]? Compose(
        DeviceStructure structure,
        IReadOnlyList<ResolvedZone> zones,
        IReadOnlyList<DeviceFrame> devices,
        NexusSettings settings,
        IReadOnlyList<string> uncontrolled)
    {
        SegmentFrameComposer.EnsureBuffers(structure, ref _buffers);
        var touched = SegmentFrameComposer.Compose(
            structure, zones, devices, settings.Devices.DisabledLightingDevices, uncontrolled,
            settings.Devices.LightingDevicePrefs, MasterBrightness.Resolve(settings), 1.0,
            DateTime.UtcNow.Ticks, _identify, _buffers, _engine.Live);
        return touched[GalleonSdLightingDeviceProvider.LedsSegment] ? _buffers[GalleonSdLightingDeviceProvider.LedsSegment] : null;
    }
}
