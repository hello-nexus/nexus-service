using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Lighting;

/// <summary>Clears quiet keys on a cadence and tells lighting subscribers to refetch when a key flips.</summary>
public sealed class LightingWriteHealthBroadcaster : BackgroundService
{
    private static readonly TimeSpan SweepPeriod = TimeSpan.FromSeconds(5);

    private readonly LightingWriteHealth _health;
    private readonly Nexus.Service.Sockets.MultiplexHub _hub;
    private readonly Nexus.Service.Devices.DeviceBroadcaster _devices;

    public LightingWriteHealthBroadcaster(LightingWriteHealth health, Nexus.Service.Sockets.MultiplexHub hub,
        Nexus.Service.Devices.DeviceBroadcaster devices)
    {
        _health = health;
        _hub = hub;
        _devices = devices;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnChanged()
        {
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(_hub);
            _devices.BroadcastNow();
        }
        _health.Changed += OnChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(SweepPeriod, stoppingToken).ConfigureAwait(false);
                _health.Sweep();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _health.Changed -= OnChanged;
        }
    }
}
