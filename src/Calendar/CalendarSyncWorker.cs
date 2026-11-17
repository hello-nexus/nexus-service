using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Calendar;

/// <summary>
/// Ticks every 30 s and syncs whatever has come due. Comparing wall-clock due
/// times (rather than sleeping a full interval) makes a source sync within one
/// tick of the PC resuming from sleep, and lets a changed refresh interval take
/// effect without rescheduling anything.
/// </summary>
public sealed class CalendarSyncWorker : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private readonly CalendarService _calendar;

    public CalendarSyncWorker(CalendarService calendar) => _calendar = calendar;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before the first outbound request.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _calendar.RunDueAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Nexus.Service.Platform.ServiceLog.Warn($"[calendar] sync tick failed ({ex.GetType().Name})");
                }
                await Task.Delay(Tick, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
