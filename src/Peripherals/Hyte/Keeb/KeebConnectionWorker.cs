using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Peripherals.Keeb;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Opens the keeb's vendor HID interface at startup and keeps it open across
/// hot-plug. Unlike the NP50 hub the keeb needs no heartbeat to retain
/// control, so this worker only owns connect/reconnect + notifying the
/// lighting provider when the device appears or disappears (so the lighting
/// page + engine refresh). RGB frames are pushed separately by
/// <see cref="KeebLightingFrameWriter"/>.
/// </summary>
public sealed class KeebConnectionWorker : BackgroundService
{
    private const int PollMs = 1000;

    private readonly KeebHub _hub;
    private readonly KeebSettingsApplier _applier;
    private readonly HardwarePresence _presence;
    private readonly LightingEngine _engine;
    private readonly DeviceControlGate _gate;
    private readonly IKeebProvider _provider;
    private readonly KeebLightingDeviceProvider? _lighting;
    private bool _lastConnected;

    public KeebConnectionWorker(KeebHub hub, KeebSettingsApplier applier, HardwarePresence presence, LightingEngine engine, DeviceControlGate gate, IKeebProvider provider, KeebLightingDeviceProvider? lighting = null)
    {
        _hub = hub;
        _applier = applier;
        _presence = presence;
        _engine = engine;
        _gate = gate;
        _provider = provider;
        _lighting = lighting;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollMs));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex)
            {
                ServiceLog.Error($"[keeb-conn] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        _hub.Disconnect();
    }

    /// <summary>One connect/notify cycle. Public so debug routes can step it.</summary>
    public void Tick()
    {
        if (!_gate.IsEnabled("keeb"))
        {
            if (_hub.IsConnected) _hub.Disconnect();
            NoteDisconnected();
            return;
        }

        // USB presence is the fast unplug signal: reads/writes on a dead
        // handle can keep "succeeding" at the HID layer for a while (or only
        // fail sporadically), but the bus knows immediately.
        var present = _presence.UsbPresent(KeebProtocol.VendorId, KeebProtocol.ProductId);
        if (!present)
        {
            if (_hub.IsConnected) _hub.Disconnect();
            NoteDisconnected();
            return;
        }

        _hub.EnsureConnected();
        var connected = _hub.IsConnected;
        if (connected != _lastConnected)
        {
            _lastConnected = connected;
            if (connected)
            {
                // On (re)connect: read device info (firmware version + layout),
                // seed the sync baselines WITHOUT adopting device bytes (the
                // store is desired state - changes made while unplugged must
                // win), push the saved settings, then re-push persisted key
                // assignments + macros so onboard storage matches the store.
                _hub.ReadDeviceInfo();
                _applier.SeedBaselines();
                _applier.Apply();
                _provider.ApplyPersistedAssignments();
            }
            _lighting?.OnConnectionChanged();
            return;
        }
        // While connected and the firmware animation is showing, poll the device's
        // effect + brightness each tick so the panel follows a hardware-side change
        // (middle button cycles the effect, the knob moves the brightness byte, both
        // with NO host callback in firmware rotary mode). While a software effect
        // streams, the frame writer is the sole reader on a faster cadence - a second
        // reader here would race it and bounce the brightness mid-knob-turn.
        if (connected && _engine.CurrentEffectName == "none") _applier.SyncFromDevice();
    }

    // Flip the transition flag on every disconnect path (gate-off, bus
    // removal, handle drop) so the NEXT connect is seen as a transition and
    // re-runs the on-connect sequence.
    private void NoteDisconnected()
    {
        if (!_lastConnected) return;
        _lastConnected = false;
        _lighting?.OnConnectionChanged();
    }
}
