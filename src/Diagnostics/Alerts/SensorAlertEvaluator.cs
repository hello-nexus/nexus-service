using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lifecycle;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.Diagnostics.Alerts;

/// <summary>Drives <see cref="SensorAlertEngine.Tick"/> on a fixed interval while the Diagnostics pillar is on, and relays the engine's state changes to the diagnostics/alerts topic.</summary>
public sealed class SensorAlertEvaluator : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly SensorAlertEngine _engine;
    private readonly FeatureGates _gates;
    private readonly TimeProvider _time;
    private readonly LogThrottle _warnings = new(TimeSpan.FromMinutes(1));

    public SensorAlertEvaluator(SensorAlertEngine engine, FeatureGates gates, MultiplexHub hub)
        : this(engine, gates, hub, TimeProvider.System)
    {
    }

    internal SensorAlertEvaluator(SensorAlertEngine engine, FeatureGates gates, MultiplexHub hub, TimeProvider time)
    {
        _engine = engine;
        _gates = gates;
        _time = time;
        _engine.Changed += () => PanelTopics.BroadcastSensorAlerts(hub, _engine.States());
        // A client that subscribes after a restart or reconnect has seen no transition, so it is handed the current states.
        hub.RegisterSnapshotProvider(PanelTopics.SensorAlerts, () => PanelTopics.BuildSensorAlerts(_engine.States()));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (!_gates.Diagnostics)
                {
                    continue;
                }
                try { _engine.Tick(); }
                catch (Exception ex)
                {
                    if (_warnings.ShouldLog("tick", _time.GetUtcNow()))
                    {
                        ServiceLog.Warn($"[sensor-alerts] tick failed: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
