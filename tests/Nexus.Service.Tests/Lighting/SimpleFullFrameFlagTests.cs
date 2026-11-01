using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests.Lighting;

/// <summary>
/// Full-frame sampling belongs to simple mode alone: the Simple flag on the start
/// request drives the engine, is persisted with the effect, and a restore replays it.
/// </summary>
public class SimpleFullFrameFlagTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigStore _store;
    private readonly List<IDisposable> _owned = new();

    public SimpleFullFrameFlagTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-simple-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
    }

    public void Dispose()
    {
        foreach (var o in _owned)
        {
            o.Dispose();
        }
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private (LightingProvider Provider, LightingEngine Engine) NewProvider()
    {
        var engine = new LightingEngine();
        var gpu = new GpuContext(160, 90);
        var provider = new LightingProvider(_store, engine, new LightingOutputHub(), gpu, new MediaLibrary(), new Nexus.Service.Platform.DefaultMonitorEnumerator());
        _owned.Add(provider);
        _owned.Add(gpu);
        return (provider, engine);
    }

    private static AnimateHeadlessStart Body(string effect, bool simple, float rotation = 0f, bool persist = true)
    {
        var body = new AnimateHeadlessStart
        {
            Effect = effect,
            Speed = 50,
            Intensity = 1f,
            Saturation = 1f,
            Contrast = 1f,
            Simple = simple,
            Persist = persist,
        };
        body.Params.Add(new ShaderParam { Name = "u_rotation", Value = rotation });
        return body;
    }

    [Theory]
    [InlineData("rainbow", true, true)]
    [InlineData("rainbow", false, false)]
    [InlineData("breathing", true, true)]
    [InlineData("breathing", false, false)]
    [InlineData("sweepcomet", true, true)]
    [InlineData("sweepcomet", false, false)]
    [InlineData("plasma", true, false)]
    public void Only_a_simple_start_samples_the_whole_canvas(string effect, bool simple, bool expected)
    {
        var (provider, engine) = NewProvider();
        provider.StartAnimate(Body(effect, simple));
        Assert.Equal(expected, engine.FullFrameSampling);
    }

    [Theory]
    [InlineData("rainbow", 0f, true)]
    [InlineData("rainbow", 90f, false)]
    [InlineData("sharplines", 45f, false)]
    [InlineData("sweepbrush", 0f, true)]
    [InlineData("sweepbrush", 90f, false)]
    public void Simple_start_keeps_the_rotation_rules(string effect, float rotation, bool expected)
    {
        var (provider, engine) = NewProvider();
        provider.StartAnimate(Body(effect, simple: true, rotation));
        Assert.Equal(expected, engine.FullFrameSampling);
    }

    [Fact]
    public void Switching_from_simple_to_advanced_on_the_same_effect_clears_full_frame()
    {
        var (provider, engine) = NewProvider();
        provider.StartAnimate(Body("rainbow", simple: true));
        Assert.True(engine.FullFrameSampling);
        provider.StartAnimate(Body("rainbow", simple: false, persist: false));
        Assert.False(engine.FullFrameSampling);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_flag_persists_and_a_restore_replays_it(bool simple)
    {
        var (first, _) = NewProvider();
        first.StartAnimate(Body("rainbow", simple));
        _store.FlushNow();
        Assert.Equal(simple, _store.Load().Lighting.Animate.Simple);

        var (second, restored) = NewProvider();
        LiveEngineSync.ApplyLighting(_store, second);

        Assert.Equal(simple, restored.FullFrameSampling);
    }

    [Fact]
    public void A_drag_without_persist_does_not_rewrite_the_stored_flag()
    {
        var (provider, _) = NewProvider();
        provider.StartAnimate(Body("rainbow", simple: true));
        provider.StartAnimate(Body("rainbow", simple: false, persist: false));
        Assert.True(_store.Load().Lighting.Animate.Simple);
    }

    [Fact]
    public void A_layout_preset_look_carries_the_flag()
    {
        var lighting = new LightingSettings();
        lighting.Animate.Effect = "rainbow";
        lighting.Animate.Simple = true;
        var look = LightingPresetLooks.Capture(lighting);
        Assert.True(look.AnimateSimple);

        var target = new LightingSettings();
        LightingPresetLooks.Apply(target, look);
        Assert.True(target.Animate.Simple);
    }
}
