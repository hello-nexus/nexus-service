using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Calendar;

/// <summary>
/// Ticks on a short interval and syncs whatever has come due. Comparing wall-clock due
/// times (rather than sleeping a full interval) makes a source sync within one
/// tick of the PC resuming from sleep, and lets a changed refresh interval take
/// effect without rescheduling anything.
/// </summary>
public sealed class CalendarSyncWorker : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private readonly CalendarService _calendar;
    private readonly IHostApplicationLifetime _lifetime;

    public CalendarSyncWorker(CalendarService calendar, IHostApplicationLifetime lifetime)
    {
        _calendar = calendar;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before the first outbound request.
        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var _ = _lifetime.ApplicationStarted.Register(() => started.TrySetResult());
            await started.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
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
