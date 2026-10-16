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
    public void ReadRamTemperatures_DefaultsTrue() => Assert.True(new NexusSettings().ReadRamTemperatures);

    [Fact]
    public void ReadRamTemperatures_MissingFromOldSettingsFile_IsTrue()
    {
        var parsed = System.Text.Json.JsonSerializer.Deserialize(
            "{\"startupDelaySeconds\":7}", Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings);
        Assert.NotNull(parsed);
        Assert.True(parsed!.ReadRamTemperatures);
        Assert.Equal(7, parsed.StartupDelaySeconds);
    }

    [Fact]
    public void ReadRamTemperatures_RoundTripsAsCamelCase()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new NexusSettings { ReadRamTemperatures = false }, Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings);
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

    private sealed class FakeBus : ISpdBus
    {
        public Dictionary<(byte, byte), int> Regs = new();
        public List<string> Calls = new();
        public int ReadByte(byte address, byte register)
        {
            Calls.Add($"r{address:X2}.{register}");
            return Regs.TryGetValue((address, register), out var v) ? v : -1;
        }
        public bool ResetDdr5Page(byte address) { Calls.Add($"d5reset{address:X2}"); return true; }
        public void SelectDdr4Page0() => Calls.Add("spa0");
        public FakeBus Ddr5(byte a) { Regs[(a, 0)] = 0x51; Regs[(a, 1)] = 0x18; return this; }
        public FakeBus Ddr4(byte a) { Regs[(a, 0)] = 0x23; Regs[(a, 1)] = 0x10; return this; }
    }

    private static readonly byte[] Slots = { 0x50, 0x52 };

    [Fact]
    public void Reset_Ddr5_UsesMr11PathAndNeverSpa0()
    {
        var bus = new FakeBus().Ddr5(0x50).Ddr5(0x52);
        Assert.Equal(2, SpdPageReset.Run(new[] { bus }, Slots, () => false));
        Assert.Contains("d5reset50", bus.Calls);
        Assert.DoesNotContain("spa0", bus.Calls);
    }

    [Fact]
    public void Reset_Ddr4_IssuesOneSpa0PerBus()
    {
        var bus = new FakeBus().Ddr4(0x50).Ddr4(0x52);
        Assert.Equal(2, SpdPageReset.Run(new[] { bus }, Slots, () => false));
        Assert.Single(bus.Calls, c => c == "spa0");
        Assert.DoesNotContain(bus.Calls, c => c.StartsWith("d5reset"));
    }

    [Fact]
    public void Reset_FailedRead_SkipsAddressWithoutWriting()
    {
        var bus = new FakeBus();
        Assert.Equal(0, SpdPageReset.Run(new[] { bus }, Slots, () => false));
        Assert.DoesNotContain("spa0", bus.Calls);
        Assert.DoesNotContain(bus.Calls, c => c.StartsWith("d5reset"));
    }

    [Fact]
    public void Reset_OnlyTouchesGivenAddresses()
    {
        var bus = new FakeBus().Ddr5(0x50).Ddr5(0x51);
        SpdPageReset.Run(new[] { bus }, new byte[] { 0x50 }, () => false);
        Assert.DoesNotContain(bus.Calls, c => c.Contains("51"));
    }

    [Fact]
    public void Reset_NoAddresses_IsNoOp()
    {
        var bus = new FakeBus();
        Assert.Equal(0, SpdPageReset.Run(new[] { bus }, Array.Empty<byte>(), () => false));
        Assert.Empty(bus.Calls);
    }

    [Fact]
    public void Reset_DdrBusWithoutDdr4_GetsNoSpa0OnOtherBus()
    {
        var ddr5 = new FakeBus().Ddr5(0x50);
        var ddr4 = new FakeBus().Ddr4(0x50);
        SpdPageReset.Run(new[] { ddr5, ddr4 }, new byte[] { 0x50 }, () => false);
        Assert.DoesNotContain("spa0", ddr5.Calls);
        Assert.Contains("spa0", ddr4.Calls);
    }

    [Fact]
    public void Reset_PastDeadline_StopsIssuingTransactions()
    {
        var bus = new FakeBus().Ddr4(0x50).Ddr4(0x52);
        Assert.Equal(0, SpdPageReset.Run(new[] { bus }, Slots, () => true));
        Assert.Empty(bus.Calls);

        var reads = 0;
        var late = new FakeBus().Ddr4(0x50).Ddr4(0x52);
        SpdPageReset.Run(new[] { late }, Slots, () => ++reads > 2);
        Assert.DoesNotContain("spa0", late.Calls);
    }

    [Fact]
    public void ResetRunner_Inactive_IsNoOp()
    {
        var called = false;
        var count = SpdPageResetRunner.Run(false, _ => { called = true; return 3; }, TimeSpan.FromSeconds(1), _ => { });
        Assert.Equal(0, count);
        Assert.False(called);
    }

    [Fact]
    public void ResetRunner_Active_RunsAndLogsCount()
    {
        var lines = new List<string>();
        var count = SpdPageResetRunner.Run(true, _ => 4, TimeSpan.FromSeconds(5), lines.Add);
        Assert.Equal(4, count);
        Assert.Contains("4 DIMM", Assert.Single(lines));
    }

    [Fact]
    public void ResetRunner_Throwing_DoesNotThrow()
    {
        var lines = new List<string>();
        var count = SpdPageResetRunner.Run(true, _ => throw new InvalidOperationException("bus gone"), TimeSpan.FromSeconds(5), lines.Add);
        Assert.Equal(0, count);
        Assert.Contains("bus gone", Assert.Single(lines));
    }

    [Fact]
    public void ResetRunner_WedgedBus_ReturnsWithinBudgetAndExpiresProbe()
    {
        var release = new ManualResetEventSlim();
        Func<bool>? probe = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var count = SpdPageResetRunner.Run(true, expired => { probe = expired; release.Wait(); return 1; }, TimeSpan.FromMilliseconds(100), _ => { });
        Assert.Equal(0, count);
        Assert.True(sw.ElapsedMilliseconds < 3000);
        Assert.True(probe!());
        release.Set();
    }
}
