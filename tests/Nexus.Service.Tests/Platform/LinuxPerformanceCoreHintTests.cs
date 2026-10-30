using System.Collections.Generic;
using Nexus.Service.Platform.Linux;
using Xunit;

namespace Nexus.Service.Tests.Platform;

public class LinuxPerformanceCoreHintTests
{
    private static string? Read(Dictionary<string, string> files, string path) =>
        files.TryGetValue(path, out var v) ? v : null;

    [Theory]
    [InlineData("0-3", new[] { 0, 1, 2, 3 })]
    [InlineData("0-1,4,6-7", new[] { 0, 1, 4, 6, 7 })]
    [InlineData("5", new[] { 5 })]
    [InlineData("", new int[0])]
    [InlineData("x-3,2-1", new int[0])]
    public void ParseCpuList_ParsesKernelLists(string text, int[] expected) =>
        Assert.Equal(expected, LinuxPerformanceCoreHint.ParseCpuList(text));

    [Fact]
    public void IntelHybrid_UsesCpuCoreList()
    {
        var files = new Dictionary<string, string>
        {
            ["/sys/devices/cpu_core/cpus"] = "0-15",
            ["/sys/devices/cpu_atom/cpus"] = "16-19",
        };
        Assert.Equal(16, LinuxPerformanceCoreHint.SelectPerformanceCpus(p => Read(files, p), new[] { 0 }).Length);
    }

    [Fact]
    public void IntelUniform_WithoutAtomList_IsNoOp()
    {
        var files = new Dictionary<string, string> { ["/sys/devices/cpu_core/cpus"] = "0-7" };
        Assert.Empty(LinuxPerformanceCoreHint.SelectPerformanceCpus(p => Read(files, p), new[] { 0, 1 }));
    }

    [Fact]
    public void CpuCapacity_PicksHighestCapacityCpus()
    {
        var files = new Dictionary<string, string>
        {
            ["/sys/devices/system/cpu/cpu0/cpu_capacity"] = "512",
            ["/sys/devices/system/cpu/cpu1/cpu_capacity"] = "512",
            ["/sys/devices/system/cpu/cpu2/cpu_capacity"] = "1024",
            ["/sys/devices/system/cpu/cpu3/cpu_capacity"] = "1024",
        };
        Assert.Equal(new[] { 2, 3 }, LinuxPerformanceCoreHint.SelectPerformanceCpus(p => Read(files, p), new[] { 0, 1, 2, 3 }));
    }

    [Fact]
    public void UniformCapacity_IsNoOp()
    {
        var files = new Dictionary<string, string>
        {
            ["/sys/devices/system/cpu/cpu0/cpu_capacity"] = "1024",
            ["/sys/devices/system/cpu/cpu1/cpu_capacity"] = "1024",
        };
        Assert.Empty(LinuxPerformanceCoreHint.SelectPerformanceCpus(p => Read(files, p), new[] { 0, 1 }));
    }

    [Fact]
    public void NoSysfs_IsNoOp() =>
        Assert.Empty(LinuxPerformanceCoreHint.SelectPerformanceCpus(_ => null, new[] { 0, 1 }));
}
