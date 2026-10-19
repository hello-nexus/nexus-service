using System.Collections.Generic;
using Nexus.Service.Peripherals.LianLiFlex;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexRandomizedEffectsTests
{
    private const int Speed = 3;

    private static FlexComposedAnimation Compose(string key, int fans)
    {
        var zones = new Dictionary<string, FlexZoneLook>
        {
            ["all"] = new FlexZoneLook(key, Speed, 0, 4, new List<Nexus.Service.Peripherals.Hyte.Np50.RgbColor>()),
        };
        return FlexAnimationComposer.Compose(FlexFamily.TlFlex, fans, zones, false);
    }

    [Theory]
    [InlineData("voice", 65, 94)]
    [InlineData("meteorShower", 62, 89)]
    [InlineData("kaleidoscope", 13, 4096)]
    public void One_fan_frame_count_stays_in_the_effect_band(string key, int min, int max)
    {
        var anim = Compose(key, 1);
        Assert.InRange(anim.Frames, min, max);
    }

    [Theory]
    [InlineData("voice")]
    [InlineData("meteorShower")]
    [InlineData("kaleidoscope")]
    public void Interval_is_the_base_times_the_speed_multiplier(string key)
    {
        var anim = Compose(key, 1);
        var expected = FlexTlExtendedEffects.IntervalBaseFor(key) * FlexAnimationComposer.SpeedMultiplier(key, Speed);
        Assert.Equal(expected, anim.IntervalMs, 6);
    }

    [Theory]
    [InlineData("voice", 1)]
    [InlineData("voice", 4)]
    [InlineData("meteorShower", 2)]
    [InlineData("kaleidoscope", 3)]
    public void Led_count_is_the_ring_size_times_fans_and_the_buffer_matches(string key, int fans)
    {
        var anim = Compose(key, fans);
        Assert.Equal(FlexTlEffects.LedsPerFan * fans, anim.Leds);
        Assert.Equal(anim.Frames * anim.Leds * 3, anim.Raw.Length);
    }

    [Theory]
    [InlineData("voice")]
    [InlineData("meteorShower")]
    [InlineData("kaleidoscope")]
    public void A_fixed_seed_renders_the_same_animation_every_time(string key)
    {
        var first = Compose(key, 2);
        var second = Compose(key, 2);
        Assert.Equal(first.Frames, second.Frames);
        Assert.Equal(first.Raw, second.Raw);
    }

    [Fact]
    public void An_sl_inf_attach_flag_of_zero_changes_the_output_of_the_effects_that_read_it()
    {
        var differing = 0;
        foreach (var key in FlexSlInfEffects.Keys)
        {
            var zones = new Dictionary<string, FlexZoneLook>
            {
                ["all"] = new FlexZoneLook(key, 3, 0, 4, new List<Nexus.Service.Peripherals.Hyte.Np50.RgbColor>()),
            };
            var a = FlexAnimationComposer.Compose(FlexFamily.SlInfFlex, 1, zones, false, 1);
            var b = FlexAnimationComposer.Compose(FlexFamily.SlInfFlex, 1, zones, false, 0);
            if (!System.MemoryExtensions.SequenceEqual<byte>(a.Raw, b.Raw))
            {
                differing++;
            }
        }
        Assert.InRange(differing, 1, FlexSlInfEffects.Keys.Count - 1);
    }
}
