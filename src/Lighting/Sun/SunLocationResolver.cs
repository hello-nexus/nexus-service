using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Weather;
using Nexus.Service.Sockets;

namespace Nexus.Service.Lighting.Sun;

/// <summary>Resolves the IP location for a schedule that follows it, so the
/// frame path only ever reads the cached result. The loop looks the place up
/// while the schedule is in use and no place is known, backing off after each
/// failure, and refreshes a known place now and then. Routes poke it through
/// <see cref="Request"/> when the settings change.</summary>
public sealed class SunLocationResolver : IHostedService
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Refresh = TimeSpan.FromHours(1);
    private static readonly TimeSpan IdleRecheck = TimeSpan.FromMinutes(5);

    private readonly IWeatherProvider _weather;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationTokenSource? _cts;
    private Task _loop = Task.CompletedTask;
    private int _forced;

    public SunLocationResolver(IWeatherProvider weather, IConfigStore store, MultiplexHub hub)
    {
        _weather = weather;
        _store = store;
        _hub = hub;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Dispose();
        _cts = null;
    }

    /// <summary>Wakes the loop. A forced request also skips the backoff wait, for a setting the user just changed.</summary>
    public void Request(bool force = false)
    {
        if (force)
        {
            Volatile.Write(ref _forced, 1);
        }
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var retry = FirstRetry;
        long nextAttempt = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = Environment.TickCount64;
                TimeSpan wait;
                if (!SunShift.NeedsAutoLocation(_store.Load().Lighting.BrightnessSchedule))
                {
                    retry = FirstRetry;
                    nextAttempt = 0;
                    wait = IdleRecheck;
                }
                else
                {
                    if (now >= nextAttempt)
                    {
                        if (await TryResolveAsync().ConfigureAwait(false))
                        {
                            retry = FirstRetry;
                            nextAttempt = now + (long)Refresh.TotalMilliseconds;
                        }
                        else
                        {
                            nextAttempt = now + (long)retry.TotalMilliseconds;
                            retry = TimeSpan.FromTicks(Math.Min(retry.Ticks * 2, MaxRetry.Ticks));
                        }
                    }
                    wait = TimeSpan.FromMilliseconds(Math.Max(nextAttempt - Environment.TickCount64, 0));
                }
                await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _forced, 0) == 1)
                {
                    retry = FirstRetry;
                    nextAttempt = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> TryResolveAsync()
    {
        try
        {
            if (await _weather.GetApproximateLocationAsync().ConfigureAwait(false) is not { } loc)
            {
                return false;
            }
            var previous = SunShift.AutoLocation;
            SunShift.AutoLocation = loc;
            if (previous is null || previous.Lat != loc.Lat || previous.Lon != loc.Lon || previous.Label != loc.Label)
            {
                PanelTopics.BroadcastLighting(_hub);
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[sun] location lookup failed: {ex.Message}");
            return false;
        }
    }
}
