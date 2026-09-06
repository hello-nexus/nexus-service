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
}
