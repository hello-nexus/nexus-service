using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Panel;
using Nexus.Service.Peripherals.Y70;
using Nexus.Service.Persistence;
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
    private readonly IOverlayHost _overlayHost;
    private readonly IConfigStore _store;
    private bool _wasDetected;
    // A ChangeDisplaySettingsEx rotation drops the Y70 from display enumeration
    // mid-mode-change, so IsConnected() flaps false for a tick right after we
    // apply orientation. Requiring several consecutive absent ticks before
    // declaring a real disconnect stops the apply from re-arming its own edge
    // (rotate -> "disappears" -> "reappears" -> rotate ...), which otherwise
    // fights the display in a loop.
    private int _absentTicks;
    private const int DetectDropTicks = 3;
    private long? _lastHostStartAttemptTick;
    private static readonly long HostStartRetryIntervalMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;

    public Y70DisplayHeartbeatWorker(Y70DisplayHub hub, HardwarePresence presence, DeviceControlGate gate, IY70Provider y70, IOverlayHost overlayHost, IConfigStore store)
    {
        _hub = hub;
        _presence = presence;
        _gate = gate;
        _y70 = y70;
        _overlayHost = overlayHost;
        _store = store;
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
        if (!_gate.IsEnabled("y70"))
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
        var detected = _y70.IsConnected();
        if (detected)
        {
            _absentTicks = 0;
            if (!_wasDetected)
            {
                _wasDetected = true;
                _y70.ApplyEffectiveOrientation();
                ServiceLog.Info("[y70-display-heartbeat] newly detected; orientation re-applied");
            }
            EnsureKioskHostRunning();
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

    /// <summary>
    /// A Y70 that is present while the overlay host is down has no other
    /// path back to a kiosk: the overlay idle-exits when it finds no panel,
    /// the service deliberately does not respawn a clean exit, and the
    /// boot-time spawn's console-user wait gives up eventually (a machine
    /// left at the lock screen past that cap would otherwise strand the
    /// kiosk until a settings toggle). Runs every detected tick, rate-
    /// limited between start attempts so a host that cannot stay up (e.g.
    /// serial controller present but the display link dead, so the overlay
    /// never finds a panel monitor and idles out) does not spawn-churn.
    /// A running host reconciles the kiosk itself via its prefs poll and
    /// WM_DISPLAYCHANGE. Same predicate subset as OverlayHostBootstrap's
    /// shouldRun, so this never starts the host in a state the reconcile
    /// wouldn't.
    /// </summary>
    private void EnsureKioskHostRunning()
    {
        try
        {
            if (!_store.Load().Panel.AutoLaunch || _overlayHost.IsRunning) return;
            var now = Environment.TickCount64;
            if (_lastHostStartAttemptTick is { } last && now - last < HostStartRetryIntervalMs) return;
            _lastHostStartAttemptTick = now;
            if (_overlayHost.Start())
            {
                ServiceLog.Info("[y70-display-heartbeat] panel present with overlay host down; started host");
            }
            else
            {
                ServiceLog.Info("[y70-display-heartbeat] panel present but overlay host unavailable; kiosk cannot auto-start");
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[y70-display-heartbeat] overlay-host start failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
