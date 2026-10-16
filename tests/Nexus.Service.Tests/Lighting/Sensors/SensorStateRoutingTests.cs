using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Nexus.Service.Tests.Lighting.CustomEffects;

namespace Nexus.Service.Tests.Lighting.Sensors;

/// <summary>Where the user's sensor picks travel: start body, persisted state, presets, pins, Studio live. Real provider and engine, uninitialised GPU.</summary>
public sealed class SensorStateRoutingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-sensor-state-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly JsonConfigStore _config;
    private readonly LightingEngine _engine = new() { FrameIntervalMs = 10 };
    private readonly GpuContext _gpu = new(160, 90);
    private readonly SensorFeed _feed = new(null, (_, _) => float.NaN, null);
    private readonly LightingProvider _provider;
    private readonly string _id = CustomFrags.NewId();

    public SensorStateRoutingTests()
    {
        Directory.CreateDirectory(_dir);
        _config = new JsonConfigStore(Path.Combine(_dir, "settings.json"));
        _provider = new LightingProvider(_config, _engine, new LightingOutputHub(), _gpu, new MediaLibrary(),
            new Nexus.Service.Platform.DefaultMonitorEnumerator(), sensorFeed: _feed);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _feed.Dispose();
        _gpu.Dispose();
        ShaderLibrary.UnregisterCustom(_id);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static SensorBinding Bind(string sensor, string category = "quick", float min = 0f, float max = 100f) =>
        new() { Category = category, Sensor = sensor, Min = min, Max = max };

    private static Dictionary<string, SensorBinding> Picks(string sensor) => new() { ["u_sensorbar_level_s"] = Bind(sensor) };

    private static AnimateHeadlessStart Start(string effect, Dictionary<string, SensorBinding>? sensors) => new()
    {
        Effect = effect,
        Speed = 50,
        Intensity = 1f,
        Saturation = 1f,
        Contrast = 1f,
        Persist = true,
        Sensors = sensors,
    };

    [Fact]
    public void A_built_in_sensor_effect_runs_as_its_own_shader_not_the_rainbow_fallback()
    {
        _provider.StartAnimate(Start("sensorbar", null));

        var shader = Assert.IsType<ShaderEffect>(_engine.CurrentEffect);
        Assert.Equal("sensorbar", shader.Name);
        Assert.True(shader.ReadsSensors);
        Assert.Same(_feed, shader.SensorFeed);
    }

    [Fact]
    public void Start_applies_the_picks_to_the_running_effect_and_persists_them()
    {
        _provider.StartAnimate(Start("sensorbar", Picks("summary/memory-usage")));

        var shader = Assert.IsType<ShaderEffect>(_engine.CurrentEffect);
        Assert.Equal("summary/memory-usage", shader.SensorOverrides!["u_sensorbar_level_s"].Sensor);
        var saved = _config.Load().Lighting.Animate.States["sensorbar"];
        Assert.Equal("summary/memory-usage", saved.Sensors!["u_sensorbar_level_s"].Sensor);
    }

    [Fact]
    public void A_second_start_of_the_same_shader_updates_the_picks_in_place()
    {
        _provider.StartAnimate(Start("sensorbar", Picks("summary/memory-usage")));
        var first = _engine.CurrentEffect;

        _provider.StartAnimate(Start("sensorbar", Picks("summary/cpu-temp")));

        Assert.Same(first, _engine.CurrentEffect);
        Assert.Equal("summary/cpu-temp", ((ShaderEffect)first!).SensorOverrides!["u_sensorbar_level_s"].Sensor);
    }

    [Fact]
    public void Start_without_picks_clears_earlier_ones()
    {
        _provider.StartAnimate(Start("sensorbar", Picks("summary/memory-usage")));

        _provider.StartAnimate(Start("sensorbar", null));

        Assert.Null(((ShaderEffect)_engine.CurrentEffect!).SensorOverrides);
        Assert.Null(_config.Load().Lighting.Animate.States["sensorbar"].Sensors);
    }

    [Fact]
    public void The_caller_cannot_mutate_the_running_picks_through_the_body()
    {
        var body = Picks("summary/memory-usage");
        _provider.StartAnimate(Start("sensorbar", body));

        body["u_sensorbar_level_s"].Sensor = "tampered";

        Assert.Equal("summary/memory-usage", ((ShaderEffect)_engine.CurrentEffect!).SensorOverrides!["u_sensorbar_level_s"].Sensor);
    }

    [Fact]
    public void A_stored_state_with_picks_restores_them_when_replayed_as_a_start()
    {
        _config.Update(s => s.Lighting.Animate.States["sensorbar"] = new AnimateEffectState { Sensors = Picks("summary/gpu-temp") });
        var saved = _config.Load().Lighting.Animate.States["sensorbar"];

        _provider.StartAnimate(new AnimateHeadlessStart
        {
            Effect = "sensorbar",
            Speed = saved.Speed,
            Intensity = saved.Intensity,
            Sensors = saved.Sensors,
        });

        Assert.Equal("summary/gpu-temp", ((ShaderEffect)_engine.CurrentEffect!).SensorOverrides!["u_sensorbar_level_s"].Sensor);
    }

    [Fact]
    public void A_preset_slot_with_picks_is_not_pruned_as_a_default_and_reapplies_to_the_running_effect()
    {
        _provider.StartAnimate(Start("sensorbar", null));
        var shader = (ShaderEffect)_engine.CurrentEffect!;

        _provider.SaveAnimateTemplates(new Dictionary<string, AnimateEffectTemplates>
        {
            ["sensorbar"] = new()
            {
                Selected = 0,
                Slots = new List<AnimateEffectState?> { new() { Sensors = Picks("summary/memory-usage") } },
            },
        });

        var slot = _config.Load().Lighting.Animate.Templates["sensorbar"].Slots[0]!;
        Assert.Equal("summary/memory-usage", slot.Sensors!["u_sensorbar_level_s"].Sensor);
        Assert.Equal("summary/memory-usage", shader.SensorOverrides!["u_sensorbar_level_s"].Sensor);
    }

    [Fact]
    public void Two_slots_that_differ_only_in_picks_are_different_looks()
    {
        var a = new AnimateEffectState { Sensors = Picks("summary/cpu-usage") };
        var b = new AnimateEffectState { Sensors = Picks("summary/memory-usage") };
        var none = new AnimateEffectState();

        Assert.False(AnimateTemplateDefaults.StateEquals(a, b));
        Assert.False(AnimateTemplateDefaults.StateEquals(a, none));
        Assert.True(AnimateTemplateDefaults.StateEquals(none, new AnimateEffectState { Sensors = new() }));
    }

    [Fact]
    public void A_layout_preset_look_captures_a_copy_of_the_picks()
    {
        var lighting = new LightingSettings { Sync = "sensorbar", Animate = new AnimateSettings { Effect = "sensorbar" } };
        lighting.Animate.States["sensorbar"] = new AnimateEffectState { Sensors = Picks("summary/cpu-usage") };

        var look = LightingPresetLooks.Capture(lighting);
        lighting.Animate.States["sensorbar"].Sensors!["u_sensorbar_level_s"].Sensor = "changed";

        Assert.Equal("summary/cpu-usage", look.AnimateState!.Sensors!["u_sensorbar_level_s"].Sensor);
    }

    [Fact]
    public void A_pin_with_picks_renders_apart_from_a_pin_without_and_keeps_its_own_tag()
    {
        var plain = new DevicePin { Kind = DevicePinKinds.Animate, Effect = "sensorbar" };
        var picked = new DevicePin { Kind = DevicePinKinds.Animate, Effect = "sensorbar", Sensors = Picks("summary/memory-usage") };
        var other = new DevicePin { Kind = DevicePinKinds.Animate, Effect = "sensorbar", Sensors = Picks("summary/gpu-temp") };

        var plainKey = DevicePinLook.AnimateRenderKey(plain);
        var pickedKey = DevicePinLook.AnimateRenderKey(picked);

        Assert.Equal("sensorbar", plainKey);
        Assert.NotEqual(plainKey, pickedKey);
        Assert.NotEqual(pickedKey, DevicePinLook.AnimateRenderKey(other));
        Assert.Equal(pickedKey, DevicePinLook.AnimateRenderKey(picked.Copy()));
        Assert.NotEqual(DevicePinLook.StaticKey(plain), DevicePinLook.StaticKey(picked));
    }

    [Fact]
    public void A_pinned_sensor_effect_builds_with_the_preset_picks_overlaid_by_the_pins()
    {
        var plain = new DevicePin { Kind = DevicePinKinds.Animate, Effect = "sensorbar" };
        var picked = new DevicePin
        {
            Kind = DevicePinKinds.Animate,
            Effect = "sensorbar",
            Sensors = new() { ["u_sensorbar_level_s"] = Bind("summary/memory-usage") },
        };
        _config.Update(s =>
        {
            s.Lighting.Animate.Templates["sensorbar"] = new AnimateEffectTemplates
            {
                Selected = 0,
                Slots = new List<AnimateEffectState?>
                {
                    new() { Sensors = new() { ["u_sensorbar_level_s"] = Bind("summary/cpu-temp"), ["u_other_s"] = Bind("summary/gpu-temp") } },
                },
            };
            s.Lighting.DevicePins["a"] = plain;
            s.Lighting.DevicePins["b"] = picked;
        });

        var plainFx = Assert.IsType<ShaderEffect>(_engine.PinEffectFactory!(DevicePinLook.AnimateRenderKey(plain), null));
        var pickedFx = Assert.IsType<ShaderEffect>(_engine.PinEffectFactory!(DevicePinLook.AnimateRenderKey(picked), null));

        Assert.Equal("sensorbar", plainFx.Name);
        Assert.Equal("sensorbar", pickedFx.Name);
        Assert.Equal("summary/cpu-temp", plainFx.SensorOverrides!["u_sensorbar_level_s"].Sensor);
        Assert.Equal("summary/memory-usage", pickedFx.SensorOverrides!["u_sensorbar_level_s"].Sensor);
        Assert.Equal("summary/gpu-temp", pickedFx.SensorOverrides["u_other_s"].Sensor);
        Assert.NotEqual(_engine.PinLookTag!(DevicePinLook.AnimateRenderKey(plain)), _engine.PinLookTag!(DevicePinLook.AnimateRenderKey(picked)));
    }

    [Fact]
    public void Changing_a_pins_picks_changes_its_tag_and_updates_the_instance_in_place()
    {
        var pin = new DevicePin { Kind = DevicePinKinds.Animate, Effect = "sensorbar", Sensors = Picks("summary/memory-usage") };
        _config.Update(s => s.Lighting.DevicePins["a"] = pin);
        var key = DevicePinLook.AnimateRenderKey(pin);
        var first = (ShaderEffect)_engine.PinEffectFactory!(key, null)!;
        var tag = _engine.PinLookTag!(key);

        // Same picks, new preset look: same render key, instance updated, tag moves.
        _config.Update(s => s.Lighting.Animate.Templates["sensorbar"] = new AnimateEffectTemplates
        {
            Selected = 0,
            Slots = new List<AnimateEffectState?> { new() { Hue = 0.5f } },
        });

        Assert.Same(first, _engine.PinEffectFactory!(key, first));
        Assert.Equal(0.5f, first.Hue);
        Assert.NotEqual(tag, _engine.PinLookTag!(key));
    }

    [Fact]
    public void A_sensor_effect_is_an_acceptable_animate_pin_and_the_picks_survive_normalization()
    {
        var pin = new DevicePin { Kind = DevicePinKinds.Animate, Effect = "sensorbar", Sensors = Picks("summary/memory-usage"), Hue = 0.4f };

        Assert.Null(DevicePinTracker.Validate(pin));
        var normalized = DevicePinTracker.Normalize(pin);

        Assert.Equal("summary/memory-usage", normalized.Sensors!["u_sensorbar_level_s"].Sensor);
        Assert.Null(normalized.Hue);
        Assert.NotSame(pin.Sensors, normalized.Sensors);
    }

    [Fact]
    public void A_mirror_pin_may_not_carry_picks()
    {
        Assert.NotNull(DevicePinTracker.Validate(new DevicePin { Kind = DevicePinKinds.Mirror, Sensors = Picks("x") }));
        Assert.Null(DevicePinTracker.Validate(new DevicePin { Kind = DevicePinKinds.Mirror }));
    }

    [Fact]
    public void A_pin_with_picks_survives_the_pin_tracker_set_and_store_round_trip()
    {
        var tracker = new DevicePinTracker(_config);
        tracker.Set("dev", new DevicePin { Kind = DevicePinKinds.Animate, Effect = "sensorbar", Sensors = Picks("summary/memory-usage") });

        Assert.True(tracker.TryGet("dev", out var live));
        Assert.Equal("summary/memory-usage", live.Sensors!["u_sensorbar_level_s"].Sensor);
        Assert.Equal("summary/memory-usage", _config.Load().Lighting.DevicePins["dev"].Sensors!["u_sensorbar_level_s"].Sensor);
    }

    [Fact]
    public void Studio_live_carries_the_picks_on_open_and_on_renewal_in_place()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag(extra:
            "uniform float u_a_s; // sensor {\"category\":\"quick\",\"sensor\":\"summary/cpu-usage\",\"min\":0,\"max\":100}\n"));
        var frag = ShaderLibrary.Get("custom:" + _id);
        var open = _provider.StartStudioLive(new StudioLiveBody { Frag = frag, Sensors = new() { ["u_a_s"] = Bind("summary/memory-usage") } });
        var live = _provider.StudioLiveEffect!;
        Assert.Equal("summary/memory-usage", live.SensorOverrides!["u_a_s"].Sensor);
        Assert.Same(_feed, live.SensorFeed);

        _provider.StartStudioLive(new StudioLiveBody { SessionId = open.SessionId, Frag = frag, Sensors = new() { ["u_a_s"] = Bind("summary/gpu-temp") } });

        Assert.Same(live, _provider.StudioLiveEffect);
        Assert.Equal("summary/gpu-temp", live.SensorOverrides!["u_a_s"].Sensor);
    }

    [Fact]
    public void Reloading_a_running_custom_effect_keeps_its_picks()
    {
        ShaderLibrary.RegisterCustom(_id, CustomFrags.Frag(extra:
            "uniform float u_a_s; // sensor {\"category\":\"quick\",\"sensor\":\"summary/cpu-usage\",\"min\":0,\"max\":100}\n"));
        var key = "custom:" + _id;
        _provider.StartAnimate(Start(key, new() { ["u_a_s"] = Bind("summary/memory-usage") }));

        _provider.ReloadCustomEffect(key);

        var after = (ShaderEffect)_engine.CurrentEffect!;
        Assert.Equal("summary/memory-usage", after.SensorOverrides!["u_a_s"].Sensor);
    }

    [Fact]
    public void An_effect_with_no_sensor_uniform_starts_no_polling_even_with_a_feed()
    {
        _provider.StartAnimate(Start("plasma", null));

        var shader = Assert.IsType<ShaderEffect>(_engine.CurrentEffect);
        Assert.False(shader.ReadsSensors);
        Assert.False(_feed.IsPolling);
    }

    [Fact]
    public void ReadsSensors_is_true_only_for_an_effect_that_declares_a_sensor_uniform()
    {
        _provider.StartAnimate(Start("sensorbar", null));
        var sensor = (ShaderEffect)_engine.CurrentEffect!;
        _provider.StartAnimate(Start("plasma", null));
        var plain = (ShaderEffect)_engine.CurrentEffect!;

        Assert.True(sensor.ReadsSensors);
        Assert.False(plain.ReadsSensors);
    }
}
