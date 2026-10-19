using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLiFlex;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexAnimationComposerTests
{
    private static readonly string[] RandomKeys = { "voice", "meteorShower", "kaleidoscope" };

    public static IEnumerable<object[]> WholeFanIds() =>
        FlexEffectVector.Ids(v => v.Kind is "default" or "variant" && !(v.Family == FlexFamily.TlFlex && RandomKeys.Contains(v.Key)));

    public static IEnumerable<object[]> ComposedIds() => FlexEffectVector.Ids(v => v.Kind == "composed");

    private static int BrightnessIndex(int percent) => percent / 25;

    private static void AssertMatches(FlexEffectVector v, FlexComposedAnimation anim)
    {
        Assert.Equal(v.Leds, anim.Leds);
        Assert.Equal(v.Frames, anim.Frames);
        Assert.Equal(v.IntervalMs, Math.Floor(anim.IntervalMs * 100 + 1e-6) / 100, 2);
        Assert.Equal(v.Raw, anim.Raw);
    }

    [Theory]
    [MemberData(nameof(WholeFanIds))]
    public void Whole_fan_look_matches_recorded_package(string id)
    {
        var v = FlexEffectVector.Get(id);
        var look = new FlexZoneLook(v.Key, 7 - v.SpeedMultiplier, v.Direction, BrightnessIndex(v.BrightnessPercent), v.Colors);
        var zones = new Dictionary<string, FlexZoneLook> { ["all"] = look };
        AssertMatches(v, FlexAnimationComposer.Compose(v.Family, 1, zones, false));
    }

    [Theory]
    [MemberData(nameof(ComposedIds))]
    public void Zone_looks_compose_to_recorded_package(string id)
    {
        var v = FlexEffectVector.Get(id);
        AssertMatches(v, FlexAnimationComposer.Compose(v.Family, 1, v.Zones!, v.UpDownSwap));
    }

    [Fact]
    public void Catalog_keys_per_family()
    {
        Assert.Equal(29, FlexAnimationComposer.Keys(FlexFamily.TlFlex).Count);
        Assert.Equal(26, FlexAnimationComposer.Keys(FlexFamily.SlInfFlex).Count);
    }

    [Fact]
    public void Up_down_swap_exchanges_the_halves_of_each_fan()
    {
        var v = FlexEffectVector.Get("tl-up-meteor");
        var zones = new Dictionary<string, FlexZoneLook>
        {
            ["up"] = new FlexZoneLook("meteor", 3, 0, 4, v.Colors),
            ["down"] = new FlexZoneLook("meteor", 3, 0, 4, v.Colors),
        };
        var plain = FlexAnimationComposer.Compose(FlexFamily.TlFlex, 1, zones, false);
        var swapped = FlexAnimationComposer.Compose(FlexFamily.TlFlex, 1, zones, true);
        Assert.Equal(plain.Frames, swapped.Frames);
        for (var f = 0; f < plain.Frames; f++)
        {
            for (var led = 0; led < 13; led++)
            {
                for (var c = 0; c < 3; c++)
                {
                    Assert.Equal(plain.Raw[(f * 26 + led) * 3 + c], swapped.Raw[(f * 26 + 13 + led) * 3 + c]);
                    Assert.Equal(plain.Raw[(f * 26 + 13 + led) * 3 + c], swapped.Raw[(f * 26 + led) * 3 + c]);
                }
            }
        }
    }
}
