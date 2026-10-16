using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Lighting.CustomEffects;

namespace Nexus.Service.Tests.Lighting.Sensors;

/// <summary>The sensor chain of one effect (binding, feed, normalize, smooth) without a GL context: NextSensorValue is what PushSensors uploads.</summary>
public sealed class ShaderEffectSensorTests : IDisposable
{
    private const string CpuLine =
        "uniform float u_a_s; // sensor {\"category\":\"quick\",\"sensor\":\"summary/cpu-usage\",\"min\":0,\"max\":100}\n";

    private readonly GpuContext _gpu = new(160, 90);
    private readonly Dictionary<string, float> _readings = new();
    private long _now = 10_000;
    private readonly SensorFeed _feed;

    public ShaderEffectSensorTests()
    {
        _feed = new SensorFeed(null, (category, sensor) => _readings.TryGetValue(category + "/" + sensor, out var v) ? v : float.NaN,
            () => Interlocked.Read(ref _now), background: false);
    }

    public void Dispose()
    {
        _feed.Dispose();
        _gpu.Dispose();
    }

    private ShaderEffect Effect(string? line = null) =>
        new("t", _gpu, CustomFrags.Frag(extra: line ?? CpuLine)) { SensorFeed = _feed };

    private static SensorBinding Bind(string sensor, float min = 0f, float max = 100f) =>
        new() { Category = "quick", Sensor = sensor, Min = min, Max = max };

    [Fact]
    public void The_authored_default_binding_drives_the_uniform_when_nothing_overrides_it()
    {
        _readings["quick/summary/cpu-usage"] = 50f;
        var fx = Effect();
        _ = fx.NextSensorValue(0, 0);
        _feed.Tick(_now);

        Assert.Equal(0.5f, fx.NextSensorValue(0, 16), 3);
    }

    [Fact]
    public void A_user_override_beats_the_default_and_an_unrelated_key_is_ignored()
    {
        _readings["quick/summary/cpu-usage"] = 50f;
        _readings["quick/summary/memory-usage"] = 80f;
        var fx = Effect();
        fx.SensorOverrides = new Dictionary<string, SensorBinding>
        {
            ["u_a_s"] = Bind("summary/memory-usage", 0f, 160f),
            ["u_not_in_this_frag_s"] = Bind("summary/cpu-usage"),
        };
        _ = fx.NextSensorValue(0, 0);
        _feed.Tick(_now);

        Assert.Equal(0.5f, fx.NextSensorValue(0, 16), 3);
    }

    [Fact]
    public void An_unusable_override_falls_back_to_the_default()
    {
        var spec = SensorUniformSpec.Parse(CpuLine)[0];
        var broken = new Dictionary<string, SensorBinding> { ["u_a_s"] = Bind("summary/memory-usage", 10f, 10f) };

        Assert.Same(spec.Default, ShaderEffect.ResolveBinding(spec, broken));
        Assert.Same(spec.Default, ShaderEffect.ResolveBinding(spec, null));
        Assert.Same(spec.Default, ShaderEffect.ResolveBinding(spec, new Dictionary<string, SensorBinding>()));
    }

    [Fact]
    public void Swapping_the_override_map_rebinds_a_running_effect()
    {
        _readings["quick/summary/cpu-usage"] = 20f;
        _readings["quick/summary/memory-usage"] = 90f;
        var fx = Effect();
        _ = fx.NextSensorValue(0, 0);
        _feed.Tick(_now);
        Assert.Equal(0.2f, fx.NextSensorValue(0, 16), 3);

        fx.SensorOverrides = new Dictionary<string, SensorBinding> { ["u_a_s"] = Bind("summary/memory-usage") };
        _ = fx.NextSensorValue(0, 32);
        _feed.Tick(_now);

        // Glides from the old value rather than jumping.
        var next = fx.NextSensorValue(0, 48);
        Assert.InRange(next, 0.2f, 0.9f);
        Assert.NotEqual(0.9f, next);
    }

    [Fact]
    public void A_missing_sensor_reads_as_zero()
    {
        var fx = Effect();
        _ = fx.NextSensorValue(0, 0);
        _feed.Tick(_now);

        Assert.Equal(0f, fx.NextSensorValue(0, 16));
    }

    [Fact]
    public void An_effect_with_no_feed_reads_zero()
    {
        var fx = new ShaderEffect("t", _gpu, CustomFrags.Frag(extra: CpuLine));

        Assert.Equal(0f, fx.NextSensorValue(0, 0));
    }

    [Fact]
    public void The_value_is_zero_before_the_first_reading_then_the_first_reading_is_taken_as_is()
    {
        var fx = Effect();
        _ = fx.NextSensorValue(0, 0);
        Assert.Equal(0f, fx.NextSensorValue(0, 16));

        _readings["quick/summary/cpu-usage"] = 100f;
        _feed.Tick(_now);

        Assert.Equal(1f, fx.NextSensorValue(0, 32));
    }

    [Fact]
    public void A_reading_outside_the_range_clamps_and_a_later_change_glides()
    {
        _readings["quick/summary/cpu-usage"] = 500f;
        var fx = Effect();
        _ = fx.NextSensorValue(0, 0);
        _feed.Tick(_now);
        Assert.Equal(1f, fx.NextSensorValue(0, 16));

        _readings["quick/summary/cpu-usage"] = 0f;
        _feed.Tick(_now);
        var stepped = fx.NextSensorValue(0, 32);

        Assert.InRange(stepped, 0.9f, 0.999f);
    }

    [Fact]
    public void A_fixed_value_bypasses_the_feed()
    {
        var fx = Effect();
        fx.FixedSensorValue = 0.65f;

        Assert.Equal(0.65f, fx.NextSensorValue(0, 0));
        Assert.False(_feed.Tick(_now));
    }

    [Fact]
    public void An_effect_without_sensor_uniforms_never_touches_the_feed()
    {
        using var live = new SensorFeed(null, (_, _) => 1f, () => _now);
        var fx = new ShaderEffect("t", _gpu, CustomFrags.Frag()) { SensorFeed = live };

        Assert.False(fx.ReadsSensors);
        Assert.Empty(fx.SensorSpecs);
        Assert.False(live.IsPolling);
        Assert.False(live.Tick(_now));
    }

    [Fact]
    public void An_annotated_effect_reports_it_reads_sensors()
    {
        Assert.True(Effect().ReadsSensors);
    }
}
