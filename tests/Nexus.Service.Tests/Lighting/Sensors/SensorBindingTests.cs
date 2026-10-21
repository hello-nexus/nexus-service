using System.Text.Json;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Lighting.Sensors;

public sealed class SensorBindingTests
{
    private static SensorBinding Bind(string sensor = "summary/cpu-usage", float min = 0f, float max = 100f, string category = "quick") =>
        new() { Category = category, Sensor = sensor, Min = min, Max = max };

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(50f, 0.5f)]
    [InlineData(100f, 1f)]
    [InlineData(-20f, 0f)]
    [InlineData(250f, 1f)]
    public void Normalize_maps_the_range_onto_zero_to_one_and_clamps(float value, float expected)
    {
        Assert.Equal(expected, Bind().Normalize(value), 5);
    }

    [Fact]
    public void Normalize_honors_a_non_zero_minimum()
    {
        Assert.Equal(0.5f, Bind(min: 30f, max: 90f).Normalize(60f), 5);
        Assert.Equal(0f, Bind(min: 30f, max: 90f).Normalize(10f), 5);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void A_missing_or_non_finite_reading_is_zero(float value)
    {
        Assert.Equal(0f, Bind().Normalize(value));
    }

    [Fact]
    public void An_empty_range_normalizes_to_zero()
    {
        Assert.Equal(0f, Bind(min: 5f, max: 5f).Normalize(5f));
        Assert.False(Bind(min: 9f, max: 1f).IsUsable);
    }

    [Fact]
    public void Merge_lets_an_override_replace_the_default_of_the_same_uniform_only()
    {
        var baseMap = new Dictionary<string, SensorBinding> { ["u_a_s"] = Bind("cpu-a"), ["u_b_s"] = Bind("cpu-b") };
        var over = new Dictionary<string, SensorBinding> { ["u_b_s"] = Bind("gpu-b"), ["u_c_s"] = Bind("extra") };

        var merged = SensorBinding.Merge(baseMap, over)!;

        Assert.Equal("cpu-a", merged["u_a_s"].Sensor);
        Assert.Equal("gpu-b", merged["u_b_s"].Sensor);
        Assert.Equal("extra", merged["u_c_s"].Sensor);
        Assert.Equal("cpu-b", baseMap["u_b_s"].Sensor);
        Assert.NotSame(over["u_b_s"], merged["u_b_s"]);
    }

    [Fact]
    public void Merge_with_nothing_on_either_side_is_null()
    {
        Assert.Null(SensorBinding.Merge(null, null));
        Assert.Null(SensorBinding.Merge(new Dictionary<string, SensorBinding>(), new Dictionary<string, SensorBinding>()));
        Assert.Equal("x", SensorBinding.Merge(null, new Dictionary<string, SensorBinding> { ["u"] = Bind("x") })!["u"].Sensor);
    }

    [Fact]
    public void CopyMap_is_deep_and_drops_empty_and_null_entries()
    {
        var map = new Dictionary<string, SensorBinding> { ["u_a_s"] = Bind("x"), ["u_b_s"] = null! };

        var copy = SensorBinding.CopyMap(map)!;
        copy["u_a_s"].Sensor = "changed";

        Assert.Equal("x", map["u_a_s"].Sensor);
        Assert.False(copy.ContainsKey("u_b_s"));
        Assert.Null(SensorBinding.CopyMap(null));
        Assert.Null(SensorBinding.CopyMap(new Dictionary<string, SensorBinding>()));
    }

    [Fact]
    public void MapEquals_treats_null_and_empty_alike_and_compares_every_field()
    {
        Assert.True(SensorBinding.MapEquals(null, new Dictionary<string, SensorBinding>()));
        var a = new Dictionary<string, SensorBinding> { ["u"] = Bind() };
        Assert.True(SensorBinding.MapEquals(a, SensorBinding.CopyMap(a)));
        Assert.False(SensorBinding.MapEquals(a, null));
        Assert.False(SensorBinding.MapEquals(a, new Dictionary<string, SensorBinding> { ["u"] = Bind(max: 90f) }));
        Assert.False(SensorBinding.MapEquals(a, new Dictionary<string, SensorBinding> { ["v"] = Bind() }));
    }

    [Fact]
    public void MapKey_ignores_insertion_order_and_changes_with_any_field()
    {
        var one = new Dictionary<string, SensorBinding> { ["u_a"] = Bind("x"), ["u_b"] = Bind("y") };
        var two = new Dictionary<string, SensorBinding> { ["u_b"] = Bind("y"), ["u_a"] = Bind("x") };

        Assert.Equal(SensorBinding.MapKey(one), SensorBinding.MapKey(two));
        two["u_a"] = Bind("x", max: 99f);
        Assert.NotEqual(SensorBinding.MapKey(one), SensorBinding.MapKey(two));
        Assert.Equal("", SensorBinding.MapKey(null));
    }

    [Fact]
    public void Sensors_round_trip_through_the_persisted_settings_json_in_state_slots_and_pins()
    {
        var settings = new NexusSettings();
        var picks = new Dictionary<string, SensorBinding> { ["u_a_s"] = Bind("cpu-temp", 30f, 95f, "cpu") };
        settings.Lighting.Animate.States["sensorbar"] = new AnimateEffectState { Sensors = SensorBinding.CopyMap(picks) };
        settings.Lighting.Animate.Templates["sensorbar"] = new AnimateEffectTemplates
        {
            Slots = new List<AnimateEffectState?> { null, new AnimateEffectState { Sensors = SensorBinding.CopyMap(picks) } },
        };
        settings.Lighting.DevicePins["dev"] = new DevicePin { Kind = DevicePinKinds.Animate, Effect = "sensorbar", Sensors = SensorBinding.CopyMap(picks) };

        var json = JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings);
        var back = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings)!;

        foreach (var got in new[]
        {
            back.Lighting.Animate.States["sensorbar"].Sensors,
            back.Lighting.Animate.Templates["sensorbar"].Slots[1]!.Sensors,
            back.Lighting.DevicePins["dev"].Sensors,
        })
        {
            var b = Assert.Single(got!).Value;
            Assert.Equal("cpu", b.Category);
            Assert.Equal("cpu-temp", b.Sensor);
            Assert.Equal(30f, b.Min);
            Assert.Equal(95f, b.Max);
        }
        Assert.Contains("\"sensors\"", json);
    }

    [Fact]
    public void A_state_without_sensors_writes_no_field_so_existing_files_are_unchanged()
    {
        var settings = new NexusSettings();
        settings.Lighting.Animate.States["plasma"] = new AnimateEffectState();
        settings.Lighting.DevicePins["dev"] = new DevicePin { Kind = DevicePinKinds.Animate, Effect = "plasma" };

        var json = JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings);

        Assert.DoesNotContain("sensors", json);
        Assert.Null(JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings)!.Lighting.Animate.States["plasma"].Sensors);
    }

    [Fact]
    public void Sensors_round_trip_on_the_wire_dtos_with_camel_case_names()
    {
        var body = new Nexus.Service.Models.Lighting.AnimateHeadlessStart
        {
            Effect = "sensorbar",
            Sensors = new Dictionary<string, SensorBinding> { ["u_a_s"] = Bind() },
        };

        var json = JsonSerializer.Serialize(body, AppJsonContext.Default.AnimateHeadlessStart);
        var back = JsonSerializer.Deserialize("{\"effect\":\"sensorbar\",\"sensors\":{\"u_a_s\":{\"category\":\"quick\",\"sensor\":\"summary/cpu-usage\",\"min\":0,\"max\":100}}}",
            AppJsonContext.Default.AnimateHeadlessStart)!;

        Assert.Contains("\"sensors\":{\"u_a_s\":{\"category\":\"quick\"", json.Replace(" ", ""));
        Assert.Equal("summary/cpu-usage", back.Sensors!["u_a_s"].Sensor);
        Assert.Equal(100f, back.Sensors["u_a_s"].Max);
    }

    [Fact]
    public void The_shader_source_response_lists_sensor_uniforms_as_uniform_and_binding()
    {
        var response = new Nexus.Service.Models.Lighting.ShaderSourceResponse { Frag = "x" };
        response.Sensors.Add(new Nexus.Service.Models.Lighting.SensorUniformInfo { Uniform = "u_a_s", Binding = Bind() });

        var json = JsonSerializer.Serialize(response, AppJsonContext.Default.ShaderSourceResponse).Replace(" ", "");

        Assert.Contains("\"sensors\":[{\"uniform\":\"u_a_s\",\"binding\":{\"category\":\"quick\"", json);
    }

    [Fact]
    public void IsUsable_is_never_written_and_old_json_carrying_it_still_reads()
    {
        var json = JsonSerializer.Serialize(Bind(), AppJsonContext.Default.SensorBinding);
        var persisted = JsonSerializer.Serialize(Bind(), PersistenceJsonContext.Default.SensorBinding);

        Assert.DoesNotContain("sUsable", json);
        Assert.DoesNotContain("sUsable", persisted);
        var back = JsonSerializer.Deserialize(
            "{\"category\":\"quick\",\"sensor\":\"summary/cpu-usage\",\"min\":0,\"max\":100,\"isUsable\":true}",
            PersistenceJsonContext.Default.SensorBinding)!;
        Assert.Equal("summary/cpu-usage", back.Sensor);
        Assert.True(back.IsUsable);
    }
}
