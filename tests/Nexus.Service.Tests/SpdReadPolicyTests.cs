using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests;

public class SpdReadPolicyTests
{
    [Fact]
    public void ReadRamTemperatures_DefaultsTrue() => Assert.True(new MonitoringSettings().ReadRamTemperatures);

    [Fact]
    public void ReadRamTemperatures_MissingFromOldSettingsFile_IsTrue()
    {
        var parsed = System.Text.Json.JsonSerializer.Deserialize(
            "{\"smartPollDefaultSeconds\":60}", Nexus.Service.Serialization.PersistenceJsonContext.Default.MonitoringSettings);
        Assert.NotNull(parsed);
        Assert.True(parsed!.ReadRamTemperatures);
        Assert.Equal(60, parsed.SmartPollDefaultSeconds);
    }

    [Fact]
    public void ReadRamTemperatures_RoundTripsAsCamelCase()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new MonitoringSettings { ReadRamTemperatures = false }, Nexus.Service.Serialization.PersistenceJsonContext.Default.MonitoringSettings);
        Assert.Matches("\"readRamTemperatures\":\\s*false", json);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void ReadingEnabled_RequiresBothGates(bool gate, bool setting, bool expected) =>
        Assert.Equal(expected, SpdReadPolicy.ReadingEnabled(gate, setting));

    [Theory]
    [InlineData(true, true, "setting are on")]
    [InlineData(true, false, "monitoring RAM temperature setting is off")]
    [InlineData(false, true, "memory device is off")]
    public void Decide_NamesTheReason(bool gate, bool setting, string fragment) =>
        Assert.Contains(fragment, SpdReadPolicy.Decide(gate, setting, currentlyActive: true).Reason);

    [Fact]
    public void Decide_LiveToggleOfSetting_FlipsAndThenSettles()
    {
        var off = SpdReadPolicy.Decide(gateEnabled: true, readRamTemperatures: false, currentlyActive: true);
        Assert.True(off.Changed);
        Assert.False(off.Wanted);

        Assert.False(SpdReadPolicy.Decide(true, false, currentlyActive: false).Changed);

        var on = SpdReadPolicy.Decide(true, true, currentlyActive: false);
        Assert.True(on.Changed);
        Assert.True(on.Wanted);
    }

    [Fact]
    public void Decide_GateToggleWhileSettingOff_ChangesNothing() =>
        Assert.False(SpdReadPolicy.Decide(gateEnabled: true, readRamTemperatures: false, currentlyActive: false).Changed);

    [Fact]
    public void ResetRunner_Inactive_IsNoOp()
    {
        var called = false;
        var count = SpdPageResetRunner.Run(false, () => { called = true; return 3; }, TimeSpan.FromSeconds(1), _ => { });
        Assert.Equal(0, count);
        Assert.False(called);
    }

    [Fact]
    public void ResetRunner_Active_RunsAndLogsCount()
    {
        var lines = new List<string>();
        var count = SpdPageResetRunner.Run(true, () => 4, TimeSpan.FromSeconds(5), lines.Add);
        Assert.Equal(4, count);
        Assert.Contains("4 DIMM", Assert.Single(lines));
    }

    [Fact]
    public void ResetRunner_Throwing_DoesNotThrow()
    {
        var lines = new List<string>();
        var count = SpdPageResetRunner.Run(true, () => throw new InvalidOperationException("bus gone"), TimeSpan.FromSeconds(5), lines.Add);
        Assert.Equal(0, count);
        Assert.Contains("bus gone", Assert.Single(lines));
    }

    [Fact]
    public void ResetRunner_WedgedBus_ReturnsWithinBudget()
    {
        var release = new ManualResetEventSlim();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var count = SpdPageResetRunner.Run(true, () => { release.Wait(); return 1; }, TimeSpan.FromMilliseconds(100), _ => { });
        release.Set();
        Assert.Equal(0, count);
        Assert.True(sw.ElapsedMilliseconds < 3000);
    }
}
