using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Monitoring;
using Nexus.Service.Peripherals.CorsairLink;

namespace Nexus.Service.Tests.CorsairLink;

// The QX fan probes reach the central sensor list through GetTemperatureSources
// -> HubCoolerSensors; these pin the ids, the QX-only filter and the disconnect path.
public class CorsairLinkTemperatureSourcesTests
{
    private static CorsairLinkDevice Qx(int channel, float temp) => new()
    {
        Channel = channel, Type = 1, Name = "iCUE LINK QX RGB", Class = CorsairLinkClass.Fan,
        HasSpeed = true, HasTemperature = true, TempC = temp,
    };

    private static CorsairLinkDevice Rx(int channel) => new()
    {
        Channel = channel, Type = 15, Name = "iCUE LINK RX RGB", Class = CorsairLinkClass.Fan, HasSpeed = true,
    };

    private static (CorsairLinkCoolingProvider Provider, CorsairLinkHub Hub) Build(params CorsairLinkDevice[] devices)
    {
        var hub = new CorsairLinkHub(modeCycleWaitMs: 0);
        hub.State.IsConnected = true;
        hub.State.Devices = devices;
        var hubs = new CorsairLinkHubs();
        hubs.Add(hub);
        return (new CorsairLinkCoolingProvider(hubs), hub);
    }

    [Fact]
    public void Only_ports_with_a_probe_produce_sources_and_ids_follow_the_port()
    {
        var (provider, _) = Build(Qx(1, 18.9f), Rx(2), Qx(3, 24f));
        var sources = provider.GetTemperatureSources();
        Assert.Equal(new[] { "corsair:ch1:temp", "corsair:ch3:temp" }, sources.Select(s => s.Id));
        Assert.Equal(new[] { "iCUE LINK QX RGB probe (Port 1)", "iCUE LINK QX RGB probe (Port 3)" }, sources.Select(s => s.Name));
    }

    [Fact]
    public void Ids_are_identical_after_a_reconnect()
    {
        var (provider, hub) = Build(Qx(1, 20f));
        var before = provider.GetTemperatureSources().Select(s => s.Id).ToList();
        hub.State.IsConnected = false;
        hub.State.IsConnected = true;
        Assert.Equal(before, provider.GetTemperatureSources().Select(s => s.Id));
    }

    [Fact]
    public void A_disconnected_hub_lists_no_sources_and_reads_null_without_throwing()
    {
        var (provider, hub) = Build(Qx(1, 20f));
        hub.State.IsConnected = false;
        Assert.Empty(provider.GetTemperatureSources());
        Assert.Null(provider.ReadTemperature("corsair:ch1:temp"));
    }

    [Fact]
    public void A_removed_fan_or_unknown_hub_reads_null()
    {
        var (provider, _) = Build(Qx(1, 20f));
        Assert.Null(provider.ReadTemperature("corsair:ch4:temp"));
        Assert.Null(provider.ReadTemperature("corsair:DEADBEEF:ch1:temp"));
    }

    [Fact]
    public void Probes_land_in_one_hub_component_with_the_curve_source_ids()
    {
        var (provider, _) = Build(Qx(1, 20f), Qx(2, 21f));
        var component = Assert.Single(HubCoolerSensors.Build(provider.GetTemperatureSources()));
        Assert.Equal("corsair", component.Id);
        Assert.Equal(new[] { "corsair:ch1:temp", "corsair:ch2:temp" }, component.Sensors.Select(s => s.Id));
    }
}
