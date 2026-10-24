using Nexus.Service.Diagnostics.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Gpu;

/// <summary>
/// GpuHealthMonitor now self-gates on Windows OR Linux (NVML is unavailable
/// on macOS, where this suite runs) - covers the "library/platform
/// unsupported" degrade path, which is the only part reachable without real
/// NVIDIA hardware. The Linux-specific "libnvidia-ml.so.1 present but load
/// fails" path can only be verified on a real Linux box.
/// </summary>
public class GpuHealthMonitorTests
{
    [NonWindowsFact]
    public void Snapshot_on_a_platform_without_nvml_never_throws_and_reports_unsupported()
    {
        var monitor = new GpuHealthMonitor();

        var snapshot = monitor.Snapshot();

        Assert.False(snapshot.Supported);
        Assert.Empty(snapshot.Gpus);
    }

    [Fact]
    public void MapReasonsToActiveKeys_maps_known_bits_and_folds_unknown_bits_into_other()
    {
        Assert.Empty(NvmlInterop.MapReasonsToActiveKeys(0));
        Assert.Equal(new[] { "swPower" }, NvmlInterop.MapReasonsToActiveKeys(NvmlInterop.ReasonSwPowerCap));
        Assert.Contains("other", NvmlInterop.MapReasonsToActiveKeys(0x100));
    }
}
