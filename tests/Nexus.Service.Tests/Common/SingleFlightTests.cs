using System;
using System.Threading.Tasks;
using Nexus.Service.Common;
using Xunit;

namespace Nexus.Service.Tests.Common;

public class SingleFlightTests
{
    [Fact]
    public void ConcurrentCallers_ShareTheRunningOperation()
    {
        var starts = 0;
        var gate = new TaskCompletionSource<int>();
        var flight = new SingleFlight<int>(() => { starts++; return gate.Task; }, TimeSpan.FromMinutes(5));

        var first = flight.Get();
        var second = flight.Get();

        Assert.Same(first, second);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task FinishedRun_IsNotReused()
    {
        var starts = 0;
        var flight = new SingleFlight<int>(() => Task.FromResult(++starts), TimeSpan.FromMinutes(5));

        Assert.Equal(1, await flight.Get());
        Assert.Equal(2, await flight.Get());
    }

    [Fact]
    public void HungRun_IsAbandonedAfterMaxAge()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var starts = 0;
        var flight = new SingleFlight<int>(() => { starts++; return new TaskCompletionSource<int>().Task; }, TimeSpan.FromMinutes(5), () => now);

        var hung = flight.Get();
        now += TimeSpan.FromMinutes(4);
        Assert.Same(hung, flight.Get());

        now += TimeSpan.FromMinutes(2);
        Assert.NotSame(hung, flight.Get());
        Assert.Equal(2, starts);
    }

    [Fact]
    public void RunIds_GrowWithEveryStartedRun()
    {
        var flight = new SingleFlight<int>(() => Task.FromResult(1), TimeSpan.FromMinutes(5));

        var first = flight.GetRun();
        var second = flight.GetRun();

        Assert.True(second.Id > first.Id);
    }

    [Fact]
    public async Task AbandonedRunCeiling_StopsNewRunsAndSaysSoOnce()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var starts = 0;
        var starved = 0;
        var flight = new SingleFlight<int>(
            () => { starts++; return new TaskCompletionSource<int>().Task; },
            TimeSpan.FromMinutes(5),
            () => now,
            maxAbandoned: 2,
            fallback: () => -7,
            onStarved: () => starved++);

        var first = flight.GetRun();
        now += TimeSpan.FromMinutes(6);
        var second = flight.GetRun();
        now += TimeSpan.FromMinutes(6);
        var third = flight.GetRun();
        var fourth = flight.GetRun();

        Assert.Equal(2, starts);
        Assert.True(first.Id >= 0 && second.Id > first.Id);
        Assert.True(third.Id < 0 && fourth.Id < 0);
        Assert.Equal(-7, await third.Task);
        Assert.Equal(1, starved);
    }

    [Fact]
    public async Task AbandonedRunThatFinishes_FreesItsSlot()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var hung = new TaskCompletionSource<int>();
        var starts = 0;
        var flight = new SingleFlight<int>(
            () => ++starts == 1 ? hung.Task : new TaskCompletionSource<int>().Task,
            TimeSpan.FromMinutes(5),
            () => now,
            maxAbandoned: 1);

        Assert.True(flight.GetRun().Id >= 0);
        now += TimeSpan.FromMinutes(6);
        Assert.True(flight.GetRun().Id < 0);

        hung.SetResult(1);
        await Task.Delay(50);

        Assert.True(flight.GetRun().Id >= 0);
    }
}

public class LatestRunValueTests
{
    [Fact]
    public void OlderRunCannotReplaceNewerData()
    {
        var cell = new LatestRunValue<string>();
        var current = "";

        Assert.True(cell.TryWrite(2, v => current = v, "newer"));
        Assert.False(cell.TryWrite(1, v => current = v, "older"));

        Assert.Equal("newer", current);
    }

    [Fact]
    public void SameRunMayReplaceItsOwnPlaceholder()
    {
        var cell = new LatestRunValue<string>();
        var current = "";

        cell.TryWrite(3, v => current = v, "empty");
        Assert.True(cell.TryWrite(3, v => current = v, "late result"));

        Assert.Equal("late result", current);
    }
}
