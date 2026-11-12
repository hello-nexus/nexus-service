using Nexus.Service.Diagnostics.Alerts;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics.Alerts;

public sealed class CpuTemperatureDefaultTests
{
    [Theory]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", 95)]
    [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", 95)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", 89)]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", 89)]
    [InlineData("AMD Ryzen 5 7600X3D 6-Core Processor", 89)]
    [InlineData("AMD Ryzen 9 7950X 16-Core Processor", 95)]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", 90)]
    [InlineData("AMD Ryzen 9 5900X 12-Core Processor", 90)]
    [InlineData("AMD Ryzen 7 3700X 8-Core Processor", 95)]
    [InlineData("Intel(R) Core(TM) i9-14900K", 100)]
    [InlineData("Some Unknown CPU", 100)]
    [InlineData("Apple M2 Max", 100)]
    [InlineData(null, 100)]
    public void The_table_gives_the_published_maximum_by_model(string? model, double expected)
    {
        Assert.Equal(expected, CpuTemperatureDefault.Resolve(model, null));
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-1365U", 105, 100)]
    [InlineData("Intel(R) Core(TM) i7-1365U", 90, 90)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", 95, 95)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", 40, 89)]
    public void A_live_reading_wins_over_the_table_and_is_clamped(string model, double live, double expected)
    {
        Assert.Equal(expected, CpuTemperatureDefault.Resolve(model, live));
    }

    private static BuiltInDevice Cpu(double? max) => new("cpuTemp", "cpu-temp", "CPU", false, "", null, max);

    private static BuiltInDevice Igpu(double? max) => new("gpuTemp", "gpu-temp:g1", "iGPU", true, "g1", null, max);

    [Fact]
    public void A_new_cpu_entry_and_Restore_default_use_the_cpu_maximum()
    {
        Assert.Equal(89, BuiltInSensorRules.Defaults(Cpu(89)).High);
        Assert.Equal(100, BuiltInSensorRules.Defaults(Cpu(null)).High);
        Assert.Equal(100, BuiltInSensorRules.Defaults(Cpu(105)).High);
    }

    [Fact]
    public void An_integrated_gpu_follows_the_cpu_maximum_and_a_discrete_gpu_does_not()
    {
        Assert.Equal(89, BuiltInSensorRules.Defaults(Igpu(89)).High);
        var discrete = new BuiltInDevice("gpuTemp", "gpu-temp:g0", "RTX", false, "g0", null, 89);
        Assert.Equal(90, BuiltInSensorRules.Defaults(discrete).High);
    }

    [Fact]
    public void A_legacy_limit_at_the_old_default_moves_to_the_cpu_maximum_and_an_edited_one_is_kept()
    {
        var untouched = new DiagnosticsSettings();
        BuiltInSensorRules.Ensure(untouched, newInstall: false);
        Assert.Equal(89, BuiltInSensorRules.Seed(Cpu(89), untouched).High);
        Assert.Equal(89, BuiltInSensorRules.Seed(Igpu(89), untouched).High);

        var edited = new DiagnosticsSettings();
        edited.Thresholds.CpuC = 80;
        BuiltInSensorRules.Ensure(edited, newInstall: false);
        Assert.Equal(80, BuiltInSensorRules.Seed(Cpu(89), edited).High);
    }
}
