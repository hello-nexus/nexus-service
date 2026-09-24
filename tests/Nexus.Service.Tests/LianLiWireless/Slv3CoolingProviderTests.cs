using System;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3CoolingProviderTests
{
    private const string Mac = "112233445566";

    [Fact]
    public void IsSlv3Id_matches_only_the_wireless_fan_channel_prefix()
    {
        Assert.True(Slv3CoolingProvider.IsSlv3Id($"lianli-wireless:{Mac}:port0"));
        Assert.False(Slv3CoolingProvider.IsSlv3Id("lianli:port0"));
        Assert.False(Slv3CoolingProvider.IsSlv3Id(""));
    }

    [Fact]
    public void GetFanChannels_and_GetAll_empty_when_hub_disconnected()
    {
        var hub = new Slv3Hub(new Slv3TestHub.FakeDiscovery(), _ => throw new InvalidOperationException());
        var provider = new Slv3CoolingProvider(hub);

        Assert.Empty(provider.GetFanChannels());
        Assert.Empty(provider.GetAll());
    }

    [Fact]
    public void GetFanChannels_surfaces_one_channel_per_occupied_port_with_telemetry_driven_mode()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        hub.State.Fans = new[]
        {
            new Slv3FanInfo
            {
                Mac = Mac,
                BoundToUs = true,
                FanCount = 2,
                Pwm = new[] { Slv3Protocol.PwmFollowMotherboard, 40, 0, 0 },
                Rpm = new[] { 0, 1200, 0, 0 },
            },
        };
        hub.State.MotherboardPwmPercent = 35;
        var provider = new Slv3CoolingProvider(hub);

        var channels = provider.GetFanChannels();

        Assert.Equal(2, channels.Count);
        Assert.Equal($"lianli-wireless:{Mac}:port0", channels[0].Id);
        Assert.Equal(FanModes.Auto, channels[0].Mode);       // wire byte 6 -> mobo-sync
        Assert.Equal(35, channels[0].DutyPercent);           // shows the header duty the RX measured
        Assert.Equal($"lianli-wireless:{Mac}:port1", channels[1].Id);
        Assert.Equal(FanModes.Manual, channels[1].Mode);
        Assert.Equal(16, channels[1].DutyPercent);           // wire byte 40 on the 0..255 scale
        Assert.Equal(1200, channels[1].Rpm);
        Assert.Equal(Slv3Protocol.MinDutyPercent, channels[1].MinDuty);
        Assert.All(channels, c => Assert.Equal($"lianli-wireless:{Mac}", c.DeviceId));
    }

    [Fact]
    public void GetFanChannels_MinDuty_follows_the_fan_family()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        hub.State.Fans = new[]
        {
            new Slv3FanInfo { Mac = Mac, BoundToUs = true, FanCount = 1, FanType = 37 }, // SL-Infinity
            new Slv3FanInfo { Mac = "AABBCCDDEEFF", BoundToUs = true, FanCount = 1, FanType = 24 }, // SLV3-LCD
        };
        var provider = new Slv3CoolingProvider(hub);

        var channels = provider.GetFanChannels();

        Assert.Equal(11, channels[0].MinDuty);
        Assert.Equal(14, channels[1].MinDuty);
    }

    [Fact]
    public void GetFanChannels_skips_unbound_but_exposes_a_bound_zero_count_chain_as_rpm_unavailable()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        hub.State.Fans = new[]
        {
            new Slv3FanInfo { Mac = Mac, BoundToUs = false, FanCount = 2 },
            new Slv3FanInfo { Mac = "AABBCCDDEEFF", BoundToUs = true, FanCount = 0 },
        };
        var provider = new Slv3CoolingProvider(hub);

        var channels = provider.GetFanChannels();
        // Unbound chain skipped; the bound chain that reports no fan count still
        // exposes the controller's physical ports so the user can drive them.
        Assert.Equal(Slv3Protocol.PortsPerRecord, channels.Count);
        Assert.All(channels, c => Assert.StartsWith("lianli-wireless:AABBCCDDEEFF:port", c.Id));
        Assert.All(channels, c => Assert.True(c.RpmUnavailable));

        var component = Assert.Single(provider.GetAll());
        Assert.Equal(Slv3Protocol.PortsPerRecord, component.Devices.Count);
        Assert.All(component.Devices, d => Assert.Null(d.Rpm));
    }

    [Fact]
    public void A_bound_strimer_gets_no_cooling_channels()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        hub.State.Fans = new[]
        {
            // The Y70 record: dev_type 2 (24-pin Strimer), no fans.
            new Slv3FanInfo { Mac = Mac, BoundToUs = true, DevType = 2, FanCount = 0 },
            new Slv3FanInfo { Mac = "AABBCCDDEEFF", BoundToUs = true, DevType = 0, FanType = 24, FanCount = 1 },
        };
        var provider = new Slv3CoolingProvider(hub);

        var channel = Assert.Single(provider.GetFanChannels());
        Assert.StartsWith("lianli-wireless:AABBCCDDEEFF:port", channel.Id);
        var component = Assert.Single(provider.GetAll());
        Assert.Equal("lianli-wireless:AABBCCDDEEFF", component.Id);
    }

    [Fact]
    public void GetAll_groups_ports_under_one_component_per_chain()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        hub.State.Fans = new[]
        {
            new Slv3FanInfo
            {
                Mac = Mac,
                BoundToUs = true,
                FanCount = 3,
                Pwm = new[] { 50, Slv3Protocol.PwmFollowMotherboard, Slv3Protocol.PwmFollowMotherboard, 0 },
                Rpm = new[] { 900, 0, 0, 0 },
            },
        };
        var provider = new Slv3CoolingProvider(hub);

        var component = Assert.Single(provider.GetAll());
        Assert.Equal($"lianli-wireless:{Mac}", component.Id);
        Assert.Equal(3, component.Devices.Count);
        Assert.Equal(900, component.Devices[0].Rpm);
        Assert.Equal(20, component.Devices[0].Pwm);          // wire byte 50 -> 20 %
    }

    [Fact]
    public void SetFanSpeed_and_ReleaseFan_drive_the_hub_by_mac_and_port()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        hub.State.Fans = new[] { new Slv3FanInfo { Mac = Mac, BoundToUs = true, FanCount = 2 } };
        var provider = new Slv3CoolingProvider(hub);

        var applied = provider.SetFanSpeed($"lianli-wireless:{Mac}:port0", 45);

        Assert.Equal(45, applied);
        Assert.Equal(45, hub.GetPortDuty(Mac, 0));

        provider.ReleaseFan($"lianli-wireless:{Mac}:port0");

        Assert.Null(hub.GetPortDuty(Mac, 0));
    }

    [Fact]
    public void ReleaseAll_clears_every_bound_port_back_to_mobo_sync()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        hub.State.Fans = new[] { new Slv3FanInfo { Mac = Mac, BoundToUs = true, FanCount = 2 } };
        var provider = new Slv3CoolingProvider(hub);
        provider.SetFanSpeed($"lianli-wireless:{Mac}:port0", 60);
        provider.SetFanSpeed($"lianli-wireless:{Mac}:port1", 70);

        provider.ReleaseAll();

        Assert.Null(hub.GetPortDuty(Mac, 0));
        Assert.Null(hub.GetPortDuty(Mac, 1));
    }

    [Fact]
    public void ReleaseAll_clears_a_bound_zero_count_chain_whose_ports_were_driven()
    {
        var (hub, _, _) = Slv3TestHub.CreateConnected();
        // Controller reports no fan count but the ports are still exposed and
        // drivable; ReleaseAll must clear them, not skip the chain.
        hub.State.Fans = new[] { new Slv3FanInfo { Mac = Mac, BoundToUs = true, FanCount = 0 } };
        var provider = new Slv3CoolingProvider(hub);
        provider.SetFanSpeed($"lianli-wireless:{Mac}:port0", 50);
        Assert.Equal(50, hub.GetPortDuty(Mac, 0));

        provider.ReleaseAll();

        Assert.Null(hub.GetPortDuty(Mac, 0));
    }

    // Persisted-manual-duty restore is covered by CurveEngineTests: bound
    // chains surface in GetFanChannels and the engine's presence-gated
    // replay applies the saved duty through SetFanSpeed.
}
