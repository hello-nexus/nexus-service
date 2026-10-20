using Nexus.Service.Devices.Firmware;
using Xunit;

namespace Nexus.Service.Tests;

public class DfuUtilTests
{
    [Fact]
    public void DownloadArgs_targets_the_app_base_with_leave()
    {
        var args = DfuUtil.DownloadArgs("fw.bin", DfuUtil.AppBaseAddress, leave: true);
        Assert.Equal(new[] { "-d", "3402:0a00", "-a", "0", "-s", "0x800C000:leave", "-D", "fw.bin" }, args);
    }

    [Fact]
    public void DownloadArgs_without_leave_omits_the_suffix()
    {
        var args = DfuUtil.DownloadArgs("flag.bin", DfuUtil.BootFlagAddress, leave: false);
        Assert.Equal(new[] { "-d", "3402:0a00", "-a", "0", "-s", "0x801FFF0", "-D", "flag.bin" }, args);
    }

    [Fact]
    public void UploadArgs_requests_a_fixed_length_readback()
    {
        var args = DfuUtil.UploadArgs("readback.bin", DfuUtil.AppBaseAddress, 54420);
        Assert.Equal(new[] { "-d", "3402:0a00", "-a", "0", "-s", "0x800C000:54420", "-U", "readback.bin" }, args);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("No DFU capable USB device available", 0)]
    [InlineData("Found DFU: [3402:0a00] ver=0100, devnum=5, cfg=1, intf=0, path=\"1-2\", alt=0, name=\"@Internal Flash\"", 1)]
    public void CountDfuDevices_counts_matching_vidpid_lines(string output, int expected)
    {
        Assert.Equal(expected, DfuUtil.CountDfuDevices(output));
    }

    [Fact]
    public void ResolveDefaultPath_prefers_the_bundled_binary_and_falls_back_to_path()
    {
        var path = DfuUtil.ResolveDefaultPath();
        var bundledName = OperatingSystem.IsWindows() ? "dfu-util.exe" : "dfu-util";
        var bundled = Path.Combine(AppContext.BaseDirectory, "tools", "dfu-util", bundledName);

        if (OperatingSystem.IsWindows() || File.Exists(bundled))
            Assert.Equal(bundled, path);
        else
            Assert.Equal("dfu-util", path);
    }
}
