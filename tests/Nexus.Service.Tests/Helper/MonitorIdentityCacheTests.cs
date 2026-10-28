using System;
using Nexus.Service.Helper;
using Xunit;

namespace Nexus.Service.Tests.Helper;

public class MonitorIdentityCacheTests
{
    private static string Id(string device) => "id:" + device;

    [Fact]
    public void RepeatLookupResolvesOnce()
    {
        long now = 0;
        var cache = new MonitorIdentityCache(TimeSpan.FromSeconds(60), () => now);
        var calls = 0;
        string Resolve(string d) { calls++; return Id(d); }

        Assert.Equal("id:A", cache.GetOrResolve("A", 2, Resolve));
        now += 2000;
        Assert.Equal("id:A", cache.GetOrResolve("A", 2, Resolve));
        Assert.Equal(1, calls);
        Assert.Equal((1, 1), cache.TakeCounts());
        Assert.Equal((0, 0), cache.TakeCounts());
    }

    [Fact]
    public void DevicesAreCachedSeparately()
    {
        var cache = new MonitorIdentityCache(TimeSpan.FromSeconds(60), () => 0);
        var calls = 0;
        string Resolve(string d) { calls++; return Id(d); }

        Assert.Equal("id:A", cache.GetOrResolve("A", 2, Resolve));
        Assert.Equal("id:B", cache.GetOrResolve("B", 2, Resolve));
        Assert.Equal("id:A", cache.GetOrResolve("A", 2, Resolve));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void MonitorCountChangeForcesResolve()
    {
        var cache = new MonitorIdentityCache(TimeSpan.FromSeconds(60), () => 0);
        var current = "old";
        string Resolve(string d) => current;

        Assert.Equal("old", cache.GetOrResolve("A", 2, Resolve));
        current = "new";
        Assert.Equal("old", cache.GetOrResolve("A", 2, Resolve));
        Assert.Equal("new", cache.GetOrResolve("A", 1, Resolve));
    }

    [Theory]
    [InlineData(59_999, false)]
    [InlineData(60_000, true)]
    public void TtlBoundsStaleness(long elapsedMs, bool expectResolve)
    {
        long now = 0;
        var cache = new MonitorIdentityCache(TimeSpan.FromSeconds(60), () => now);
        var current = "old";
        string Resolve(string d) => current;

        cache.GetOrResolve("A", 2, Resolve);
        current = "new";
        now = elapsedMs;
        Assert.Equal(expectResolve ? "new" : "old", cache.GetOrResolve("A", 2, Resolve));
    }
}
