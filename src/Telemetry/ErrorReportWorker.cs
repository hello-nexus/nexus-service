using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Telemetry;

/// <summary>First flush about a minute after start (delivers a leftover crash file), then every few minutes.</summary>
internal sealed class ErrorReportWorker : BackgroundService
{
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly ErrorReporter _reporter;

    public ErrorReportWorker(ErrorReporter reporter) => _reporter = reporter;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!Nexus.Service.FocusModes.FocusNetworkGate.IsHeld)
                    await _reporter.FlushAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
