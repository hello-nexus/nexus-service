using Nexus.Service.Devices;
using Xunit;

namespace Nexus.Service.Tests;

public class DeviceControlGateTests
{
    [Fact]
    public void IsEnabled_DefaultsToTrue()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());

        Assert.True(gate.IsEnabled("cnvs"));
    }

    [Fact]
    public void SetEnabled_False_PersistsToDisabledList()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);

        gate.SetEnabled("cnvs", false);

        Assert.False(gate.IsEnabled("cnvs"));
        Assert.Contains("cnvs", store.Load().Devices.NexusControlDisabled);
    }

    [Fact]
    public void SetEnabled_True_RemovesFromDisabledList()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        gate.SetEnabled("cnvs", false);

        gate.SetEnabled("cnvs", true);

        Assert.True(gate.IsEnabled("cnvs"));
        Assert.DoesNotContain("cnvs", store.Load().Devices.NexusControlDisabled);
    }

    [Theory]
    [InlineData("CNVS")]
    [InlineData("Cnvs")]
    [InlineData("cnvs")]
    public void IsEnabled_IsCaseInsensitive(string lookupId)
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        gate.SetEnabled("cnvs", false);

        Assert.False(gate.IsEnabled(lookupId));
    }

    [Fact]
    public void SetEnabled_DoesNotDuplicateEntries()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);

        gate.SetEnabled("cnvs", false);
        gate.SetEnabled("CNVS", false);

        Assert.Single(store.Load().Devices.NexusControlDisabled);
    }

    // Nothing turns these on but the user's own toggle, running app or not.
    [Theory]
    [InlineData("lianli-wireless")]
    [InlineData("lianli")]
    [InlineData("corsair")]
    public void IsEnabled_ThirdPartyHandler_DefaultsToFalse(string handlerId)
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());

        Assert.False(gate.IsEnabled(handlerId));
    }

    [Fact]
    public void IsEnabled_HyteHandler_DefaultsToTrue()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());

        Assert.True(gate.IsEnabled("qseries"));
    }

    [Fact]
    public void SetEnabled_True_OnThirdPartyHandler_OverridesDefaultAndPersists()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);

        gate.SetEnabled("lianli-wireless", true);

        Assert.True(gate.IsEnabled("lianli-wireless"));
        Assert.Contains("lianli-wireless", store.Load().Devices.NexusControlEnabled);
    }

    [Fact]
    public void SetEnabled_False_OnThirdPartyHandler_Persists()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);
        gate.SetEnabled("lianli-wireless", true);

        gate.SetEnabled("lianli-wireless", false);

        Assert.False(gate.IsEnabled("lianli-wireless"));
        Assert.DoesNotContain("lianli-wireless", store.Load().Devices.NexusControlEnabled);
        Assert.Contains("lianli-wireless", store.Load().Devices.NexusControlDisabled);
    }

    [Fact]
    public void SetEnabled_TogglingHyteHandlerOffThenOn_ReturnsToDefaultOn()
    {
        var store = new InMemoryConfigStore();
        var gate = new DeviceControlGate(store);

        gate.SetEnabled("qseries", false);
        Assert.False(gate.IsEnabled("qseries"));

        gate.SetEnabled("qseries", true);

        Assert.True(gate.IsEnabled("qseries"));
        Assert.DoesNotContain("qseries", store.Load().Devices.NexusControlDisabled);
    }

    // Streamed-panel handler ids reach the gate but are not IDeviceHandlers, so
    // they have no on/off row. They must default on or they strand with no way
    // to re-enable them.
    [Fact]
    public void IsEnabled_StreamedPanelHandler_DefaultsToTrue()
    {
        var gate = new DeviceControlGate(new InMemoryConfigStore());

        Assert.True(gate.IsEnabled("artinchip-d213"));
    }
}
