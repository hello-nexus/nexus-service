using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Routes;
using Nexus.Service.Telemetry;
using Xunit;

public class LightingEffectTelemetryTests
{
    private sealed class RecordingTelemetry : ITelemetry
    {
        public List<(string Event, Dictionary<string, object?> Props)> Events { get; } = new();
        public void Capture(string @event, params (string Key, object? Value)[] properties) =>
            Events.Add((@event, properties.ToDictionary(p => p.Key, p => p.Value)));
        public void Identify(params (string Key, object? Value)[] properties) { }
    }

    private static RecordingTelemetry Fresh()
    {
        // Process-static dedupe slot; every case starts from a known state.
        LightingRoutes.ResetEffectSignature();
        return new RecordingTelemetry();
    }

    [Theory]
    [InlineData("plasma", "plasma")]
    [InlineData("solid_fill", "solid_fill")]
    [InlineData("My Custom Look", "other")]
    [InlineData("C:\\Users\\nicola\\look.frag", "other")]
    public void Keeps_effect_names_to_a_slug(string effect, string expected)
    {
        Assert.Equal(expected, LightingRoutes.SafeSlug(effect));
    }

    [Fact]
    public void Reports_the_mode_the_provider_actually_ran()
    {
        // StartAnimate reroutes any static-catalog key into StartStatic, so the
        // route it arrived on is not the mode that ran. The search bar posts the
        // whole effect pool to animate, so this is a shipping path.
        Assert.Equal(("static", "gradientlinear"), LightingRoutes.ResolveEffect("animate", "gradientlinear"));
        Assert.Equal(("animate", "plasma"), LightingRoutes.ResolveEffect("animate", "plasma"));
    }

    [Fact]
    public void Normalizes_the_key_the_way_the_provider_does()
    {
        // The provider lowercases before matching, so a mixed-case key applies
        // correctly; reporting it raw would bucket it as "other".
        Assert.Equal(("animate", "plasma"), LightingRoutes.ResolveEffect("animate", "PLASMA"));
        Assert.Equal(("animate", "plasma"), LightingRoutes.ResolveEffect("animate", " Plasma "));
    }

    [Fact]
    public void Fills_in_the_defaults_the_provider_applies()
    {
        // An animate start with no effect runs rainbow; static falls back to the
        // catalog default. Reporting "other" for either would be wrong.
        Assert.Equal(("animate", "rainbow"), LightingRoutes.ResolveEffect("animate", null));
        Assert.Equal(("static", "gradientlinear"), LightingRoutes.ResolveEffect("static", ""));
    }

    [Fact]
    public void Screen_mode_carries_no_effect()
    {
        // StartScreen reads only Monitor; the client sends a hardcoded constant.
        Assert.Equal(("screen", "screen"), LightingRoutes.ResolveEffect("screen", "average"));
        Assert.Equal(("screen", "screen"), LightingRoutes.ResolveEffect("screen", null));
    }

    [Fact]
    public void A_repeat_is_silent_and_a_change_is_not()
    {
        var t = Fresh();
        LightingRoutes.CaptureEffect(t, "animate", "plasma");
        LightingRoutes.CaptureEffect(t, "animate", "plasma");
        Assert.Single(t.Events);
        Assert.Equal(TelemetryEvents.LightingEffectApplied, t.Events[0].Event);
        Assert.Equal("animate", t.Events[0].Props["mode"]);
        Assert.Equal("plasma", t.Events[0].Props["effect"]);

        LightingRoutes.CaptureEffect(t, "animate", "fire");
        Assert.Equal(2, t.Events.Count);
    }

    [Fact]
    public void Switching_away_and_back_is_two_transitions()
    {
        var t = Fresh();
        LightingRoutes.CaptureEffect(t, "animate", "plasma");
        LightingRoutes.CaptureEffect(t, "screen", null);
        LightingRoutes.CaptureEffect(t, "animate", "plasma");
        Assert.Equal(3, t.Events.Count);
    }

    [Fact]
    public void Stopping_lets_the_same_effect_report_again()
    {
        var t = Fresh();
        LightingRoutes.CaptureEffect(t, "animate", "plasma");
        LightingRoutes.ResetEffectSignature();
        LightingRoutes.CaptureEffect(t, "animate", "plasma");
        Assert.Equal(2, t.Events.Count);
    }

    // The routes' real order: read the persisted look, start (which rewrites it
    // the way the provider does), then capture.
    private static Nexus.Service.Tests.InMemoryConfigStore StoreWith(string sync, string? staticEffect = null, string? staticColor = null)
    {
        var store = new Nexus.Service.Tests.InMemoryConfigStore();
        store.Update(s =>
        {
            s.Lighting.Sync = sync;
            if (staticEffect is not null) s.Lighting.Static.Effect = staticEffect;
            s.Lighting.Static.Color = staticColor;
        });
        return store;
    }

    private static void Animate(RecordingTelemetry t, Nexus.Service.Tests.InMemoryConfigStore store, string effect, bool persist = true) =>
        LightingRoutes.StartAndCapture(t, store, "animate", effect, null, () =>
        {
            if (persist) store.Update(s => s.Lighting.Sync = effect);
        });

    private static void Static(RecordingTelemetry t, Nexus.Service.Tests.InMemoryConfigStore store, string effect, string? color = null) =>
        LightingRoutes.StartAndCapture(t, store, "static", effect, color, () => store.Update(s =>
        {
            s.Lighting.Sync = "static";
            s.Lighting.Static.Color = color;
            if (color is null) s.Lighting.Static.Effect = effect;
        }));

