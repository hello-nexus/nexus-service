using System;
using Nexus.Service.Platform;
using Xunit;

namespace Nexus.Service.Tests.Platform;

public class LogGateTests
{
    private long _now;
    private LogGate Gate(int seconds = 60, int maxKeys = 512) => new(TimeSpan.FromSeconds(seconds), () => _now, maxKeys);

    [Fact]
    public void FirstOccurrence_AlwaysEmits()
    {
        var gate = Gate();
        Assert.True(gate.Check("k", "s", out var suppressed));
        Assert.Equal(0, suppressed);
    }

    [Fact]
    public void Repeats_WithinInterval_AreWithheld_AndCounted()
    {
        var gate = Gate();
        gate.Check("k", "s", out _);
        for (var i = 0; i < 5; i++)
        {
            _now += 1000;
            Assert.False(gate.Check("k", "s", out _));
        }
        _now += 60_000;
        Assert.True(gate.Check("k", "s", out var suppressed));
        Assert.Equal(5, suppressed);
    }

    [Fact]
    public void SummaryCount_ResetsAfterEachEmission()
    {
        var gate = Gate();
        gate.Check("k", "s", out _);
        _now += 1000;
        gate.Check("k", "s", out _);
        _now += 60_000;
        gate.Check("k", "s", out var first);
        _now += 60_000;
        Assert.True(gate.Check("k", "s", out var second));
        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public void StateChange_EmitsImmediately_EvenInsideInterval()
    {
        var gate = Gate();
        gate.Check("k", "locked", out _);
        _now += 10;
        gate.Check("k", "locked", out _);
        _now += 10;
        Assert.True(gate.Check("k", "settling", out var suppressed));
        Assert.Equal(1, suppressed);
        _now += 10;
        Assert.True(gate.Check("k", "locked", out _));
    }

    [Fact]
    public void Keys_AreIndependent()
    {
        var gate = Gate();
        Assert.True(gate.Check("a", "s", out _));
        Assert.True(gate.Check("b", "s", out _));
        Assert.False(gate.Check("a", "s", out _));
        Assert.False(gate.Check("b", "s", out _));
    }

    [Fact]
    public void IntervalBoundary_EmitsAtExactlyTheInterval()
    {
        var gate = Gate(10);
        gate.Check("k", "s", out _);
        _now += 9_999;
        Assert.False(gate.Check("k", "s", out _));
        _now += 1;
        Assert.True(gate.Check("k", "s", out _));
    }

    [Fact]
    public void Reset_ReportsWhetherKeyHadState_AndReturnsCount()
    {
        var gate = Gate();
        Assert.False(gate.Reset("k", out _));
        gate.Check("k", "s", out _);
        gate.Check("k", "s", out _);
        gate.Check("k", "s", out _);
        Assert.True(gate.Reset("k", out var suppressed));
        Assert.Equal(2, suppressed);
        Assert.False(gate.Reset("k", out _));
        Assert.True(gate.Check("k", "s", out _));
    }

    [Fact]
    public void KeyCap_NeverWithholdsAFirstOccurrence()
    {
        var gate = Gate(maxKeys: 3);
        for (var i = 0; i < 10; i++)
            Assert.True(gate.Check("k" + i, "s", out _));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, " suppressed=1")]
    [InlineData(250, " suppressed=250")]
    public void Suffix_NamesTheCountOnlyWhenNonZero(int count, string expected)
    {
        Assert.Equal(expected, LogGate.Suffix(count));
    }
}
