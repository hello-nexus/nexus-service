using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Lighting.Zones;
using Nexus.Service.Peripherals.Ibp;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting;

/// <summary>
/// Pushes per-frame engine output to every attached iBUYPOWER keyboard /
/// mouse over HID feature reports. Mirrors <see cref="KeebLightingFrameWriter"/>:
/// a 30 Hz timer composes each unit's zone frames into its single segment
/// buffer (brightness / disabled / identify per card against the shared
/// settings store) and streams it through <see cref="IbpPeripheralHub"/>.
/// Streams only while a software effect is active; otherwise the units are
/// handed back to their onboard animation once.
/// </summary>
public sealed class IbpPeripheralLightingFrameWriter : IHostedService, IDisposable
{
    private const int TickPeriodMs = 33; // 30 Hz, matches the engine + Keeb writer.

    private readonly LightingEngine _engine;
    private readonly IbpPeripheralHub _hub;
    private readonly IConfigStore _store;
    private readonly Np50IdentifyTracker _identify;
    private readonly FeatureGates _gates;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _wasStreaming;

    // Per-unit segment buffers, keyed by device id; pruned when a unit leaves.
    private readonly Dictionary<string, RgbColor[][]> _buffers = new();

    public IbpPeripheralLightingFrameWriter(LightingEngine engine, IbpPeripheralHub hub, IConfigStore store, Np50IdentifyTracker identify, FeatureGates? gates = null)
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
                ServiceLog.Error($"[ibp-lighting-writer] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One compose/write cycle. Internal so tests can step it.</summary>
    internal void Tick()
    {
        if (!_gates.Lighting) return;
        var attached = _hub.Attached;
        if (attached.Count == 0)
        {
            _wasStreaming = false;
            if (_buffers.Count > 0) _buffers.Clear();
            return;
        }

        // Firmware/software arbitration: stream only while a software effect
        // is active. When none is, hand the LEDs back once so the onboard
        // animation resumes, then stay off the wire.
        if (_engine.CurrentEffectName == "none")
        {
            if (_wasStreaming)
            {
                _wasStreaming = false;
                _hub.ReleaseAllToFirmware();
            }
            return;
        }
        _wasStreaming = true;

        var devices = _engine.Devices;
        if (devices.Length == 0) return;

        var settings = _store.Load();
        var disabled = settings.Devices.DisabledLightingDevices;
        var uncontrolled = settings.Devices.UncontrolledLightingDevices;
        var prefs = settings.Devices.LightingDevicePrefs;
        var globalBrightness = Math.Clamp(settings.Lighting.GlobalBrightness, 0f, 1f);
        var nowTicks = DateTime.UtcNow.Ticks;

        foreach (var unit in attached)
        {
            var structure = IbpPeripheralLightingDeviceProvider.BuildStructure(unit);
            var zones = ZoneResolution.Resolve(structure, settings);

            // Uncontrolled = the user handed this unit to its firmware: leave
            // the wire alone (idempotent; only sends the handback once).
            if (ZoneResolution.IsFullyUncontrolled(zones, uncontrolled))
            {
                _hub.ReleaseToFirmware(unit.DeviceId);
                continue;
            }

            _buffers.TryGetValue(unit.DeviceId, out var buffers);
            buffers ??= Array.Empty<RgbColor[]>();
            SegmentFrameComposer.EnsureBuffers(structure, ref buffers);
            _buffers[unit.DeviceId] = buffers;

            var touched = SegmentFrameComposer.Compose(
                structure, zones, devices, disabled, uncontrolled, prefs, globalBrightness, 1.0, nowTicks, _identify, buffers);
            if (touched[IbpPeripheralLightingDeviceProvider.LedsSegment])
            {
                _hub.WriteFrame(unit.DeviceId, buffers[IbpPeripheralLightingDeviceProvider.LedsSegment]);
            }
        }

        if (_buffers.Count > attached.Count)
        {
            var stale = new List<string>();
            foreach (var key in _buffers.Keys)
            {
                var live = false;
                foreach (var unit in attached)
                {
                    if (unit.DeviceId == key) { live = true; break; }
                }
                if (!live) stale.Add(key);
            }
            foreach (var key in stale) _buffers.Remove(key);
        }
    }
}