    [Fact]
    public void A_replay_of_the_persisted_look_after_a_restart_is_silent()
    {
        var t = Fresh();
        Animate(t, StoreWith("plasma"), "plasma");
        Assert.Empty(t.Events);
    }

    [Fact]
    public void A_different_look_than_the_persisted_one_is_captured_once()
    {
        var t = Fresh();
        var store = StoreWith("plasma");
        Animate(t, store, "fire");
        Animate(t, store, "fire");
        Assert.Single(t.Events);
    }

    [Fact]
    public void After_a_stop_the_same_look_is_captured()
    {
        var t = Fresh();
        var store = StoreWith("plasma");
        Animate(t, store, "plasma");
        store.Update(s => s.Lighting.Sync = "none");
        LightingRoutes.ResetEffectSignature();
        Animate(t, store, "plasma");
        Assert.Single(t.Events);
    }

    [Fact]
    public void A_look_changed_outside_the_routes_still_dedupes_its_replay_and_counts_a_real_change()
    {
        var t = Fresh();
        var store = StoreWith("plasma");
        Animate(t, store, "plasma");
        store.Update(s => s.Lighting.Sync = "fire"); // profile switch
        Animate(t, store, "fire");
        Assert.Empty(t.Events);
        Animate(t, store, "plasma");
        Assert.Single(t.Events);
    }

    [Fact]
    public void The_same_change_after_an_outside_write_restored_the_old_look_counts_again()
    {
        var t = Fresh();
        var store = StoreWith("plasma");
        Animate(t, store, "fire");
        store.Update(s => s.Lighting.Sync = "plasma"); // Deck button, profile, MCP or auto-restore
        Animate(t, store, "fire");
        Assert.Equal(2, t.Events.Count);
    }

    [Fact]
    public void An_unparseable_persisted_static_color_is_not_flat()
    {
        var store = StoreWith("static", "stripes", "not-a-color");
        Assert.Equal("static:stripes", LightingRoutes.PersistedEffectSignature(store.Load().Lighting));
    }

    [Fact]
    public void A_to_B_to_A_counts_every_change()
    {
        var t = Fresh();
        var store = StoreWith("plasma");
        Animate(t, store, "fire");
        Animate(t, store, "plasma");
        Animate(t, store, "fire");
        Assert.Equal(3, t.Events.Count);
    }

    [Fact]
    public void A_flat_color_then_stripes_counts_and_repeated_flat_picks_do_not()
    {
        var t = Fresh();
        var store = StoreWith("static", "stripes");
        Static(t, store, "gradientlinear", "#ff0000");
        Static(t, store, "gradientlinear", "#00ff00");
        Static(t, store, "gradientlinear", "#0000ff");
        Assert.Single(t.Events);
        Assert.Equal("flat", t.Events[0].Props["effect"]);
        Static(t, store, "stripes");
        Assert.Equal(2, t.Events.Count);
        Assert.Equal("stripes", t.Events[1].Props["effect"]);
    }

    [Fact]
    public void A_replay_of_a_persisted_flat_color_is_silent_whatever_effect_the_client_sends()
    {
        var t = Fresh();
        var store = StoreWith("static", "stripes", "#ff0000");
        Static(t, store, "gradientlinear", "#ff0000");
        Assert.Empty(t.Events);
    }

    [Fact]
    public void A_non_persisting_replay_of_the_persisted_look_is_silent()
    {
        var t = Fresh();
        var store = StoreWith("plasma");
        Animate(t, store, "plasma", persist: false);
        Animate(t, store, "plasma", persist: false);
        Assert.Empty(t.Events);
    }

    [Fact]
    public void A_non_persisting_different_look_counts_once_not_per_post()
    {
        var t = Fresh();
        var store = StoreWith("plasma");
        for (var i = 0; i < 5; i++) Animate(t, store, "fire", persist: false);
        Assert.Single(t.Events);
        // Dragging back to the persisted look and out again is two more real changes.
        Animate(t, store, "plasma", persist: false);
        Animate(t, store, "fire", persist: false);
        Assert.Equal(2, t.Events.Count);
    }

    [Fact]
    public void Two_interleaved_requests_for_the_same_change_capture_it_once()
    {
        var t = Fresh();
        var store = StoreWith("plasma");
        // The second request runs entirely inside the first one's start, so both
        // read the old persisted look.
        LightingRoutes.StartAndCapture(t, store, "animate", "fire", null, () =>
        {
            Animate(t, store, "fire");
            store.Update(s => s.Lighting.Sync = "fire");
        });
        Assert.Single(t.Events);
    }

    [Fact]
    public void A_replay_racing_a_real_change_cannot_swallow_it()
    {
        var t = Fresh();
        var store = StoreWith("plasma");
        // The replay reads plasma, then the user's change to fire completes, then the replay finishes.
        LightingRoutes.StartAndCapture(t, store, "animate", "plasma", null, () => Animate(t, store, "fire"));
        Assert.Single(t.Events);
        Assert.Equal("fire", t.Events[0].Props["effect"]);
    }

    [Theory]
    [InlineData("music")]
    [InlineData("media")]
    [InlineData("gif")]
    [InlineData("")]
    public void Modes_the_routes_do_not_track_have_no_persisted_signature(string sync)
    {
        Assert.Equal("", LightingRoutes.PersistedEffectSignature(new Nexus.Service.Persistence.LightingSettings { Sync = sync }));
    }
}
