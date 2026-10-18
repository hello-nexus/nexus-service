using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Telemetry;

/// <summary>Runs <see cref="FleetEventService.RunPendingRetriesAsync"/> at boot and hourly so a failed delivery or an interrupted consent transition eventually reaches nexus-api.</summary>
internal sealed class FleetTelemetryWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly FleetEventService _fleet;

    public FleetTelemetryWorker(FleetEventService fleet) => _fleet = fleet;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PassAsync(stoppingToken).ConfigureAwait(false);

        // Specs wait for the USB bus to settle, so they get a pass just after that window instead of an hour later.
        try { await Task.Delay(FleetEventService.UsbSettleDelay + TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        await PassAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(Interval);
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false))
            await PassAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task PassAsync(CancellationToken ct)
    {
        try
        {
            if (!Nexus.Service.FocusModes.FocusNetworkGate.IsHeld)
                await _fleet.RunPendingRetriesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fleet-event] retry pass failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
