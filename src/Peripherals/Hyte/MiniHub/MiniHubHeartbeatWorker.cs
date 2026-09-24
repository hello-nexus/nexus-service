using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Hyte.MiniHub;

/// <summary>
/// Background poller for the MiniHub. The MiniHub has no 5s revert-to-firmware
/// timer (unlike NP50). Jobs: (a) keep trying to open the port until the device
/// shows up, (b) read the firmware version once on first connect,
/// (c) re-assert software RGB + fan control modes after a reconnect so the
/// LED frames and fan writes take effect, (d) poll the per-port tach
/// readings so the cooling page can show live RPM.
/// </summary>
public sealed class MiniHubHeartbeatWorker : BackgroundService
{
    private readonly MiniHubHub _hub;
    private readonly HardwarePresence _presence;
    private readonly DeviceControlGate _gate;
    private bool _rgbModeAsserted;
    private bool _fanModeAsserted;
    private int _tickCount;
    private const int TraceEveryNTicks = 15; // every ~30 s with the 2 s timer

    public MiniHubHeartbeatWorker(MiniHubHub hub, HardwarePresence presence, DeviceControlGate gate)
    {
        _hub = hub;
        _presence = presence;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { Console.Error.WriteLine($"[minihub-heartbeat] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
        _hub.Disconnect();
    }

    public void Tick()
    {
        if (!_gate.IsEnabled("fan-hub"))
        {
            if (_hub.IsConnected) _hub.Disconnect();
            return;
        }

        // Skip silently when the hub isn't connected and no MiniHub is on the bus:
        // no discovery, no log, until one actually appears. Stay live while
        // connected so an unplug is still noticed and handled below.
        if (!_hub.IsConnected && !_presence.UsbPresent(MiniHubProtocol.VendorId, MiniHubProtocol.ProductId))
            return;

        var connectedBefore = _hub.IsConnected;
        if (!_hub.EnsureConnected()) { _rgbModeAsserted = false; _fanModeAsserted = false; return; }
        // First connect ⇒ read FW version + assert software control modes so
        // lighting writes and fan-speed writes take effect. Re-asserted on each
        // reconnect; no-op in steady-state.
        if (!connectedBefore || string.IsNullOrEmpty(_hub.State.FirmwareVersion))
        {
            _hub.PollFirmwareVersion();
        }
        if (!_rgbModeAsserted)
        {
            if (_hub.SetRgbControlMode(MiniHubProtocol.RgbModeSoftware))
                _rgbModeAsserted = true;
        }
        if (!_fanModeAsserted)
        {
            // Respect a user-pinned mode: if the user picked Motherboard (BIOS)
            // via PUT /devices/minihub/cooling-mode, re-asserting Software here
            // would undo it on every reconnect. Unpinned default is Software.
            var modeToAssert = _hub.DesiredFanControlMode ?? MiniHubProtocol.FanModeSoftware;
            if (_hub.SetFanControlMode(modeToAssert))
                _fanModeAsserted = true;
        }
        // Poll tachs every tick so the cooling page shows live RPM: one 3-byte
        // write + 9-byte read at 0.5 Hz.
        var pollOk = _hub.PollFanSpeeds();
        if (++_tickCount % TraceEveryNTicks == 1)
        {
            var s = _hub.State;
            ServiceLog.Info(
                $"[minihub-cooling] poll#{_tickCount} ok={pollOk} serial={s.Serial} " +
                $"port1Fans={s.Port1Fans} port1Rpm={s.Port1Rpm} port1RpmValid={s.Port1RpmValid} port1Raw={s.Port1RawRpm} port1Duty={s.Port1Duty} " +
                $"port2Fans={s.Port2Fans} port2Rpm={s.Port2Rpm} port2RpmValid={s.Port2RpmValid} port2Raw={s.Port2RawRpm} port2Duty={s.Port2Duty}");
        }
    }
}
