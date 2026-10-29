using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Plugins;

namespace Nexus.Service.Tests;

public class DeviceListWriteHealthTests
{
    [Fact]
    public void A_failing_handler_key_marks_its_card_and_leaves_the_others_unmarked()
    {
        var health = new LightingWriteHealth();
        for (var i = 0; i < LightingWriteHealth.MinFailures; i++)
        {
            health.NoteFailure("cnvs:SN1");
        }
        var manager = new DeviceManager(
            new IDeviceHandler[] { TestHandlers.Cnvs(), TestHandlers.FanHub() },
            new StubUsbEnumerator(),
            new PluginProviderRegistry(),
            new DeviceControlGate(new InMemoryConfigStore()),
            health);

        var items = manager.GetAll();

        Assert.Equal(LightingWriteHealth.Failing, items.Single(i => i.Id == "cnvs").WriteHealth);
        Assert.Null(items.Single(i => i.Id == "fan-hub").WriteHealth);
    }
}
