using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.DependencyInjection;
using Nexus.Service.Devices;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.LianLiFlex;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexCompositeTests
{
    [Fact]
    public void The_composite_lists_and_routes_the_Flex_zone_devices()
    {
        var discovery = new FakeFlexDiscovery();
        discovery.Ports.Add(FakeFlexDiscovery.Tl());
        var hub = new FlexHub(discovery, _ => new FakeFlexTransport("6D1007F517E1"), new FlexClock().Func, null, _ => { });
        hub.Refresh();

        var services = new ServiceCollection();
        services.AddNexusCore().AddNexusSensors().AddNexusCooling().AddNexusBenchmarks().AddNexusLighting().AddNexusDevices()
            .AddNexusPeripherals().AddNexusActivity().AddNexusNetwork().AddNexusLifecycle().AddNexusWeather()
            .AddNexusPanel(servicePort: 9400).AddNexusLinuxDBus().AddNexusHelper();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(hub);
        var sp = services.BuildServiceProvider();

        var composite = sp.GetRequiredService<ILightingDeviceProvider>();
        var devices = composite.GetAll().Devices;
        Assert.Contains(devices, d => d.Id.StartsWith("lianli-flex:", System.StringComparison.Ordinal));
        Assert.All(devices.Where(d => d.Id.StartsWith("lianli-flex:", System.StringComparison.Ordinal)),
            d => Assert.Equal("lianli-flex", d.ControlHandlerId));
        Assert.True(CompositeLightingDeviceProvider.IsFlexId(FlexLightingDeviceProvider.ZoneIdFor("6D1007F517E1", "up")));
        Assert.False(CompositeLightingDeviceProvider.IsFlexId("lianli-wireless:x"));
    }
}
