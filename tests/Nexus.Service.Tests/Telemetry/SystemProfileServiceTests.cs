using Nexus.Service.Devices.Handlers;
using Nexus.Service.Models.Devices;
using Nexus.Service.Telemetry;
using Nexus.Service.Tests.Cloud;
using Xunit;

namespace Nexus.Service.Tests.Telemetry;

public class SystemProfileServiceTests
{
    [Fact]
    public void LatchY70Seen_sets_the_flag_when_the_y70_is_connected()
    {
        var store = new InMemoryConfigStore();

        SystemProfileService.LatchY70Seen(store, new[] { new DeviceListItem { Id = Y70Handler.HandlerId, Connected = true } });

        Assert.True(store.Load().Telemetry.FleetY70Seen);
    }

    [Fact]
    public void LatchY70Seen_ignores_other_devices()
    {
        var store = new InMemoryConfigStore();

        SystemProfileService.LatchY70Seen(store, new[] { new DeviceListItem { Id = "qseries", Connected = true } });

        Assert.False(store.Load().Telemetry.FleetY70Seen);
    }

    [Fact]
    public void LatchY70Seen_never_clears_a_set_flag()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Telemetry.FleetY70Seen = true);

        SystemProfileService.LatchY70Seen(store, System.Array.Empty<DeviceListItem>());

        Assert.True(store.Load().Telemetry.FleetY70Seen);
    }
}
