using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class LhmComponentIdentifiersTests
{
    [Theory]
    [InlineData("/memory/dimm/0", true)]
    [InlineData("/memory/dimm/1", true)]
    [InlineData("/memory/dimm0", true)]
    [InlineData("/memory/dimm", true)]
    [InlineData("/ram", false)]
    [InlineData("/vram", false)]
    [InlineData("/memory/other/0", false)]
    [InlineData("", false)]
    public void IsDimmModule_matches_only_dimm_nodes(string identifier, bool expected)
    {
        Assert.Equal(expected, LhmComponentIdentifiers.IsDimmModule(identifier));
    }

    [Theory]
    [InlineData("/gpu-powermonitor/COM3", true)]
    [InlineData("/gpu-powermonitor/COM3/voltage/0", true)]
    [InlineData("/gpu-nvidia/0", false)]
    [InlineData("/psu/0", false)]
    [InlineData("", false)]
    public void IsPowerMonitor_matches_only_power_monitor_nodes(string identifier, bool expected)
    {
        Assert.Equal(expected, LhmComponentIdentifiers.IsPowerMonitor(identifier));
    }

    [Theory]
    [InlineData(true, false, 0f, true)]
    [InlineData(false, true, -100f, true)]
    [InlineData(false, true, -100.5f, true)]
    [InlineData(false, true, -99.9f, false)]
    [InlineData(false, true, 41.5f, false)]
    [InlineData(false, true, null, true)]
    [InlineData(false, false, null, true)]
    [InlineData(false, false, -100f, false)]
    public void HidePowerMonitorSensor_drops_control_and_absent_probe_only(bool isControl, bool isTemperature, float? value, bool expected)
    {
        Assert.Equal(expected, LhmComponentIdentifiers.HidePowerMonitorSensor(isControl, isTemperature, value));
    }

    [Theory]
    [InlineData("/nvme/0", "smart/nvme/0")]
    [InlineData("/hdd/1", "smart/hdd/1")]
    public void BuildSmartStorageId_prefixes_and_strips_the_leading_slash(string identifier, string expected)
    {
        Assert.Equal(expected, LhmComponentIdentifiers.BuildSmartStorageId(identifier));
    }

    [Theory]
    [InlineData("smart/nvme/0", true)]
    [InlineData("smart/hdd/1", true)]
    [InlineData("C:", false)]
    [InlineData("D:", false)]
    [InlineData("", false)]
    public void IsSmartStorageComponent_matches_only_smart_prefixed_ids(string componentId, bool expected)
    {
        Assert.Equal(expected, LhmComponentIdentifiers.IsSmartStorageComponent(componentId));
    }
}
