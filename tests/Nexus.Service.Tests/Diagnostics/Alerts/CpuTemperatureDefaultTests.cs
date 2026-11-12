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
    [InlineData("AMD Ryzen 5 7600X3D 6-Core Processor", 95)]
    [InlineData("AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", 100)]
    [InlineData("AMD Ryzen 9 7945HX3D 16-Core Processor", 100)]
    [InlineData("AMD Ryzen 7 5800H with Radeon Graphics", 100)]
    [InlineData("AMD Ryzen 5 7535U with Radeon Graphics", 100)]
    [InlineData("AMD Ryzen AI 9 HX 370 w/ Radeon 890M", 100)]
    [InlineData("AMD Ryzen 7 260 w/ Radeon 780M Graphics", 100)]
    [InlineData("AMD Ryzen 5 PRO 230 w/ Radeon 760M Graphics", 100)]
    [InlineData("AMD Ryzen Z1 Extreme", 100)]
    [InlineData("AMD Ryzen Z2 Extreme", 100)]
    [InlineData("AMD Ryzen Z2 Go", 100)]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor", 95)]
    [InlineData("AMD Ryzen 7 5700G with Radeon Graphics", 95)]
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
        var max = CpuTemperatureDefault.Resolve(model, null);
        Assert.False(max.FromHardware);
        Assert.Equal(expected, CpuTemperatureDefault.LimitFromMax(max.MaxC));
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-1365U", 105, 100, true)]
    [InlineData("Intel(R) Core(TM) i7-1365U", 90, 90, true)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", 95, 95, true)]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", 40, 89, false)]
    public void A_live_reading_wins_over_the_table_and_is_clamped(string model, double live, double expected, bool fromHardware)
    {
        var max = CpuTemperatureDefault.Resolve(model, live);
        Assert.Equal(fromHardware, max.FromHardware);
        Assert.Equal(expected, CpuTemperatureDefault.LimitFromMax(max.MaxC));
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

        // A user-chosen limit is kept even when it equals the fallback default.
        var chosen100 = new DiagnosticsSettings();
        chosen100.Thresholds.CpuC = 100;
        BuiltInSensorRules.Ensure(chosen100, newInstall: false);
        Assert.Equal(100, BuiltInSensorRules.Seed(Cpu(89), chosen100).High);
    }

    [Fact]
    public void An_untouched_default_follows_a_later_reading_or_cpu_change_once_and_an_edited_limit_does_not()
    {
        var rule = BuiltInSensorRules.Defaults(Cpu(95));
        Assert.False(BuiltInSensorRules.CpuDefaultStale(rule, Cpu(95)));

        Assert.True(BuiltInSensorRules.CpuDefaultStale(rule, Cpu(89)));
        BuiltInSensorRules.RefreshCpuDefault(rule, Cpu(89));
        Assert.Equal(89, rule.High);
        Assert.False(BuiltInSensorRules.CpuDefaultStale(rule, Cpu(89)));

        var igpu = BuiltInSensorRules.Defaults(Igpu(95));
        Assert.True(BuiltInSensorRules.CpuDefaultStale(igpu, Igpu(89)));

        var edited = BuiltInSensorRules.Defaults(Cpu(95));
        edited.High = 92;
        Assert.False(BuiltInSensorRules.CpuDefaultStale(edited, Cpu(89)));

        var discrete = new BuiltInDevice("gpuTemp", "gpu-temp:g0", "RTX", false, "g0", null, 89);
        Assert.False(BuiltInSensorRules.CpuDefaultStale(BuiltInSensorRules.Defaults(discrete), discrete));
    }

    [Fact]
    public void A_client_update_cannot_change_the_recorded_seed()
    {
        var current = BuiltInSensorRules.Defaults(Cpu(89));
        var sent = current.Clone();
        sent.SeededHigh = null;
        sent.High = 80;
        BuiltInSensorRules.Pin(sent, current);
        Assert.Equal(89, sent.SeededHigh);
    }

    [Fact]
    public void A_stored_rule_from_a_release_without_a_seed_follows_the_cpu_default_while_it_holds_the_old_flat_default()
    {
        var legacyCpu = BuiltInSensorRules.Defaults(Cpu(null));
        legacyCpu.SeededHigh = null;
        Assert.Equal(100, legacyCpu.High);
        Assert.True(BuiltInSensorRules.CpuDefaultStale(legacyCpu, Cpu(89)));
        BuiltInSensorRules.RefreshCpuDefault(legacyCpu, Cpu(89));
        Assert.Equal(89, legacyCpu.High);
        Assert.Equal(89, legacyCpu.SeededHigh);

        var legacyIgpu = BuiltInSensorRules.Defaults(Igpu(null));
        legacyIgpu.SeededHigh = null;
        Assert.True(BuiltInSensorRules.CpuDefaultStale(legacyIgpu, Igpu(89)));

        var edited = BuiltInSensorRules.Defaults(Cpu(null));
        edited.SeededHigh = null;
        edited.High = 92;
        Assert.False(BuiltInSensorRules.CpuDefaultStale(edited, Cpu(89)));
    }

    [Fact]
    public void An_unknown_cpu_maximum_never_refreshes_a_limit()
    {
        var rule = BuiltInSensorRules.Defaults(Cpu(89));
        Assert.False(BuiltInSensorRules.CpuDefaultStale(rule, Cpu(null)));
    }

    [Fact]
    public void A_template_without_a_seed_that_holds_the_old_default_is_untouched_and_a_chosen_one_is_kept()
    {
        var d = new DiagnosticsSettings();
        BuiltInSensorRules.Ensure(d, newInstall: false);
        foreach (var t in d.SensorAlerts)
        {
            t.SeededHigh = null;
        }
        Assert.Equal(89, BuiltInSensorRules.Seed(Cpu(89), d).High);
        Assert.Equal(89, BuiltInSensorRules.Seed(Igpu(89), d).High);

        d.SensorAlerts.Single(r => r.Id == "builtin-cpu").High = 92;
        Assert.Equal(92, BuiltInSensorRules.Seed(Cpu(89), d).High);
    }

    // ---- CpuMaxCache ----

    private sealed class Source
    {
        public string? Model = "AMD Ryzen 7 7800X3D 8-Core Processor";
        public double? Tj;
        public Exception? Throw;
        public int Errors;

        public CpuMaxCache Cache() => new(
            () => Throw is null ? Model : throw Throw,
            () => Tj,
            _ => Errors++);
    }

    [Fact]
    public void The_cache_uses_the_table_then_a_hardware_reading_once_and_keeps_it()
    {
        var src = new Source();
        var cache = src.Cache();
        Assert.Equal(new CpuMax(89, false), cache.Get());

        src.Tj = 95;
        Assert.Equal(new CpuMax(95, true), cache.Get());

        src.Tj = 94;
        Assert.Equal(new CpuMax(95, true), cache.Get());
    }

    [Fact]
    public void The_cache_resolves_again_when_the_model_changes()
    {
        var src = new Source { Tj = 95 };
        var cache = src.Cache();
        Assert.Equal(95, cache.Get()!.Value.MaxC);

        src.Model = "Intel(R) Core(TM) i9-14900K";
        src.Tj = null;
        Assert.Equal(new CpuMax(100, false), cache.Get());
    }

    [Fact]
    public void A_blank_model_reuses_the_last_one_and_keeps_a_hardware_reading()
    {
        var src = new Source { Model = "Intel(R) Core(TM) i9-14900K", Tj = 100 };
        var cache = src.Cache();
        Assert.Equal(new CpuMax(100, true), cache.Get());

        src.Model = "";
        src.Tj = null;
        Assert.Equal(new CpuMax(100, true), cache.Get());
        src.Model = null;
        Assert.Equal(new CpuMax(100, true), cache.Get());
    }

    [Fact]
    public void A_blank_model_before_any_is_known_gives_no_value()
    {
        var src = new Source { Model = "" };
        Assert.Null(src.Cache().Get());
    }

    [Fact]
    public void A_failing_lookup_keeps_the_previous_value_and_reports_the_error()
    {
        var src = new Source();
        var cache = src.Cache();
        Assert.Equal(89, cache.Get()!.Value.MaxC);

        src.Throw = new InvalidOperationException("boom");
        Assert.Equal(89, cache.Get()!.Value.MaxC);
        Assert.Equal(1, src.Errors);

        var fresh = new Source { Throw = new InvalidOperationException("boom") };
        Assert.Null(fresh.Cache().Get());
    }
}
