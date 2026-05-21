using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Qos.Service.Lighting;
using Qos.Service.Sockets;

namespace Qos.Service.Peripherals.Hyte.Np50;

/// <summary>
/// Background service that polls the NP50 hub on a fixed cadence. Two
/// concerns it owns:
///
///   1. <b>Heartbeat.</b> The hub reverts to motherboard/firmware control
///      if "Get NP50 Info" isn't called within
///      <see cref="Np50Protocol.HeartbeatRevertMs"/> (5s). Tick faster than
///      that so a single missed tick doesn't lose software control.
///
///   2. <b>Snapshot refresh.</b> Pull per-port fan info + warning detail
///      so the cooling provider and routes see fresh data without each
///      caller having to poll.
///
/// On first connect it also reads the firmware version (one-shot). Tick
/// failures don't throw out of <see cref="ExecuteAsync"/>; the hub class
/// drops the transport on IO errors and the next tick re-discovers.
/// </summary>
public sealed class Np50HeartbeatWorker : BackgroundService
{
    private const long ModeAssertCooldownMs = 30_000;

    private readonly Np50Hub _hub;
    private readonly MultiplexHub _wsHub;
    private readonly Np50LightingDeviceProvider? _lighting;
    private string _lastBroadcastFwVersion = "";
    private bool _lastConnected;
    private byte _lastWarningSummary;
    private long _lastModeAssertMs;

    public Np50HeartbeatWorker(Np50Hub hub, MultiplexHub wsHub, Np50LightingDeviceProvider? lighting = null)
    {
        _hub = hub;
        _wsHub = wsHub;
        _lighting = lighting;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromMilliseconds(Np50Protocol.RecommendedPollMs);
        // PeriodicTimer skips drift accumulation if a tick runs long.
        using var timer = new PeriodicTimer(period);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                // Never let a tick exception take the worker down.
                Console.Error.WriteLine($"[np50-heartbeat] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        // On shutdown release the port cleanly so a service restart can
        // reopen without "port already in use".
        _hub.Disconnect();
    }

    /// <summary>One poll cycle. Public so tests / debug routes can step manually.</summary>
    public void Tick()
    {
        var connectedBefore = _hub.IsConnected;

        // The Get-Info call IS the heartbeat: it has to happen every tick
        // or the hub reverts. PollHubInfo also opens the port if needed.
        if (!_hub.PollHubInfo())
        {
            BroadcastIfConnectionChanged(connectedBefore: connectedBefore, connectedAfter: false);
            return;
        }

        // First time we see the hub, grab the firmware version. After that,
        // re-read only when we reconnect — version doesn't change at runtime.
        if (!connectedBefore || string.IsNullOrEmpty(_hub.State.FirmwareVersion))
        {
            _hub.PollFirmwareVersion();
        }

        // Enforce desired cooling mode. We used to re-send on EVERY drift
        // detection, which can fire mid-frame and surface as a one-frame
        // RGB glitch on connected strips (the 15-byte SetCoolingMode
        // command parses on the same UART thread the LED stream rides).
        // Match HYTE's shipping nexus-control-service: assert mode only
        // when there's a genuine state change to push — first time we see
        // the hub this session (no FW version yet), and at most once per
        // 30 s of sustained drift. That keeps the rare firmware-2.0.3.1
        // "needs the command twice" path covered without injecting a
        // command burst into the 30 Hz lighting stream.
        if (_hub.DesiredCoolingMode is byte desired)
        {
            var current = _hub.State.HubInfo.CoolingMode switch
            {
                "Software" => Np50Protocol.ModeSoftware,
                "Motherboard" => Np50Protocol.ModeMotherboard,
                "Static" => Np50Protocol.ModeStatic,
                _ => (byte)0,
            };
            var driftDetected = current != desired;
            var nowMs = Environment.TickCount64;
            if (driftDetected)
            {
                var firstSeen = !connectedBefore || string.IsNullOrEmpty(_hub.State.FirmwareVersion);
                var rateLimitElapsed = nowMs - _lastModeAssertMs >= ModeAssertCooldownMs;
                if (firstSeen || rateLimitElapsed)
                {
                    _hub.SetCoolingMode(desired);
                    _lastModeAssertMs = nowMs;
                }
            }
            else
            {
                // Reset the cooldown so the next drift event can re-assert
                // immediately. Without this a quick mode-change → drift →
                // reconverge cycle would silently swallow the next real one.
                _lastModeAssertMs = 0;
            }
        }

        // Pull per-port info every tick. Even with no fans attached the hub
        // returns a valid empty response cheaply.
        for (var port = 1; port <= Np50Protocol.PortCount; port++)
        {
            _hub.PollPort(port);
        }

        // Tell the lighting contributor that the lit-device topology may
        // have changed (a strip hot-plugged onto a port, hub reconnected,
        // etc.). The contributor debounces internally; calling this every
        // tick is cheap.
        _lighting?.OnHubStateUpdated();

        // Detailed warning bytes only when the summary is non-zero. Saves a
        // pointless command on the happy path.
        if (_hub.State.HubInfo.WarningSummary != 0)
        {
            _hub.PollWarningDetail();
            if (_hub.State.HubInfo.WarningSummary != _lastWarningSummary)
            {
                _lastWarningSummary = _hub.State.HubInfo.WarningSummary;
                if (!string.IsNullOrEmpty(_hub.DeviceId))
                    PanelTopics.BroadcastCoolingWarnings(_wsHub, _hub.DeviceId);
            }
        }
        else if (_lastWarningSummary != 0)
        {
            // Warnings cleared. Notify so the UI can drop the badge.
            _lastWarningSummary = 0;
            if (!string.IsNullOrEmpty(_hub.DeviceId))
                PanelTopics.BroadcastCoolingWarnings(_wsHub, _hub.DeviceId);
        }

        BroadcastIfConnectionChanged(connectedBefore: connectedBefore, connectedAfter: true);
        BroadcastCoolingIfFwChanged();
    }

    private void BroadcastIfConnectionChanged(bool connectedBefore, bool connectedAfter)
    {
        if (connectedBefore == connectedAfter && _lastConnected == connectedAfter) return;
        _lastConnected = connectedAfter;
        // Devices list changed (NP50 appeared or disappeared). Cooling channel
        // list also changed for the same reason.
        PanelTopics.BroadcastCooling(_wsHub);
    }

    private void BroadcastCoolingIfFwChanged()
    {
        if (_hub.State.FirmwareVersion == _lastBroadcastFwVersion) return;
        _lastBroadcastFwVersion = _hub.State.FirmwareVersion;
        // FW version is shown on the device card; trigger a refetch.
        PanelTopics.BroadcastPanelDevice(_wsHub, _hub.DeviceId);
    }
}
