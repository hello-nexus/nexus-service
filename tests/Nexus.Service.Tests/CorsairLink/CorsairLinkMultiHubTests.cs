using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Mappings;
using Nexus.Service.Peripherals.CorsairLink;
using Xunit;

namespace Nexus.Service.Tests.CorsairLink;

public class CorsairLinkMultiHubTests
{
    private const string SecondPrefix = "corsair:B728C6AA:";

    private static CorsairLinkHub HubWithFan(string prefix, int number)
    {
        var hub = new CorsairLinkHub(prefix, number);
        hub.State.IsConnected = true;
        hub.State.Devices = new[]
        {
            new CorsairLinkDevice
            {
                Channel = 1, Type = 1, Name = "iCUE LINK QX RGB", Class = CorsairLinkClass.Fan,
                LedCount = 34, HasSpeed = true, HasTemperature = true, Rpm = 1200, TempC = 27f,
            },
        };
        return hub;
    }

    private static CorsairLinkHubs TwoHubs()
    {
        var hubs = new CorsairLinkHubs();
        hubs.Add(HubWithFan(SecondPrefix, 2));
        hubs.Add(HubWithFan("corsair:", 1));
        return hubs;
    }

    [Theory]
    [InlineData("corsair:ch3", "corsair:", 3)]
    [InlineData("corsair:ch3:temp", "corsair:", 3)]
    [InlineData("corsair:B728C6AA:ch13", SecondPrefix, 13)]
    [InlineData("corsair:B728C6AA:ch13:temp", SecondPrefix, 13)]
    public void Channel_ids_parse_to_their_hub_prefix_and_channel(string id, string prefix, int channel)
    {
        Assert.True(CorsairLinkHubs.TryParseChannelId(id, out var p, out var ch));
        Assert.Equal(prefix, p);
        Assert.Equal(channel, ch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("corsair")]
    [InlineData("corsair:ch0")]
    [InlineData("lianli:ch1")]
    public void Malformed_channel_ids_do_not_parse(string id)
    {
        Assert.False(CorsairLinkHubs.TryParseChannelId(id, out _, out _));
    }

    [Fact]
    public void Hubs_are_kept_in_display_number_order_and_resolve_their_own_ids()
    {
        var hubs = TwoHubs();

        Assert.Equal(new[] { 1, 2 }, hubs.All.Select(h => h.Number));
        Assert.True(hubs.TryResolve("corsair:B728C6AA:ch1:temp", out var hub, out var ch));
        Assert.Equal(2, hub.Number);
        Assert.Equal(1, ch);
        Assert.Same(hubs.All[1], hubs.Find("corsair:B728C6AA"));
        Assert.Same(hubs.All[0], hubs.Find("corsair"));
    }

    [Theory]
    [InlineData(null, "B|A", false, "A")]
    [InlineData("B", "A|B", false, "B")]
    [InlineData("OLD", "NEW", true, "NEW")]
    [InlineData("OLD", "A|B", true, "OLD")]
    public void Primary_hub_is_the_recorded_one_else_the_lowest_or_an_adopted_sole_replacement(
        string? stored, string present, bool adoptSole, string expected)
    {
        Assert.Equal(expected, CorsairLinkConnectionWorker.PickPrimaryKey(stored, present.Split('|'), adoptSole));
    }

    [Fact]
    public void A_sole_hub_waits_instead_of_claiming_a_missing_hubs_ids()
    {
        // At boot a second hub can enumerate before the recorded one; it must not take its ids.
        Assert.Null(CorsairLinkConnectionWorker.PickPrimaryKey("OLD", new[] { "NEW" }, adoptSole: false));
    }

    [Fact]
    public void Short_key_takes_the_serial_tail_and_hashes_a_path()
    {
        Assert.Equal("B728C6AA", CorsairLinkConnectionWorker.ShortKey("3E18C4D9C4C0F15880147F68B728C6AA", isSerial: true));
        // A macOS hidapi path has no slash; without a serial it is still hashed, not truncated.
        var path = "DevSrvsID:4295012345";
        var hashed = CorsairLinkConnectionWorker.ShortKey(path, isSerial: false);
        Assert.Equal(8, hashed.Length);
        Assert.NotEqual("95012345", hashed);
        Assert.Equal(hashed, CorsairLinkConnectionWorker.ShortKey(path, isSerial: false));
    }

    [Fact]
    public void Cooling_lists_each_hubs_channels_under_their_own_ids()
    {
        var cooling = new CorsairLinkCoolingProvider(TwoHubs());

        var channels = cooling.GetFanChannels();

        Assert.Equal(new[] { "corsair:ch1", "corsair:B728C6AA:ch1" }, channels.Select(c => c.Id));
        Assert.Equal(new[] { "Port 1", "Hub 2 Port 1" }, channels.Select(c => c.PortLabel));
        Assert.Equal(new[] { "corsair", "corsair:B728C6AA" }, cooling.GetAll().Select(c => c.Id));
        Assert.Equal("iCUE LINK QX RGB probe (Hub 2 Port 1)", cooling.GetTemperatureSources()[1].Name);
        Assert.Equal(27f, cooling.ReadTemperature("corsair:B728C6AA:ch1:temp"));
    }

    [Fact]
    public void A_duty_written_to_one_hub_leaves_the_other_hubs_channel_alone()
    {
        var cooling = new CorsairLinkCoolingProvider(TwoHubs());

        cooling.SetFanSpeed("corsair:B728C6AA:ch1", 35);

        var channels = cooling.GetFanChannels();
        Assert.Equal(35, channels.Single(c => c.Id == "corsair:B728C6AA:ch1").DutyPercent);
        Assert.NotEqual(35, channels.Single(c => c.Id == "corsair:ch1").DutyPercent);
    }

    [Fact]
    public void Lighting_names_devices_by_port_and_keeps_the_first_hubs_ids()
    {
        var hubs = TwoHubs();

        var first = CorsairLinkLightingDeviceProvider.BuildStructure(hubs.All[0], hubs.All[0].State.Devices[0]);
        var second = CorsairLinkLightingDeviceProvider.BuildStructure(hubs.All[1], hubs.All[1].State.Devices[0]);

        Assert.Equal("corsair:ch1", first.DeviceId);
        Assert.Equal("iCUE LINK QX RGB (Port 1)", first.Name);
        Assert.Equal(DeviceKeyComputer.ForFirstParty(CorsairLinkProtocol.VendorId, CorsairLinkProtocol.ProductId, "ch1"), first.DeviceKey);
        Assert.Equal("corsair:B728C6AA:ch1", second.DeviceId);
        Assert.Equal("iCUE LINK QX RGB (Hub 2 Port 1)", second.Name);
        Assert.NotEqual(first.DeviceKey, second.DeviceKey);
    }

    [Fact]
    public void Releasing_a_fan_while_its_hub_is_away_drops_the_manual_duty()
    {
        var hubs = TwoHubs();
        var cooling = new CorsairLinkCoolingProvider(hubs);
        cooling.SetFanSpeed("corsair:B728C6AA:ch1", 35);
        var second = hubs.All[1];

        hubs.Remove(second);
        cooling.ReleaseFan("corsair:B728C6AA:ch1");
        hubs.Add(second);

        Assert.NotEqual(35, cooling.GetFanChannels().Single(c => c.Id == "corsair:B728C6AA:ch1").DutyPercent);
    }
}
