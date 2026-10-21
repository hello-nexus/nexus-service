using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Peripherals.DeepCool;

/// <summary>
/// Keeps a Mystique screen on live data while it is present and Nexus Control is on: opens its
/// bulk pipes, writes the settings groups the user has changed, then pushes Nexus's sensors
/// once a second. Without a host the screen falls back to its own preset animation.
/// </summary>
public sealed class MystiqueWorker : BackgroundService
{
    public const string HandlerId = "deepcool-mystique";

    private const int MissesToDetach = 2;
    private const int PresencePollMs = 2000;
    private const int LogEveryMs = 60_000;

    private readonly IBulkUsbPipeFactory _pipes;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;
    private readonly IConfigStore _store;
    private readonly ISensorProvider _sensors;
    private readonly int _connectPollMs;
    private readonly int _tickMs;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile bool _connected;
    private long _lastLogMs = long.MinValue;

    public MystiqueWorker(
        IBulkUsbPipeFactory pipes, DeviceControlGate gate, HardwarePresence presence, IConfigStore store, ISensorProvider sensors,
        int connectPollMs = 5000, int tickMs = 1000)
    {
        _pipes = pipes;
        _gate = gate;
        _presence = presence;
        _store = store;
        _sensors = sensors;
        _connectPollMs = connectPollMs;
        _tickMs = tickMs;
    }

    public bool IsConnected => _connected;

    /// <summary>Tells the worker the saved settings changed; it sends the difference on its next pass.</summary>
    public void RequestApply()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled(HandlerId) || !_presence.UsbPresent(MystiqueProtocol.VendorId, MystiqueProtocol.ProductId))
                {
                    await Task.Delay(_connectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                if (!await RunSession(stoppingToken).ConfigureAwait(false))
                {
                    await Task.Delay(_connectPollMs, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"worker error: {ex.Message}", error: true);
                await Task.Delay(_connectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One open-to-close session. True when it ended because the screen or the gate went away, false when the link failed.</summary>
    private async Task<bool> RunSession(CancellationToken stoppingToken)
    {
        using var pipe = _pipes.Open(
            MystiqueProtocol.VendorId, MystiqueProtocol.ProductId, MystiqueProtocol.LiveWritePipe, MystiqueProtocol.LiveReadPipe);
        if (pipe is null)
        {
            Log("the screen's pipes could not be opened", error: false);
            return false;
        }
        var session = new MystiqueSession(pipe);
        if (!session.Open(ReadSettings(), DateTime.Now))
        {
            Log("the screen did not answer the opening exchange", error: false);
            return false;
        }
        ServiceLog.Info($"[{HandlerId}] connected {session.Info}");
        _connected = true;
        try
        {
            int misses = 0;
            long nextPresenceMs = Environment.TickCount64 + PresencePollMs;
            while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled(HandlerId))
            {
                await _wake.WaitAsync(_tickMs, stoppingToken).ConfigureAwait(false);
                var settings = ReadSettings();
                if (!session.Apply(settings) || !session.Tick(MystiqueSensors.Read(_sensors, settings), DateTime.Now))
                {
                    Log("a write was refused; reopening", error: false);
                    return false;
                }
                if (Environment.TickCount64 >= nextPresenceMs)
                {
                    nextPresenceMs = Environment.TickCount64 + PresencePollMs;
                    misses = _presence.UsbPresent(MystiqueProtocol.VendorId, MystiqueProtocol.ProductId) ? 0 : misses + 1;
                    if (misses >= MissesToDetach)
                    {
                        return true;
                    }
                }
            }
            return true;
        }
        finally
        {
            _connected = false;
            ServiceLog.Info($"[{HandlerId}] disconnected");
        }
    }

    private DeepCoolMystiqueSettings ReadSettings()
    {
        DeepCoolMystiqueSettings copy = new();
        _store.Read(s => copy = s.Devices.DeepCoolMystique.Copy());
        return copy;
    }

    private void Log(string message, bool error)
    {
        long now = Environment.TickCount64;
        if (_lastLogMs != long.MinValue && now - _lastLogMs < LogEveryMs)
        {
            return;
        }
        _lastLogMs = now;
        if (error)
        {
            ServiceLog.Error($"[{HandlerId}] {message}");
        }
        else
        {
            ServiceLog.Warn($"[{HandlerId}] {message}");
        }
    }
}
