using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.FanLcd;
using Xunit;

namespace Nexus.Service.Tests.FanLcd;

public class FanLcdFrameClockTests
{
    private static long In(int ms) => Stopwatch.GetTimestamp() + (Stopwatch.Frequency * ms / 1000);

    [Fact]
    public async Task A_deadline_that_has_passed_returns_at_once()
    {
        using var clock = new FanLcdFrameClock();
        var watch = Stopwatch.StartNew();

        await clock.WaitUntilAsync(Stopwatch.GetTimestamp() - Stopwatch.Frequency, CancellationToken.None);

        Assert.True(watch.ElapsedMilliseconds < 100);
    }

    [Fact]
    public async Task A_future_deadline_waits_for_it_and_waits_can_follow_one_another()
    {
        using var clock = new FanLcdFrameClock();
        var watch = Stopwatch.StartNew();

        await clock.WaitUntilAsync(In(60), CancellationToken.None);
        await clock.WaitUntilAsync(In(60), CancellationToken.None);

        // Timers may fire a tick early on the fallback; the two waits together still take about two periods.
        Assert.InRange(watch.ElapsedMilliseconds, 100, 1000);
    }

    [Fact]
    public async Task A_cancelled_token_ends_the_wait()
    {
        using var clock = new FanLcdFrameClock();
        using var cts = new CancellationTokenSource(30);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clock.WaitUntilAsync(In(10_000), cts.Token));
    }
}
