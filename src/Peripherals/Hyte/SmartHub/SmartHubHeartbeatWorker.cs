using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Hyte.SmartHub;

/// <summary>
/// Background poller for the SmartHub. Mirrors
/// <see cref="Nexus.Service.Peripherals.Hyte.MiniHub.MiniHubHeartbeatWorker"/>.
/// Jobs: (a) keep trying to open the port until the device shows up,
/// (b) read the firmware version once on first connect, (c) turn the hub's
/// onboard LED animation OFF after a (re)connect so our software LED frames
/// take effect, (d) poll the per-channel tach + enabled state so the cooling
/// page shows live RPM and only surfaces populated ports, (e) assert fan
/// duty on (re)connect and RE-ASSERT it every tick: the firmware runs a
/// 5-second watchdog fed only by <c>FF CC 02</c> fan writes
/// (<c>USB_NO_Activity_Time = 50</c> × 100 ms in the Control_box source) -
/// when it expires the hub reloads <c>Default_FAN_Percent</c> from flash and
/// re-applies it to ALL four ports, silently undoing any one-shot duty.
/// This is why HYTE's legacy agent rewrites every port every second.
/// </summary>
public sealed class SmartHubHeartbeatWorker : BackgroundService
{
    /// <summary>
    /// Duty written to ports with no saved manual speed and no curve binding.
    /// The firmware's own fallback is the flash-saved <c>Default_FAN_Percent</c>
    /// (60% when flash is erased); without host writes the watchdog holds
    /// every port there forever.
    /// </summary>
    private const int DefaultDutyPercent = 50;

    private readonly SmartHubHub _hub;
    private readonly HardwarePresence _presence;
    private readonly IConfigStore _config;
    private readonly SmartHubCoolingProvider _cooling;
    private readonly DeviceControlGate _gate;
    private bool? _animationStateAsserted; // null = not yet asserted (e.g. fresh connect)
    private bool _initialDutyAsserted;
    private int _tickCount;
    private const int TraceEveryNTicks = 15; // every ~30 s with the 2 s timer

    public SmartHubHeartbeatWorker(SmartHubHub hub, HardwarePresence presence, IConfigStore config, SmartHubCoolingProvider cooling, DeviceControlGate gate)
    {
        _hub = hub;
        _presence = presence;
        _config = config;
        _cooling = cooling;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex) { Console.Error.WriteLine($"[smarthub-heartbeat] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
        _hub.Disconnect();
    }

    public void Tick()
    {
        if (!_gate.IsEnabled(SmartHubHub.DeviceType))
        {
            if (_hub.IsConnected) _hub.Disconnect();
            return;
        }

        // Skip silently when disconnected and no SmartHub is on the bus; stay live
        // while connected so an unplug is still handled.
        if (!_hub.IsConnected && !_presence.UsbPresent(SmartHubProtocol.VendorId, SmartHubProtocol.ProductId))
            return;

        var connectedBefore = _hub.IsConnected;
        if (!_hub.EnsureConnected()) { _animationStateAsserted = null; _initialDutyAsserted = false; return; }
        if (!connectedBefore) { _initialDutyAsserted = false; _animationStateAsserted = null; }

        // First connect ⇒ read FW version. Cheap; no-op once populated.
        if (!connectedBefore || string.IsNullOrEmpty(_hub.State.FirmwareVersion))
        {
            _hub.PollFirmwareVersion();
        }

        // Assert firmware animation on/off to match the persisted preference.
        // Re-asserted on reconnect (null tracker) or whenever the preference changes.
        var desiredOn = _config.Load().Devices.SmartHubFirmwareControl;
        if (_animationStateAsserted != desiredOn)
        {
            if (_hub.SetFirmwareAnimation(on: desiredOn))
            {
                _animationStateAsserted = desiredOn;
            }
        }

        if (!_initialDutyAsserted)
            _initialDutyAsserted = ApplyInitialDuty();
        else
            ReassertDuty();

        var pollOk = _hub.PollChannelInfo();
        if (++_tickCount % TraceEveryNTicks == 1)
        {
            var s = _hub.State;
            ServiceLog.Info(
                $"[smarthub-cooling] poll#{_tickCount} ok={pollOk} serial={s.Serial} " +
                $"fans=[{string.Join(",", FanSummaries(s))}]");
        }
    }

    /// <summary>
    /// Bring every PWM port off the firmware's 100% power-on default: restore
    /// the user's saved manual speed where one exists (so it survives a service
    /// restart), otherwise write <see cref="DefaultDutyPercent"/>. Curve-bound
    /// ports are skipped - <see cref="CurveEngine"/> drives those within a tick.
    /// Returns false (retry next tick) if any write fails.
    /// The saved-speed write overlaps CurveEngine's manual replay (same value,
    /// harmless) but is kept: the replay writes nothing to ports WITHOUT a
    /// saved entry, so only this pass takes them off the 100% default, and
    /// only this pass seeds the SeenFan presence latch.
    /// </summary>
    private bool ApplyInitialDuty()
    {
        var serial = _hub.State.Serial;
        if (string.IsNullOrEmpty(serial)) return false;

        var manual = _config.Load().Cooling.ManualSpeeds;
        var curveBound = CurveModes.BoundFanIds(_config);
        var ok = true;
        var restored = 0;
        for (var ch = 0; ch < SmartHubProtocol.FanChannelCount; ch++)
        {
            var id = SmartHubCoolingProvider.FanId(serial, ch);
            var hasSaved = manual.TryGetValue(id, out var saved);
            // A saved manual speed or a curve binding means the user set this
            // port up - seed the presence latch so a fan parked at 0% duty
            // doesn't vanish from the cooling page across a restart (the tach
            // reads 0 and would never latch on its own).
            if (hasSaved || curveBound.Contains(id)) _hub.State.Fans[ch].SeenFan = true;
            if (curveBound.Contains(id)) continue; // CurveEngine drives these within a tick
            if (hasSaved)
            {
                if (_cooling.RestoreManualSpeed(id, saved)) restored++;
                else ok = false;
            }
            else
            {
                ok &= _hub.WriteFanSpeed(ch, DefaultDutyPercent);
            }
        }
        if (ok) ServiceLog.Info($"[smarthub-cooling] initial duty asserted (default={DefaultDutyPercent}%, restored {restored} saved manual speed(s))");
        return ok;
    }

    /// <summary>
    /// Rewrite each host-commanded port's last duty every tick (2 s, well
    /// inside the firmware's 5 s watchdog) so the hub never falls back to its
    /// flash default. Curve-bound ports also get fresh writes from
    /// <see cref="CurveEngine"/> every second; the duplicate here is harmless
    /// (6 bytes/port) and keeps the watchdog fed between curve ticks. Ports
    /// nothing has driven yet (curve bound but stalled) are skipped - their
    /// State duty is still 0, and feeding the watchdog 0% would defeat the
    /// firmware's own flash-default fallback.
    /// </summary>
    private void ReassertDuty()
    {
        foreach (var fan in _hub.State.Fans)
        {
            if (!fan.HostDriven) continue;
            _hub.WriteFanSpeed(fan.Index, fan.Duty, fan.Enabled);
        }
    }

    private static System.Collections.Generic.IEnumerable<string> FanSummaries(SmartHubState s)
    {
        foreach (var f in s.Fans)
            yield return $"ch{f.Index}:en={f.Enabled},rpm={f.Rpm},duty={f.Duty}";
    }
}
