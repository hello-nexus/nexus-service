#if MACOS
using Nexus.Service.Platform.Mac;

namespace Nexus.Service.Tests;

public class MacGpuStatsTests
{
    // A machine with no readable accelerator reports null; any reading is a percentage.
    [MacOnlyFact]
    public void TryReadDeviceUtilization_IsNullOrAPercentage()
    {
        var value = MacGpuStats.TryReadDeviceUtilization();
        Assert.True(value is null or (>= 0 and <= 100), $"unexpected utilization {value}");
    }
}
#endif
