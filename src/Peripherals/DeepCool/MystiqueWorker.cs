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
/// every tick. Without a host the screen falls back to its own preset animation.
/// </summary>
public sealed class MystiqueWorker : BackgroundService
{
    public const string HandlerId = "deepcool-mystique";

    private const int MissesToDetach = 2;
    private const int PresencePollMs = 2000;
    private const int LogEveryMs = 60_000;
    private const int MaxBackoffMs = 5 * 60 * 1000;
    private const int StableSessionMs = 30_000;

    private readonly IBulkUsbPipeFactory _pipes;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;
    private readonly IConfigStore _store;
    private readonly ISensorProvider _sensors;
    private readonly Nexus.Service.Cooling.IFanControlProvider? _fans;
    private readonly int _connectPollMs;
    private readonly int _tickMs;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile bool _connected;
    private long _lastLogMs = long.MinValue;

    public MystiqueWorker(
        IBulkUsbPipeFactory pipes, DeviceControlGate gate, HardwarePresence presence, IConfigStore store, ISensorProvider sensors,
        int connectPollMs = 5000, int tickMs = 1000, Nexus.Service.Cooling.IFanControlProvider? fans = null)
    {
        _fans = fans;
        _pipes = pipes;
        _gate = gate;
        _gate.Changed += OnGateChanged;
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

    private void OnGateChanged(string handlerId, bool enabled)
    {
        if (handlerId == HandlerId)
        {
            RequestApply();
        }
    }

    public override void Dispose()
    {
        _gate.Changed -= OnGateChanged;
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled(HandlerId) || !_presence.UsbPresent(MystiqueProtocol.VendorId, MystiqueProtocol.ProductId))
                {
                    failures = 0;
                    await WaitAsync(_connectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                long startedMs = Environment.TickCount64;
                if (await RunSession(stoppingToken).ConfigureAwait(false) || Environment.TickCount64 - startedMs >= StableSessionMs)
                {
                    failures = 0;
                }
                else
                {
                    failures++;
                    await WaitAsync(Backoff(failures), stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                Log($"worker error: {ex.Message}", error: true);
                await WaitAsync(Backoff(failures), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Exponential from the connect poll, capped at a few minutes.</summary>
    private int Backoff(int failures)
    {
        long delay = _connectPollMs;
        for (int i = 1; i < failures && delay < MaxBackoffMs; i++)
        {
            delay *= 2;
        }
        return (int)Math.Min(delay, Math.Max(MaxBackoffMs, _connectPollMs));
    }

    /// <summary>A wait that ends early when the gate flips or a change is requested.</summary>
    private async Task WaitAsync(int delayMs, CancellationToken ct)
    {
        await _wake.WaitAsync(delayMs, ct).ConfigureAwait(false);
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
                if (!_gate.IsEnabled(HandlerId))
                {
                    break;
                }
                var settings = ReadSettings();
                if (!session.Apply(settings) || !session.Tick(MystiqueSensors.Read(_sensors, settings, MystiqueSensors.FanNames(_store, _fans)), DateTime.Now))
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
