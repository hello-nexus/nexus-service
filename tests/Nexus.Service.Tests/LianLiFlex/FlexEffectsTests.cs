using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLiFlex;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexEffectsTests
{
    private static readonly string[] TlKeys =
    {
        "rainbow", "rainbowMorph", "static", "breathing", "runway", "meteor", "colorCycle", "staggered", "tide",
        "mixing", "voice", "door", "render", "ripple", "reflect", "tailChasing", "paint", "pingPong", "stack",
        "coverCycle", "wave", "racing", "lottery", "intertwine", "meteorShower", "collide", "electricCurrent",
        "kaleidoscope", "twinkle",
    };

    private static readonly string[] TlImplemented =
    {
        "rainbow", "rainbowMorph", "static", "breathing", "runway", "meteor", "colorCycle", "stack", "twinkle",
    };

    private static readonly string[] SlInfKeys =
    {
        "rainbow", "rainbowMorph", "static", "breathing", "runway", "meteor", "twinkle", "taichi", "mopUp",
        "meteorRainbow", "warning", "voice", "mixing", "tide", "scan", "door", "heartBeat", "heartBeatRunway",
        "disco", "electricCurrent", "reflect", "gradientRibbon", "wing", "drumming", "boomerang", "candyBox",
    };

    [Fact]
    public void Tl_catalog_has_29_keys_and_nine_generators()
    {
        Assert.Equal(TlKeys, FlexTlEffects.Keys);
        Assert.Equal(TlImplemented.OrderBy(k => k), TlKeys.Where(FlexTlEffects.IsImplemented).OrderBy(k => k));
    }

    [Fact]
    public void SlInf_catalog_has_26_keys_all_implemented()
    {
        Assert.Equal(SlInfKeys, FlexSlInfEffects.Keys);
        Assert.All(SlInfKeys, k => Assert.True(FlexSlInfEffects.IsImplemented(k)));
    }

    [Theory]
    [InlineData("staggered")]
    [InlineData("tide")]
    [InlineData("collide")]
    [InlineData("kaleidoscope")]
    public void Tl_effects_without_a_generator_throw_NotSupported(string key)
    {
        Assert.False(FlexTlEffects.IsImplemented(key));
        var req = new FlexEffectRequest(FlexScope.All, 1, 0, 255, Array.Empty<Nexus.Service.Peripherals.Hyte.Np50.RgbColor>());
        Assert.Throws<NotSupportedException>(() => FlexTlEffects.Render(key, req));
    }

    public static IEnumerable<object[]> WholeFanIds() =>
        FlexEffectVector.Ids(v => v.Kind != "composed" && !v.ZoneOnly && (v.Family == FlexFamily.SlInfFlex || FlexTlEffects.IsImplemented(v.Key)));

    public static IEnumerable<object[]> ZoneIds() => FlexEffectVector.Ids(v => v.ZoneOnly);

    private static FlexRawAnimation RenderVector(FlexEffectVector v, FlexScope scope)
    {
        var req = new FlexEffectRequest(scope, 1, v.Direction, v.BrightnessLevel, v.Colors, v.SpeedMultiplier, v.RightAttach);
        return v.Family == FlexFamily.TlFlex ? FlexTlEffects.Render(v.Key, req) : FlexSlInfEffects.Render(v.Key, req);
    }

    [Theory]
    [MemberData(nameof(WholeFanIds))]
    public void Whole_fan_render_matches_expected_frames(string id)
    {
        var v = FlexEffectVector.Get(id);
        var anim = RenderVector(v, FlexScope.All);
        Assert.Equal(v.Leds, anim.Leds);
        Assert.Equal(v.Frames, anim.FrameCount);
        Assert.Equal(v.IntervalMs, Math.Floor(anim.IntervalBaseMs * v.SpeedMultiplier * 100 + 1e-6) / 100, 2);
        Assert.Equal(v.Raw, anim.Frames);
    }

    [Theory]
    [MemberData(nameof(ZoneIds))]
    public void Zone_render_matches_expected_frames_inside_the_zone(string id)
    {
        var v = FlexEffectVector.Get(id);
        var scope = v.Scope switch
        {
            "up" => FlexScope.Up,
            "down" => FlexScope.Down,
            "inner" => FlexScope.Inner,
            "center" => FlexScope.Center,
            _ => FlexScope.Outer,
        };
        var anim = RenderVector(v, scope);
        Assert.Equal(v.Frames, anim.FrameCount);
        var zone = ZoneLeds(v.Family, scope);
        for (var f = 0; f < v.Frames; f++)
        {
            foreach (var led in zone)
            {
                for (var c = 0; c < 3; c++)
                {
                    var o = (f * v.Leds + led) * 3 + c;
                    Assert.True(v.Raw[o] == anim.Frames[o], $"{id} frame {f} led {led} channel {c}");
                }
            }
        }
    }

    internal static int[] ZoneLeds(FlexFamily family, FlexScope scope) => (family, scope) switch
    {
        (FlexFamily.TlFlex, FlexScope.Up) => Enumerable.Range(0, 13).ToArray(),
        (FlexFamily.TlFlex, FlexScope.Down) => Enumerable.Range(13, 13).ToArray(),
        (_, FlexScope.Center) => Enumerable.Range(0, 8).ToArray(),
        (_, FlexScope.Inner) => Enumerable.Range(8, 10).Concat(Enumerable.Range(26, 10)).ToArray(),
        (_, FlexScope.Outer) => Enumerable.Range(18, 8).Concat(Enumerable.Range(36, 8)).ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };
}
