using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Aw5;

/// <summary>
/// Drives every AW5 pump display from live sensors, replacing the per-ODM vendor
/// driver .exe. Ticks at the Levelplay cycle rate, which that panel needs as a
/// keep-alive: it reverts to its own standalone reading a few seconds after frames
/// stop. CoolerMaster holds its last frame for about 30s and is written on the same
/// tick, well inside its own tolerance (its vendor sends every ~2.4s; any rate works).
///
/// Gated by Nexus Control on the aw5 handler, which is variant-agnostic: one toggle
/// covers both models. Gated off, Levelplay fades to standalone on its own and
/// CoolerMaster is blanked outright, since it would otherwise hold a stale reading.
/// </summary>
public sealed class Aw5PanelWorker : BackgroundService
{
    /// <summary>Off the startup critical path. An unsampled sensor stack reads as 0, which the panel renders harmlessly.</summary>
    private const int InitialDelayMs = 2000;

    /// <summary>
    /// Ticks between bus scans. Discovery walks every HID interface on the box, so
    /// doing it per tick would sweep the bus once a second forever, on machines with
    /// no AW5 too. The tick rate is the panel's keep-alive; hot-plug latency is not,
    /// and ~5s matches the other device connection workers.
    /// </summary>
    private const int RediscoverEveryTicks = 5;

    private readonly Aw5Hub _hub;
    private readonly Aw5SensorReader _reader;
    private readonly DeviceControlGate _gate;
    private readonly DriverExePolicy _driverExe;
    private bool _wasGatedOn;
    private int _loggedPanels = -1;
    private IReadOnlyList<Aw5PanelTarget> _panels = Array.Empty<Aw5PanelTarget>();
    private int _ticksSinceDiscover = int.MaxValue;

    public Aw5PanelWorker(Aw5Hub hub, Aw5SensorReader reader, DeviceControlGate gate, DriverExePolicy driverExe)
    {
        _hub = hub;
        _reader = reader;
        _gate = gate;
        _driverExe = driverExe;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // This worker and the vendor driver .exe are two implementations of the same
        // panel, so exactly one runs: restoring the vendor path stands this one down
        // rather than leaving two writers on one HID.
        if (_driverExe.Enabled)
        {
            ServiceLog.Info("[aw5] vendor driver path enabled; native panel worker standing down");
            return;
        }

        try { await Task.Delay(InitialDelayMs, stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Aw5Protocol.LevelplayCycleMs));
        try
        {
            do
            {
                try { await TickAsync(stoppingToken); }
                catch (Exception ex) { ServiceLog.Error($"[aw5] tick failed: {ex.GetType().Name}: {ex.Message}"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
        _hub.CloseAll();
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        // Gate first: a bus scan is the expensive part of a tick, and a device the
        // user switched off needs neither the scan nor the frames.
        if (!_gate.IsEnabled(Aw5Handler.HandlerId))
        {
            // Only on the on->off edge: blanking every tick would spam a device the
            // user asked Nexus to leave alone.
            if (_wasGatedOn)
            {
                foreach (var p in _panels) _hub.Blank(p);
                _hub.CloseAll();
                _panels = Array.Empty<Aw5PanelTarget>();
                _ticksSinceDiscover = int.MaxValue;
                _wasGatedOn = false;
            }
            return;
        }
        _wasGatedOn = true;

        // Tested before the increment: pre-incrementing the int.MaxValue seed
        // overflows to int.MinValue and the scan never runs at all.
        if (_ticksSinceDiscover >= RediscoverEveryTicks)
        {
            _ticksSinceDiscover = 0;
            _panels = _hub.Discover();
            _hub.CloseAbsent(_panels);
            if (_panels.Count != _loggedPanels)
            {
                if (_panels.Count > 0) ServiceLog.Info($"[aw5] driving {_panels.Count} panel(s) natively");
                _loggedPanels = _panels.Count;
            }
        }
        _ticksSinceDiscover++;
        if (_panels.Count == 0) return;

        var reading = _reader.Read();
        for (var i = 0; i < _panels.Count; i++) await _hub.RenderAsync(_panels[i], reading, ct);
    }
}
