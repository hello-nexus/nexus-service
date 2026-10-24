#if MACOS
using System.Diagnostics;
using Nexus.Service.Platform.Mac;

namespace Nexus.Service.Tests;

public class MacGpuStatsTests
{
    [MacOnlyFact]
    public void TryReadDeviceUtilization_IsAPercentageWhereTheAcceleratorExposesIt()
    {
        var value = MacGpuStats.TryReadDeviceUtilization();
        Assert.True(value is null or (>= 0 and <= 100), $"unexpected utilization {value}");

        // A virtualized GPU may not publish the counter, so a reading is only required where the registry lists it.
        if (RegistryListsUtilization())
        {
            Assert.NotNull(value);
        }
    }

    private static bool RegistryListsUtilization()
    {
        using var ioreg = Process.Start(new ProcessStartInfo("/usr/sbin/ioreg", "-r -c IOAccelerator")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });
        var output = ioreg!.StandardOutput.ReadToEnd();
        ioreg.WaitForExit();
        return output.Contains("Device Utilization %", StringComparison.Ordinal);
    }
}
#endif
