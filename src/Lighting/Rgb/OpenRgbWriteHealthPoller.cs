using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>Turns the daemon's cumulative write counters into per-poll deltas on <see cref="LightingWriteHealth"/>.</summary>
public sealed class OpenRgbWriteHealthPoller
{
    private readonly LightingWriteHealth _health;
    private readonly IOpenRgbWriteHealthTransport _transport;
    private readonly Dictionary<int, (uint Failures, uint Successes)> _last = new();
    private bool _unsupported;

    public OpenRgbWriteHealthPoller(LightingWriteHealth health, IOpenRgbWriteHealthTransport transport)
    {
        _health = health;
        _transport = transport;
    }

    /// <summary>True once the daemon ACKed the packet as unsupported; polling stops until <see cref="Reset"/>.</summary>
    public bool Unsupported => _unsupported;

    /// <summary>Forgets baselines and the unsupported verdict, for a new daemon connection.</summary>
    public void Reset()
    {
        _last.Clear();
        _unsupported = false;
    }

    /// <summary>One poll over the frames Nexus is driving: frame id and the controller address it writes to.</summary>
    public async Task PollOnceAsync(IReadOnlyList<(string FrameId, int Address)> controlled, CancellationToken ct)
    {
        if (_unsupported || controlled.Count == 0)
        {
            return;
        }
        var byAddress = new Dictionary<int, List<string>>();
        foreach (var (frameId, address) in controlled)
        {
            if (!byAddress.TryGetValue(address, out var ids))
            {
                ids = new List<string>();
                byAddress[address] = ids;
            }
            ids.Add(frameId);
        }
        foreach (var (address, ids) in byAddress)
        {
            var result = await _transport.GetWriteHealthAsync(address, ct).ConfigureAwait(false);
            if (result.Outcome == OpenRgbWriteHealthOutcome.Unsupported)
            {
                _unsupported = true;
                return;
            }
            if (result.Outcome != OpenRgbWriteHealthOutcome.Ok)
            {
                continue;
            }
            var now = (result.Health.Failures, result.Health.Successes);
            var seen = _last.TryGetValue(address, out var before);
            _last[address] = now;
            // A counter that went backwards is a restarted daemon: the new value is only a baseline.
            if (!seen || now.Failures < before.Failures || now.Successes < before.Successes)
            {
                continue;
            }
            var failures = (int)Math.Min(now.Failures - before.Failures, int.MaxValue);
            var successes = (int)Math.Min(now.Successes - before.Successes, int.MaxValue);
            foreach (var id in ids)
            {
                _health.Report(id, failures, successes);
            }
        }
    }

    /// <summary>Bridge frames Nexus drives: not marked uncontrolled. Uncontrolled ids are forgotten on the tracker.</summary>
    internal static List<(string FrameId, int Address)> ControlledFrames(
        IReadOnlyList<DeviceFrame> frames, IReadOnlySet<string> bridgeFrameIds, IReadOnlyList<string> uncontrolled, LightingWriteHealth health)
    {
        var result = new List<(string, int)>();
        foreach (var f in frames)
        {
            if (!bridgeFrameIds.Contains(f.Id))
            {
                continue;
            }
            if (uncontrolled.Contains(f.Id))
            {
                health.Forget(f.Id);
                continue;
            }
            result.Add((f.Id, f.PhysicalIndex));
        }
        return result;
    }
}

/// <summary>Polls the daemon's write health while the bridge is connected and lighting is being driven.</summary>
public sealed class OpenRgbWriteHealthWorker : BackgroundService
{
    internal static readonly TimeSpan PollPeriod = TimeSpan.FromSeconds(5);

    private readonly OpenRgbWriteHealthPoller _poller;
    private readonly LightingWriteHealth _health;
    private readonly RgbBridge _bridge;
    private readonly LightingEngine _engine;
    private readonly IConfigStore _store;

    public OpenRgbWriteHealthWorker(OpenRgbWriteHealthPoller poller, LightingWriteHealth health, RgbBridge bridge,
        LightingEngine engine, IConfigStore store)
    {
        _poller = poller;
        _health = health;
        _bridge = bridge;
        _engine = engine;
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wasConnected = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollPeriod, stoppingToken).ConfigureAwait(false);
                var connected = _bridge.IsActive && _bridge.IsConnected;
                if (connected && !wasConnected)
                {
                    _poller.Reset();
                }
                wasConnected = connected;
                if (!connected)
                {
                    continue;
                }
                var controlled = OpenRgbWriteHealthPoller.ControlledFrames(
                    _engine.Devices, _bridge.BridgeFrameIds, _store.Load().Devices.UncontrolledLightingDevices, _health);
                await _poller.PollOnceAsync(controlled, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[write-health] openrgb poll failed: {ex.Message}");
            }
        }
    }
}
