using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Covers liquidctl --json status parsing (channel pairing, temps) and the
/// control path (right address/channel/duty reach the CLI seam) without a real
/// liquidctl binary.
/// </summary>
public class LinuxLiquidctlProviderTests
{
    private const string KrakenJson = """
    [
      {
        "bus": "hid",
        "address": "/dev/hidraw3",
        "description": "NZXT Kraken X53",
        "status": [
          {"key": "Liquid temperature", "value": 29.9, "unit": "°C"},
          {"key": "Pump speed", "value": 1890, "unit": "rpm"},
          {"key": "Pump duty", "value": 60, "unit": "%"}
        ]
      }
    ]
    """;

    private const string CommanderJson = """
    [
      {
        "bus": "hid",
        "address": "/dev/hidraw5",
        "description": "Corsair Commander Pro",
        "status": [
          {"key": "Fan 1 speed", "value": 800, "unit": "rpm"},
          {"key": "Fan 1 duty", "value": 40, "unit": "%"},
          {"key": "Fan 2 speed", "value": 0, "unit": "rpm"},
          {"key": "Temperature 1", "value": 25.2, "unit": "°C"},
          {"key": "Firmware version", "value": "0.9.212", "unit": ""}
        ]
      }
    ]
    """;

    [Fact]
    public void ParseStatus_Kraken_PumpChannelAndLiquidTemp()
    {
        var devices = LinuxLiquidctlProvider.ParseStatus(KrakenJson);
        var dev = Assert.Single(devices);
        Assert.Equal("/dev/hidraw3", dev.Address);
        Assert.Equal("NZXT Kraken X53", dev.Description);

        var pump = Assert.Single(dev.Channels);
        Assert.Equal("pump", pump.Name);
        Assert.Equal(1890, pump.Rpm);
        Assert.Equal(60, pump.Duty);

        var temp = Assert.Single(dev.Temps);
        Assert.Equal("Liquid temperature", temp.Label);
        Assert.Equal(29.9f, temp.Celsius, 1);
    }

