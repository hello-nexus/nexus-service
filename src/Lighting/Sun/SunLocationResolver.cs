using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Persistence;
using Nexus.Service.Platform.Weather;

namespace Nexus.Service.Lighting.Sun;

/// <summary>Resolves the IP location in the background for schedules that
/// follow it, so the frame path only ever reads the cached result. Asked at
/// startup, when sun shift is turned on and when a GET finds it on without a
/// location.</summary>
public sealed class SunLocationResolver : IHostedService
{
    private static readonly TimeSpan RetryGap = TimeSpan.FromSeconds(60);

    private readonly IWeatherProvider _weather;
    private readonly IConfigStore _store;
    private int _busy;
    private long _lastAttemptTicks;

    public SunLocationResolver(IWeatherProvider weather, IConfigStore store)
    {
        _weather = weather;
        _store = store;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var schedule = _store.Load().Lighting.BrightnessSchedule;
        if (schedule is { SunShift: true, SunLocation: null })
        {
            Request();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Starts a background lookup unless one is running, the location is
    /// known, or the last try was under a minute ago (unless <paramref name="force"/>).</summary>
    public void Request(bool force = false)
    {
        if (SunShift.AutoLocation is not null)
        {
            return;
        }
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastAttemptTicks);
        if (!force && last != 0 && now - last < RetryGap.TotalMilliseconds)
        {
            return;
        }
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }
        Interlocked.Exchange(ref _lastAttemptTicks, now);
        _ = Task.Run(async () =>
        {
            try
            {
                if (await _weather.GetApproximateLocationAsync().ConfigureAwait(false) is { } loc)
                {
                    SunShift.AutoLocation = loc;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[sun] location lookup failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }
}
