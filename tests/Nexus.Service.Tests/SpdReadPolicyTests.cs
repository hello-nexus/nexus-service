using System;
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
}
