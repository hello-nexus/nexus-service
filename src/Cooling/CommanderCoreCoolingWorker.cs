using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>The Commander Core controllers the RGB bridge currently lists; empty while OpenRGB is off or disconnected.</summary>
public sealed class RgbBridgeCommanderCoreSource : CommanderCoreCoolingProvider.ISource
{
    private readonly Func<RgbBridge> _bridge;

    public RgbBridgeCommanderCoreSource(Func<RgbBridge> bridge)
    {
        _bridge = bridge;
    }

    public IReadOnlyList<CommanderCoreCoolingProvider.Endpoint> Endpoints()
    {
        var bridge = _bridge();
        if (!bridge.IsActive || !bridge.IsConnected)
        {
            return Array.Empty<CommanderCoreCoolingProvider.Endpoint>();
        }
        var result = new List<CommanderCoreCoolingProvider.Endpoint>();
        foreach (var d in bridge.Devices)
        {
            if (CommanderCoreCoolingProvider.IsCommanderCoreName(d.Name))
            {
                result.Add(new CommanderCoreCoolingProvider.Endpoint(d.StableId, d.Name, d.Address));
            }
        }
        return result;
    }
}

/// <summary>Runs <see cref="CommanderCoreCoolingProvider.PollOnceAsync"/> on a fixed cadence and right after a duty write.</summary>
public sealed class CommanderCoreCoolingWorker : BackgroundService
{
    private static readonly TimeSpan PollPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdlePeriod = TimeSpan.FromSeconds(5);

    private readonly CommanderCoreCoolingProvider _provider;

    public CommanderCoreCoolingWorker(CommanderCoreCoolingProvider provider)
    {
        _provider = provider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _provider.PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[cmdcore-cooling] poll failed: {ex.GetType().Name}: {ex.Message}");
            }
            await _provider.WaitForWorkAsync(_provider.HasDevices ? PollPeriod : IdlePeriod, stoppingToken).ConfigureAwait(false);
        }
    }
}