    [Fact]
    public void ParseStatus_Commander_NumberedFansAndIgnoresNonSensorRows()
    {
        var dev = Assert.Single(LinuxLiquidctlProvider.ParseStatus(CommanderJson));
        // fan1 (rpm+duty) and fan2 (rpm only). Firmware string row is ignored.
        Assert.Equal(new[] { "fan1", "fan2" }, dev.Channels.Select(c => c.Name).ToArray());
        var fan1 = dev.Channels[0];
        Assert.Equal(800, fan1.Rpm);
        Assert.Equal(40, fan1.Duty);
        var fan2 = dev.Channels[1];
        Assert.Equal(0, fan2.Rpm);
        Assert.Null(fan2.Duty);
        Assert.Equal("Temperature 1", Assert.Single(dev.Temps).Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void ParseStatus_GarbageOrEmpty_NoDevices(string json)
        => Assert.Empty(LinuxLiquidctlProvider.ParseStatus(json));

    [Fact]
    public void GetFanChannels_BuildsRouteSafeIdsAndDeviceGrouping()
    {
        var p = new LinuxLiquidctlProvider(() => KrakenJson, (_, _, _) => true);
        var ch = Assert.Single(p.GetFanChannels());
        Assert.Equal("liquidctl:dev-hidraw3:pump", ch.Id);
        Assert.DoesNotContain('/', ch.Id); // route-safe
        Assert.Equal("liquidctl:dev-hidraw3", ch.DeviceId);
        Assert.Equal("NZXT Kraken X53", ch.DeviceName);
        Assert.Equal(60, ch.DutyPercent);
        Assert.Equal(1890, ch.Rpm);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void NativeKrakenAttached_HidesLiquidctlKrakenEverywhere(bool nativeAttached, int expected)
    {
        var p = new LinuxLiquidctlProvider(() => KrakenJson, (_, _, _) => true, nativeKraken: () => nativeAttached);
        Assert.Equal(expected, p.GetFanChannels().Count);
        Assert.Equal(expected, p.GetTemperatureSources().Count);
        Assert.Equal(expected, p.GetAll().Count);
    }

    [Fact]
    public void NativeKrakenAttached_KeepsOtherLiquidctlDevices()
    {
        var p = new LinuxLiquidctlProvider(() => CommanderJson, (_, _, _) => true, nativeKraken: () => true);
        Assert.Equal(2, p.GetFanChannels().Count);
    }

    [Fact]
    public void SetFanSpeed_SendsAddressChannelDutyToCli()
    {
        var calls = new List<(string addr, string chan, int duty)>();
        var p = new LinuxLiquidctlProvider(() => CommanderJson, (a, c, d) => { calls.Add((a, c, d)); return true; });
        p.GetFanChannels(); // populate the control map

        p.SetFanSpeed("liquidctl:dev-hidraw5:fan1", 75);

        var call = Assert.Single(calls);
        Assert.Equal("/dev/hidraw5", call.addr); // raw address, not the sanitized id
        Assert.Equal("fan1", call.chan);
        Assert.Equal(75, call.duty);
    }

    [Fact]
    public void SetFanSpeed_ClampsAndRebuildsMapOnColdCache()
    {
        var calls = new List<(string addr, string chan, int duty)>();
        // No GetFanChannels() first - Drive must lazily rebuild the control map.
        var p = new LinuxLiquidctlProvider(() => KrakenJson, (a, c, d) => { calls.Add((a, c, d)); return true; });
        p.SetFanSpeed("liquidctl:dev-hidraw3:pump", 250); // clamp to 100
        Assert.Equal(("/dev/hidraw3", "pump", 100), Assert.Single(calls));
    }

    [Fact]
    public void SetFanSpeed_RejectedWrite_DoesNotThrow()
    {
        // liquidctl returns non-zero -> seam returns false. Drive must warn-once
        // and not blow up (it still returns the requested duty per the interface).
        var p = new LinuxLiquidctlProvider(() => KrakenJson, (_, _, _) => false);
        Assert.Equal(50, p.SetFanSpeed("liquidctl:dev-hidraw3:pump", 50));
    }

    [Fact]
    public void TemperatureSources_AndReadByIdRoundTrip()
    {
        var p = new LinuxLiquidctlProvider(() => KrakenJson, (_, _, _) => true);
        var src = Assert.Single(p.GetTemperatureSources());
        // id keyed by sanitized label, not ordinal - stable across reorders
        Assert.Equal("liquidctl:dev-hidraw3:t:liquid-temperature", src.Id);
        Assert.Equal("Hub", src.Category);
        Assert.Equal(29.9f, p.ReadTemperature(src.Id) ?? -1, 1);
        Assert.Null(p.ReadTemperature("liquidctl:dev-hidraw3:t:nonexistent"));
    }

    [Fact]
    public void IsLiquidctlId_OnlyMatchesPrefix()
    {
        Assert.True(LinuxLiquidctlProvider.IsLiquidctlId("liquidctl:x:fan"));
        Assert.False(LinuxLiquidctlProvider.IsLiquidctlId("nvidia:0"));
        Assert.False(LinuxLiquidctlProvider.IsLiquidctlId(""));
    }

    [Fact]
    public void SetFanSpeed_PersistsManualOverride_DriveDoesNot()
    {
        var store = new InMemoryConfigStore();
        var p = new LinuxLiquidctlProvider(() => KrakenJson, (_, _, _) => true, store);

        p.SetFanSpeed("liquidctl:dev-hidraw3:pump", 70);
        Assert.Equal(70, store.Load().Cooling.ManualSpeeds["liquidctl:dev-hidraw3:pump"]);

        // Curve-engine path must not pollute the user-override dict.
        p.DriveFanSpeed("liquidctl:dev-hidraw3:pump", 33);
        Assert.Equal(70, store.Load().Cooling.ManualSpeeds["liquidctl:dev-hidraw3:pump"]);
    }

    [Fact]
    public void SetFanSpeed_RejectedWrite_DoesNotPersist()
    {
        var store = new InMemoryConfigStore();
        var p = new LinuxLiquidctlProvider(() => KrakenJson, (_, _, _) => false, store);
        p.SetFanSpeed("liquidctl:dev-hidraw3:pump", 70);
        Assert.Empty(store.Load().Cooling.ManualSpeeds);
    }

    [Fact]
    public void GetFanChannels_CurveBoundFan_ReportsCurveMode()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Cooling.Curves.Add(new Nexus.Service.Persistence.CurveDocument
        {
            Id = "c1",
            Outputs = { new Nexus.Service.Persistence.CurveOutputDocument { Id = "liquidctl:dev-hidraw3:pump" } },
        }));
        var p = new LinuxLiquidctlProvider(() => KrakenJson, (_, _, _) => true, store);
        Assert.Equal(FanModes.Curve, Assert.Single(p.GetFanChannels()).Mode);
    }
}
