using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Hyte.Y70Display;

/// <summary>
/// Background poller for the Y70 display controller. Keeps trying to open the
/// COM port until it shows up, then reads the firmware version once on first
/// connect so the Firmware Updates page can show current-vs-available. Mirrors
/// <see cref="Nexus.Service.Peripherals.Hyte.QSeriesCooler.QSeriesCoolerHeartbeatWorker"/>.
/// </summary>
public sealed class Y70DisplayHeartbeatWorker : BackgroundService
{
    private readonly Y70DisplayHub _hub;
    private readonly HardwarePresence _presence;
    private readonly DeviceControlGate _gate;
    private readonly IY70Provider _y70;
    private bool _wasDetected;

    /// <summary>A HYTE panel is attached; drives the overlay supervisor.</summary>
    public bool Detected { get; private set; }
    // A ChangeDisplaySettingsEx rotation drops the Y70 from display enumeration
    // mid-mode-change, so IsConnected() flaps false for a tick right after we
    // apply orientation. Requiring several consecutive absent ticks before
    // declaring a real disconnect stops the apply from re-arming its own edge
    // (rotate -> "disappears" -> "reappears" -> rotate ...), which otherwise
    // fights the display in a loop.
    private int _absentTicks;
    private const int DetectDropTicks = 3;

    public Y70DisplayHeartbeatWorker(Y70DisplayHub hub, HardwarePresence presence, DeviceControlGate gate, IY70Provider y70)
    {
        _hub = hub;
        _presence = presence;
        _gate = gate;
        _y70 = y70;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { ServiceLog.Error($"[y70-display-heartbeat] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
        _hub.Disconnect();
    }

    public void Tick()
    {
        // Read before the gate: the panel kiosk is a display surface, not
        // device control, so it must still open when Nexus Control is off.
        Detected = _y70.IsConnected();

        if (!_gate.IsEnabled(Y70Handler.HandlerId))
        {
            if (_hub.IsConnected) _hub.Disconnect();
            _wasDetected = false;
            _absentTicks = 0;
            return;
        }

        // IY70Provider.IsConnected() covers both the serial controller and a
        // monitor-only Y70 (DDC/EDID identity), so a monitor-only hot-plug
        // still trips the re-orient edge below even though the serial-gated
        // block further down never runs for it. Apply orientation exactly once
        // per genuine (re)detection; a transient absence (our own rotation, or a
        // flaky probe) must NOT re-arm the edge - see DetectDropTicks above.
        if (Detected)
        {
            _absentTicks = 0;
            if (!_wasDetected)
            {
                _wasDetected = true;
                _y70.ApplyEffectiveOrientation();
                ServiceLog.Info("[y70-display-heartbeat] newly detected; orientation re-applied");
            }
        }
        else if (_wasDetected && ++_absentTicks >= DetectDropTicks)
        {
            _wasDetected = false;
            _absentTicks = 0;
        }

        // The serial COM port open/poll below only applies to the serial
        // controller, so it stays gated on the serial VID/PID regardless of
        // whether a monitor-only Y70 was just detected above.
        if (!_hub.IsConnected && !_presence.UsbPresent(
                Y70DisplayProtocol.VendorId,
                Y70DisplayProtocol.Y70TouchProductId,
                Y70DisplayProtocol.Y70InfiniteProductId,
                Y70DisplayProtocol.Y70TrulyProductId))
        {
            return;
        }

        var connectedBefore = _hub.IsConnected;
        if (!_hub.EnsureConnected()) return;
        if (!connectedBefore || string.IsNullOrEmpty(_hub.State.FirmwareVersion))
        {
            _hub.PollFirmwareVersion();
        }
    }
}
