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
    private readonly Y70CaseAssigner? _caseAssigner;
    private readonly Nexus.Service.Lighting.Y70GtLightingDeviceProvider? _lighting;
    private bool _wasDetected;
    private bool _caseHandled;
    private int _caseAbsentTicks;

    // The helper holds display writes for a settle window after it starts and may keep one for retry, only
    // re-sending it on the next write. One delayed reapply after the boot applies is that next write.
    // Must exceed the helper's DdcGate settle window, or the reapply lands inside it and is held again.
    private static readonly TimeSpan DefaultSettleDelay = Nexus.Service.Platform.Displays.DdcGateTiming.UnlockSettle + TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _settleLock = new();
    private CancellationTokenSource? _settleCts;
    private bool _stopped;
    private volatile bool _detected;

    /// <summary>The delayed brightness reapply scheduled by the latest detect or helper-connect edge; completes when it ran or was dropped.</summary>
    public Task? PendingSettledReapply { get; private set; }
    // Serializes the detect-edge applies with OnHelperConnected, which runs on a pool thread.
    private readonly object _applyLock = new();

    /// <summary>A HYTE panel is attached; drives the overlay supervisor.</summary>
    public bool Detected { get => _detected; private set => _detected = value; }
    // A ChangeDisplaySettingsEx rotation drops the Y70 from display enumeration
    // mid-mode-change, so IsConnected() flaps false for a tick right after we
    // apply orientation. Requiring several consecutive absent ticks before
    // declaring a real disconnect stops the apply from re-arming its own edge
    // (rotate -> "disappears" -> "reappears" -> rotate ...), which otherwise
    // fights the display in a loop.
    private int _absentTicks;
    private const int DetectDropTicks = 3;

    public Y70DisplayHeartbeatWorker(Y70DisplayHub hub, HardwarePresence presence, DeviceControlGate gate, IY70Provider y70, Y70CaseAssigner? caseAssigner = null, Nexus.Service.Lighting.Y70GtLightingDeviceProvider? lighting = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _delay = delay ?? Task.Delay;
        _lighting = lighting;
        _caseAssigner = caseAssigner;
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

    /// <summary>
    /// The helper (which performs the display rotation and DDC writes) just connected. It usually
    /// connects after the first detect, when its rotation and DDC applies cannot land, so they are
    /// sent again here. A no-op while no Y70 is detected: the detect edge covers that case.
    /// </summary>
    public void OnHelperConnected()
    {
        if (!Detected || !_gate.IsEnabled(Y70Handler.HandlerId))
        {
            return;
        }
        try
        {
            lock (_applyLock)
            {
                // A new helper process has no held-write queue, so a preset it only held is gone.
                _y70.InvalidateDisplayPrep();
                _y70.ApplyEffectiveOrientation();
                _y70.ReapplyBrightness();
            }
            ScheduleSettledReapply();
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[y70-display-heartbeat] helper-connect reapply failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // At most one reapply is pending: each edge cancels the previous one and restarts the delay.
    private void ScheduleSettledReapply()
    {
        CancellationTokenSource? replaced;
        lock (_settleLock)
        {
            if (_stopped)
            {
                return;
            }
            replaced = _settleCts;
            _settleCts = new CancellationTokenSource();
            PendingSettledReapply = RunSettledReapplyAsync(_settleCts.Token);
        }
        CancelAndDispose(replaced);
    }

    private static void CancelAndDispose(CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }
        cts.Cancel();
        cts.Dispose();
    }

    private async Task RunSettledReapplyAsync(CancellationToken token)
    {
        try
        {
            await _delay(DefaultSettleDelay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (token.IsCancellationRequested || !Detected || !_gate.IsEnabled(Y70Handler.HandlerId))
        {
            return;
        }
        try
        {
            lock (_applyLock)
            {
                _y70.ReapplySettledBrightness();
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[y70-display-heartbeat] settled reapply failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cts;
        Task? pending;
        lock (_settleLock)
        {
            _stopped = true;
            cts = _settleCts;
            _settleCts = null;
            pending = PendingSettledReapply;
        }
        CancelAndDispose(cts);
        // A reapply already past its delay is mid-write; give it a moment instead of cutting the helper RPC off.
        if (pending is not null)
        {
            await Task.WhenAny(pending, Task.Delay(StopWait, CancellationToken.None)).ConfigureAwait(false);
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    // Once per detection event, independent of the control gate: assigning the case is not device control.
    private void AssignCaseOnDetection()
    {
        if (Detected)
        {
            _caseAbsentTicks = 0;
            if (_caseHandled)
            {
                return;
            }
            try { _caseHandled = _caseAssigner?.OnDetected() ?? true; }
            catch (Exception ex) { ServiceLog.Error($"[y70-case] assign failed: {ex.GetType().Name}: {ex.Message}"); }
        }
        else if (_caseHandled && ++_caseAbsentTicks >= DetectDropTicks)
        {
            _caseHandled = false;
            _caseAbsentTicks = 0;
        }
    }

    public void Tick()
    {
        // Every path, including the early returns, nudges the lighting provider so a
        // replugged panel re-fires its devices-changed edge; the provider debounces.
        try { TickCore(); }
        finally { _lighting?.OnHubStateUpdated(); }
    }

    private void TickCore()
    {
        // Read before the gate: the panel kiosk is a display surface, not
        // device control, so it must still open when Nexus Control is off.
        Detected = _y70.IsConnected();
        AssignCaseOnDetection();

        if (!_gate.IsEnabled(Y70Handler.HandlerId))
        {
            if (_hub.IsConnected || _hub.HasBacklight) _hub.Disconnect();
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
                lock (_applyLock)
                {
                    _y70.ApplyEffectiveOrientation();
                    _y70.ReapplyBrightness();
                }
                ScheduleSettledReapply();
                ServiceLog.Info("[y70-display-heartbeat] newly detected; orientation and brightness re-applied");
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
                Y70DisplayProtocol.Y70TrulyProductId,
                Y70DisplayProtocol.Y70GtProductId))
        {
            return;
        }

        if (_hub.IsConnected && !_hub.HasBacklight
            && _presence.UsbPresent(Y70DisplayProtocol.VendorId, Y70DisplayProtocol.Y70GtProductId))
        {
            _hub.EnsureBacklightConnected();
        }

        var connectedBefore = _hub.IsConnected;
        if (!_hub.EnsureConnected()) return;
        if (!connectedBefore || string.IsNullOrEmpty(_hub.State.FirmwareVersion))
        {
            _hub.PollFirmwareVersion();
        }
    }
}

#if WINDOWS
/// <summary>Re-sends the stored Y70 orientation and brightness whenever a helper connects.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class Y70HelperConnectHook : IHostedService
{
    private readonly Nexus.Service.Helper.HelperRegistry _helpers;
    private readonly Y70DisplayHeartbeatWorker _worker;

    public Y70HelperConnectHook(Nexus.Service.Helper.HelperRegistry helpers, Y70DisplayHeartbeatWorker worker)
    {
        _helpers = helpers;
        _worker = worker;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _helpers.Connected += OnConnected;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _helpers.Connected -= OnConnected;
        return Task.CompletedTask;
    }

    // Off the pipe thread: the applies are helper RPCs and must not block the handshake that raised this.
    private void OnConnected(Nexus.Service.Helper.HelperConnection _) => Task.Run(_worker.OnHelperConnected);
}
#endif
