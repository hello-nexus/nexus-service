using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Media;
using Nexus.Service.Models.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Static mode owns the solid fills and the frozen patterns. Its selection
/// persists under Lighting.Static while template slots stay shared with animate,
/// so a fill keeps the colours it had before the mode existed.
/// </summary>
public class StaticModeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonConfigStore _store;
    private readonly LightingEngine _engine;
    private readonly LightingOutputHub _hub;
    private readonly GpuContext _gpu;
    private readonly LightingProvider _provider;

    public StaticModeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "nexus-static-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        _engine = new LightingEngine();
        _hub = new LightingOutputHub();
        _gpu = new GpuContext(160, 90);
        _provider = new LightingProvider(_store, _engine, _hub, _gpu, new MediaLibrary(), new Nexus.Service.Platform.DefaultMonitorEnumerator());
    }

    public void Dispose()
    {
        _provider.Dispose();
        _gpu.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static StaticHeadlessStart BodyFrom(string effect, AnimateEffectState look) => new()
    {
        Effect = effect,
        Intensity = look.Intensity,
        Hue = look.Hue,
        Colorize = look.Colorize,
        Saturation = look.Saturation,
        Contrast = look.Contrast,
        Persist = true,
        Params = look.Params.Select(kv => new ShaderParam { Name = kv.Key, Value = kv.Value }).ToList(),
    };

    [Fact]
    public async Task The_set_mode_app_action_persists_and_acks_static_for_game_sync()
    {
        var registry = new Nexus.Service.Widgets.AppActionRegistry();
        Nexus.Service.Widgets.AppActions.LightingActions.RegisterAll(registry);
        var services = new ServiceCollection()
            .AddSingleton<ILightingProvider>(_provider)
            .BuildServiceProvider();
        Assert.True(registry.TryGet("lighting.setMode", out var handler));

        var result = await handler(services, new Dictionary<string, JsonElement> { ["mode"] = JsonDocument.Parse("\"gamesync\"").RootElement }, CancellationToken.None);

        Assert.Equal("static", _store.Load().Lighting.Sync);
        Assert.Equal("static", result!.Value.GetProperty("applied").GetString());
    }

    [Fact]
    public void A_removed_game_sync_mode_arriving_at_runtime_persists_as_static()
    {
        _provider.SetSync("gamesync");

        Assert.Equal("static", _store.Load().Lighting.Sync);
    }

    [Fact]
    public void StartStatic_persists_the_mode_and_selection()
    {
        _provider.StartStatic(new StaticHeadlessStart { Effect = "gradientlinear", Persist = true });

        var lighting = _store.Load().Lighting;
        Assert.Equal("static", lighting.Sync);
        Assert.Equal("gradientlinear", lighting.Static.Effect);
    }

    [Fact]
    public void Every_catalog_pattern_is_startable()
    {
        foreach (var key in StaticEffectCatalog.Patterns)
        {
            _provider.StartStatic(new StaticHeadlessStart { Effect = key, Persist = true });
            Assert.Equal(key, _store.Load().Lighting.Static.Effect);
        }
    }

    [Fact]
    public void Activating_the_default_look_stores_no_state_entry()
    {
        _provider.StartStatic(BodyFrom("simplered", AnimateTemplateDefaults.Slot("simplered", 0)!));

        Assert.False(_store.Load().Lighting.Static.States.ContainsKey("simplered"));
    }

    [Fact]
    public void Custom_look_stores_a_state_entry_and_returning_to_default_removes_it()
    {
        var canon = AnimateTemplateDefaults.Slot("simplered", 0)!;
        var custom = new AnimateEffectState
        {
            Intensity = canon.Intensity,
            Hue = 0.42f,
            Colorize = canon.Colorize,
            Saturation = canon.Saturation,
            Contrast = canon.Contrast,
            Params = new Dictionary<string, float>(canon.Params),
        };

        _provider.StartStatic(BodyFrom("simplered", custom));
        Assert.Equal(0.42f, _store.Load().Lighting.Static.States["simplered"].Hue, 3);

        _provider.StartStatic(BodyFrom("simplered", canon));
        Assert.False(_store.Load().Lighting.Static.States.ContainsKey("simplered"));
    }

    [Fact]
    public void StartAnimate_with_any_static_key_lands_in_static()
    {
        // Settings written before the split, Stream Deck rgbEffect buttons and
        // set_static_color all still name a fill through the animate entrypoint.
        foreach (var key in new[] { "simpleblue", "stripes" })
        {
            _provider.StartAnimate(new AnimateHeadlessStart { Effect = key, Speed = 50, Persist = true });
            var l = _store.Load().Lighting;
            Assert.Equal("static", l.Sync);
            Assert.Equal(key, l.Static.Effect);
            Assert.False(l.Animate.States.ContainsKey(key));
        }
    }

    [Fact]
    public void Each_mode_remembers_its_own_selection()
    {
        // The two catalogs are disjoint now, so switching modes must not drag
        // the other mode's selection along.
        _provider.StartStatic(new StaticHeadlessStart { Effect = "checker", Persist = true });
        _provider.StartAnimate(new AnimateHeadlessStart { Effect = "fire", Speed = 50, Persist = true });

        var lighting = _store.Load().Lighting;
        Assert.Equal("checker", lighting.Static.Effect);
        Assert.Equal("fire", lighting.Animate.Effect);
        Assert.Equal("fire", lighting.Sync);
    }

    [Fact]
    public void Pattern_colours_persist_as_ordinary_params()
    {
        // Colours ride the float params dictionary as HSV triples, which is what
        // lets two-tone patterns work with no new wire or storage shape.
        _provider.StartStatic(new StaticHeadlessStart
        {
            Effect = "splitsharp",
            Persist = true,
            Params = new List<ShaderParam>
            {
                new() { Name = "u_aHue", Value = 0.25f },
                new() { Name = "u_bHue", Value = 0.75f },
            },
        });

        var saved = _store.Load().Lighting.Static.States["splitsharp"].Params;
        Assert.Equal(0.25f, saved["u_aHue"], 3);
        Assert.Equal(0.75f, saved["u_bHue"], 3);
    }

    [Fact]
    public void GetSync_reports_the_mode_not_the_shader_name()
    {
        // The engine runs a catalog shader by name, so reporting the engine
        // verbatim (as animate does) would classify a frozen pattern as
        // Animation on every surface that reads /lighting/current.
        _provider.StartStatic(new StaticHeadlessStart { Effect = "gradientlinear", Persist = true });

        Assert.Equal("static", _provider.GetSync());
    }

    [Fact]
    public void GetSync_still_reports_the_effect_name_for_animate()
    {
        _provider.StartAnimate(new AnimateHeadlessStart { Effect = "fire", Speed = 50, Persist = true });

        Assert.Equal("fire", _provider.GetSync());
    }

    [Fact]
    public void Switching_from_static_to_animate_unfreezes_the_engine()
    {
        // Leaving Static must clear the engine hold, or the animation renders a
        // single frame and GetSync keeps reporting static.
        _provider.StartStatic(new StaticHeadlessStart { Effect = "stripes", Persist = true });
        Assert.Equal("static", _provider.GetSync());

        _provider.StartAnimate(new AnimateHeadlessStart { Effect = "fire", Speed = 50, Persist = true });

        Assert.Equal("fire", _provider.GetSync());
        Assert.False(_engine.Frozen);
    }

    [Fact]
    public void An_unknown_key_is_rejected_rather_than_coerced()
    {
        Assert.Throws<System.ArgumentException>(() =>
            _provider.StartStatic(new StaticHeadlessStart { Effect = "fire", Persist = true }));
        Assert.False(_store.Load().Lighting.Static.States.ContainsKey("fire"));
    }

    [Fact]
    public void An_empty_key_still_gets_the_default_effect()
    {
        _provider.StartStatic(new StaticHeadlessStart { Effect = "", Persist = true });

        Assert.Equal(StaticEffectCatalog.DefaultEffect, _store.Load().Lighting.Static.Effect);
    }

    [Fact]
    public void A_fresh_profile_starts_on_the_default_effect()
    {
        Assert.Equal(StaticEffectCatalog.DefaultEffect, _store.Load().Lighting.Static.Effect);
    }

    [Fact]
    public void Coerce_keeps_catalog_keys_and_defaults_unknown_ones()
    {
        Assert.Equal("simplered", StaticEffectCatalog.Coerce("simplered"));
        Assert.Equal("gradientlinear", StaticEffectCatalog.Coerce("gradientlinear"));
        Assert.Equal(StaticEffectCatalog.DefaultEffect, StaticEffectCatalog.Coerce("simpleturquoise"));
        Assert.Equal(StaticEffectCatalog.DefaultEffect, StaticEffectCatalog.Coerce("nosucheffect"));
    }
}
