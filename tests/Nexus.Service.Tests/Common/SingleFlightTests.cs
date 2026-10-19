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
}
