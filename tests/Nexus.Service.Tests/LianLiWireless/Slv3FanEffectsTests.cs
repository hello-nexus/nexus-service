using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3FanEffectsTests
{
    private static readonly string[] SlKeys =
    {
        "rainbow", "rainbowMorph", "static", "breathing", "runway", "meteor", "colorCycle", "staggered",
        "tide", "mixing", "render", "pingPong", "stack", "ripple", "collide", "reflect", "electricCurrent",
        "endless", "river", "duel", "hourglass", "pioneer", "shuttleRun", "gradientRibbon", "twinkle",
    };

    [Fact]
    public void CatalogFor_returns_the_25_SL_keys_in_order_for_both_SL_families()
    {
        Assert.Equal(SlKeys, Slv3FanEffects.CatalogFor(Slv3FanFamily.Slv3Led).Select(i => i.Key));
        Assert.Equal(SlKeys, Slv3FanEffects.CatalogFor(Slv3FanFamily.Slv3Lcd).Select(i => i.Key));
    }

    [Fact]
    public void CatalogFor_returns_empty_for_Unknown()
    {
        Assert.Empty(Slv3FanEffects.CatalogFor(Slv3FanFamily.Unknown));
    }

    [Fact]
    public void Find_returns_info_for_known_key_and_null_for_unknown()
    {
        Assert.NotNull(Slv3FanEffects.Find(Slv3FanFamily.Slv3Led, "rainbow"));
        Assert.Null(Slv3FanEffects.Find(Slv3FanFamily.Slv3Led, "notARealEffect"));
        Assert.Null(Slv3FanEffects.Find(Slv3FanFamily.Unknown, "rainbow"));
    }

    public static IEnumerable<object[]> SlKeysAndFanCounts()
    {
        foreach (var key in SlKeys)
        {
            for (var fanCount = 1; fanCount <= Slv3FanEffects.MaxFans; fanCount++)
            {
                yield return new object[] { key, fanCount };
            }
        }
    }

    [Theory]
    [MemberData(nameof(SlKeysAndFanCounts))]
    public void Buffer_length_matches_frame_count_times_fan_geometry(string key, int fanCount)
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Slv3Led, key, fanCount, 2, 0, Array.Empty<RgbColor>());
        var ledsPerFan = Slv3Protocol.LedsPerFanFor(Slv3FanFamily.Slv3Led);
        Assert.Equal(anim.FrameCount * fanCount * ledsPerFan * 3, anim.Frames.Length);
    }

    public static IEnumerable<object[]> SlKeysFanCountsAndSpeeds()
    {
        foreach (var key in SlKeys)
        {
            for (var fanCount = 1; fanCount <= Slv3FanEffects.MaxFans; fanCount++)
            {
                for (var speed = 0; speed < Slv3StrimerEffects.SpeedLevels; speed++)
                {
                    yield return new object[] { key, fanCount, speed };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(SlKeysFanCountsAndSpeeds))]
    public void Every_key_fanCount_and_speed_fits_the_wire_budget(string key, int fanCount, int speed)
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Slv3Led, key, fanCount, speed, 0, Array.Empty<RgbColor>());
        Assert.True(anim.FrameCount <= 2048);
        var compressed = TinyUz.Compress(anim.Frames);
        Assert.True(compressed.Length <= TinyUz.MaxCompressedLength,
            $"{key} at {fanCount} fans speed {speed}: {compressed.Length} bytes compressed");
    }

    public static IEnumerable<object[]> SlKeysMemberData() => SlKeys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(SlKeysMemberData))]
    public void Speed_4_is_faster_than_speed_0_when_the_effect_has_speed(string key)
    {
        var info = Slv3FanEffects.Find(Slv3FanFamily.Slv3Led, key)!;
        if (!info.HasSpeed)
        {
            return;
        }
        var slow = Slv3FanEffects.Render(Slv3FanFamily.Slv3Led, key, 3, 0, 0, Array.Empty<RgbColor>());
        var fast = Slv3FanEffects.Render(Slv3FanFamily.Slv3Led, key, 3, 4, 0, Array.Empty<RgbColor>());
        Assert.True(fast.IntervalMs < slow.IntervalMs);
    }

    [Theory]
    [MemberData(nameof(SlKeysMemberData))]
    public void Direction_1_differs_from_0_when_the_effect_has_direction(string key)
    {
        var info = Slv3FanEffects.Find(Slv3FanFamily.Slv3Led, key)!;
        if (!info.HasDirection)
        {
            return;
        }
        var forward = Slv3FanEffects.Render(Slv3FanFamily.Slv3Led, key, 3, 2, 0, Array.Empty<RgbColor>());
        var backward = Slv3FanEffects.Render(Slv3FanFamily.Slv3Led, key, 3, 2, 1, Array.Empty<RgbColor>());
        Assert.False(forward.Frames.AsSpan().SequenceEqual(backward.Frames));
    }

    [Theory]
    [MemberData(nameof(SlKeysMemberData))]
    public void A_pure_user_colour_reaches_the_output_when_the_effect_uses_colours(string key)
    {
        var info = Slv3FanEffects.Find(Slv3FanFamily.Slv3Led, key)!;
        if (info.ColorsMax < 1)
        {
            return;
        }
        var pureRed = new RgbColor(255, 0, 0);
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Slv3Led, key, 3, 2, 0, new[] { pureRed });
        var found = false;
        for (var i = 0; i + 2 < anim.Frames.Length && !found; i += 3)
        {
            if (Math.Abs(anim.Frames[i] - 255) <= 2 && anim.Frames[i + 1] <= 2 && anim.Frames[i + 2] <= 2)
            {
                found = true;
            }
        }
        Assert.True(found, $"{key}: no pixel across the loop reached pure red");
    }

    [Fact]
    public void Render_is_deterministic()
    {
        var a = Slv3FanEffects.Render(Slv3FanFamily.Slv3Led, "twinkle", 4, 3, 1, Slv3StrimerEffects.DefaultColors);
        var b = Slv3FanEffects.Render(Slv3FanFamily.Slv3Led, "twinkle", 4, 3, 1, Slv3StrimerEffects.DefaultColors);
        Assert.Equal(a.FrameCount, b.FrameCount);
        Assert.Equal(a.IntervalMs, b.IntervalMs);
        Assert.Equal(a.Frames, b.Frames);
    }

    [Fact]
    public void Render_throws_for_unknown_key()
    {
        Assert.Throws<ArgumentException>(() =>
            Slv3FanEffects.Render(Slv3FanFamily.Slv3Led, "notARealEffect", 3, 2, 0, Array.Empty<RgbColor>()));
    }

    [Fact]
    public void Render_throws_for_Unknown_family()
    {
        Assert.Throws<ArgumentException>(() =>
            Slv3FanEffects.Render(Slv3FanFamily.Unknown, "rainbow", 3, 2, 0, Array.Empty<RgbColor>()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void Render_throws_for_out_of_range_fan_count(int fanCount)
    {
        Assert.Throws<ArgumentException>(() =>
            Slv3FanEffects.Render(Slv3FanFamily.Slv3Led, "rainbow", fanCount, 2, 0, Array.Empty<RgbColor>()));
    }

    [Fact]
    public void Different_fan_counts_scale_the_buffer_without_changing_per_fan_content_for_static()
    {
        var oneFan = Slv3FanEffects.Render(Slv3FanFamily.Slv3Led, "static", 1, 0, 0, new[] { new RgbColor(10, 20, 30) });
        var fourFans = Slv3FanEffects.Render(Slv3FanFamily.Slv3Led, "static", 4, 0, 0, new[] { new RgbColor(10, 20, 30) });
        var ledsPerFan = Slv3Protocol.LedsPerFanFor(Slv3FanFamily.Slv3Led);
        Assert.Equal(oneFan.Frames, fourFans.Frames.Take(ledsPerFan * 3).ToArray());
    }

    private static readonly string[] TlKeys =
    {
        "rainbow", "rainbowMorph", "static", "breathing", "runway", "meteor", "colorCycle", "staggered",
        "tide", "mixing", "voice", "door", "render", "ripple", "reflect", "tailChasing", "paint", "pingPong",
        "stack", "coverCycle", "wave", "racing", "lottery", "intertwine", "meteorShower", "collide",
        "electricCurrent", "kaleidoscope", "twinkle",
    };

    [Fact]
    public void CatalogFor_returns_the_29_TL_keys_in_order_for_both_TL_families()
    {
        Assert.Equal(TlKeys, Slv3FanEffects.CatalogFor(Slv3FanFamily.Tlv2Led).Select(i => i.Key));
        Assert.Equal(TlKeys, Slv3FanEffects.CatalogFor(Slv3FanFamily.Tlv2Lcd).Select(i => i.Key));
    }

    public static IEnumerable<object[]> TlKeysAndFanCounts()
    {
        foreach (var key in TlKeys)
        {
            for (var fanCount = 1; fanCount <= Slv3FanEffects.MaxFans; fanCount++)
            {
                yield return new object[] { key, fanCount };
            }
        }
    }

    [Theory]
    [MemberData(nameof(TlKeysAndFanCounts))]
    public void TL_buffer_length_matches_frame_count_times_fan_geometry(string key, int fanCount)
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, key, fanCount, 2, 0, Array.Empty<RgbColor>());
        var ledsPerFan = Slv3Protocol.LedsPerFanFor(Slv3FanFamily.Tlv2Led);
        Assert.Equal(anim.FrameCount * fanCount * ledsPerFan * 3, anim.Frames.Length);
    }

    public static IEnumerable<object[]> TlKeysFanCountsAndSpeeds()
    {
        foreach (var key in TlKeys)
        {
            for (var fanCount = 1; fanCount <= Slv3FanEffects.MaxFans; fanCount++)
            {
                for (var speed = 0; speed < Slv3StrimerEffects.SpeedLevels; speed++)
                {
                    yield return new object[] { key, fanCount, speed };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(TlKeysFanCountsAndSpeeds))]
    public void Every_TL_key_fanCount_and_speed_fits_the_wire_budget(string key, int fanCount, int speed)
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, key, fanCount, speed, 0, Array.Empty<RgbColor>());
        Assert.True(anim.FrameCount <= 2048);
        var compressed = TinyUz.Compress(anim.Frames);
        Assert.True(compressed.Length <= TinyUz.MaxCompressedLength,
            $"{key} at {fanCount} fans speed {speed}: {compressed.Length} bytes compressed");
    }

    public static IEnumerable<object[]> TlKeysMemberData() => TlKeys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(TlKeysMemberData))]
    public void TL_speed_4_is_faster_than_speed_0_when_the_effect_has_speed(string key)
    {
        var info = Slv3FanEffects.Find(Slv3FanFamily.Tlv2Led, key)!;
        if (!info.HasSpeed)
        {
            return;
        }
        var slow = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, key, 3, 0, 0, Array.Empty<RgbColor>());
        var fast = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, key, 3, 4, 0, Array.Empty<RgbColor>());
        Assert.True(fast.IntervalMs < slow.IntervalMs);
    }

    [Theory]
    [MemberData(nameof(TlKeysMemberData))]
    public void TL_direction_1_differs_from_0_when_the_effect_has_direction(string key)
    {
        var info = Slv3FanEffects.Find(Slv3FanFamily.Tlv2Led, key)!;
        if (!info.HasDirection)
        {
            return;
        }
        var forward = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, key, 3, 2, 0, Array.Empty<RgbColor>());
        var backward = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, key, 3, 2, 1, Array.Empty<RgbColor>());
        Assert.False(forward.Frames.AsSpan().SequenceEqual(backward.Frames));
    }

    [Theory]
    [MemberData(nameof(TlKeysMemberData))]
    public void TL_a_pure_user_colour_reaches_the_output_when_the_effect_uses_colours(string key)
    {
        var info = Slv3FanEffects.Find(Slv3FanFamily.Tlv2Led, key)!;
        if (info.ColorsMax < 1)
        {
            return;
        }
        var pureRed = new RgbColor(255, 0, 0);
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, key, 3, 2, 0, new[] { pureRed });
        var found = false;
        for (var i = 0; i + 2 < anim.Frames.Length && !found; i += 3)
        {
            if (Math.Abs(anim.Frames[i] - 255) <= 2 && anim.Frames[i + 1] <= 2 && anim.Frames[i + 2] <= 2)
            {
                found = true;
            }
        }
        Assert.True(found, $"{key}: no pixel across the loop reached pure red");
    }

    [Fact]
    public void TL_render_is_deterministic()
    {
        var a = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "twinkle", 4, 3, 1, Slv3StrimerEffects.DefaultColors);
        var b = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "twinkle", 4, 3, 1, Slv3StrimerEffects.DefaultColors);
        Assert.Equal(a.FrameCount, b.FrameCount);
        Assert.Equal(a.IntervalMs, b.IntervalMs);
        Assert.Equal(a.Frames, b.Frames);
    }

    // ── TL effects drawn the way L-Connect draws them ───────────────────────────

    private static readonly RgbColor[] FourColours =
    {
        new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(255, 255, 0),
    };

    private static RgbColor TlLed(Slv3StrimerAnimation anim, int frame, int fanCount, int fan, int led)
    {
        var ledsPerFan = Slv3Protocol.LedsPerFanFor(Slv3FanFamily.Tlv2Led);
        var o = ((frame * fanCount + fan) * ledsPerFan + led) * 3;
        return new RgbColor(anim.Frames[o], anim.Frames[o + 1], anim.Frames[o + 2]);
    }

    [Fact]
    public void TL_static_gives_each_fan_its_own_colour()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "static", 4, 2, 0, FourColours);
        for (var fan = 0; fan < 4; fan++)
        {
            for (var led = 0; led < 26; led++)
            {
                Assert.Equal(FourColours[fan], TlLed(anim, 0, 4, fan, led));
            }
        }
    }

    [Fact]
    public void TL_breathing_keeps_each_fan_in_its_own_colour()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "breathing", 4, 2, 0, FourColours);
        var peak = new int[4];
        for (var f = 0; f < anim.FrameCount; f++)
        {
            var red = TlLed(anim, f, 4, 0, 0);
            var green = TlLed(anim, f, 4, 1, 0);
            var blue = TlLed(anim, f, 4, 2, 0);
            var yellow = TlLed(anim, f, 4, 3, 0);
            Assert.True(red.G == 0 && red.B == 0);
            Assert.True(green.R == 0 && green.B == 0);
            Assert.True(blue.R == 0 && blue.G == 0);
            Assert.True(yellow.R == yellow.G && yellow.B == 0);
            peak[0] = Math.Max(peak[0], red.R);
            peak[1] = Math.Max(peak[1], green.G);
            peak[2] = Math.Max(peak[2], blue.B);
            peak[3] = Math.Max(peak[3], yellow.R);
        }
        Assert.All(peak, p => Assert.True(p >= 250));
    }

    [Fact]
    public void TL_colours_brighter_than_LConnect_allows_are_stepped_down()
    {
        Assert.Equal(new RgbColor(195, 195, 195), Slv3FanEffects.CapColourSum(new RgbColor(255, 255, 255)));
        Assert.Equal(new RgbColor(255, 0, 0), Slv3FanEffects.CapColourSum(new RgbColor(255, 0, 0)));
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "static", 1, 2, 0, new[] { new RgbColor(255, 255, 255) });
        Assert.Equal(new RgbColor(195, 195, 195), TlLed(anim, 0, 1, 0, 0));
    }

    [Theory]
    [InlineData(Slv3FanScope.All, true, true)]
    [InlineData(Slv3FanScope.Top, true, false)]
    [InlineData(Slv3FanScope.Bottom, false, true)]
    public void TL_scope_lights_the_chosen_half_of_each_fan(Slv3FanScope scope, bool topLit, bool bottomLit)
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "static", 2, 2, 0, FourColours, scope: scope);
        for (var fan = 0; fan < 2; fan++)
        {
            Assert.Equal(topLit ? FourColours[fan] : default, TlLed(anim, 0, 2, fan, 0));
            Assert.Equal(topLit ? FourColours[fan] : default, TlLed(anim, 0, 2, fan, 12));
            Assert.Equal(bottomLit ? FourColours[fan] : default, TlLed(anim, 0, 2, fan, 13));
            Assert.Equal(bottomLit ? FourColours[fan] : default, TlLed(anim, 0, 2, fan, 25));
        }
    }

    [Fact]
    public void A_scope_keeps_any_TL_effect_to_one_half()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "door", 1, 2, 0, FourColours, scope: Slv3FanScope.Top);
        for (var f = 0; f < anim.FrameCount; f++)
        {
            for (var led = 13; led < 26; led++)
            {
                Assert.Equal(default, TlLed(anim, f, 1, 0, led));
            }
        }
    }

    [Theory]
    [InlineData(0, 77.0)]
    [InlineData(2, 55.0)]
    [InlineData(4, 33.0)]
    public void TL_rainbow_steps_at_LConnect_speeds(int speed, double intervalMs)
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "rainbow", 1, speed, 0, Array.Empty<RgbColor>());
        Assert.Equal(13, anim.FrameCount);
        Assert.Equal(intervalMs, anim.IntervalMs, 6);
    }

    [Fact]
    public void TL_rainbow_runs_one_colour_wheel_along_the_chain()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "rainbow", 1, 2, 1, Array.Empty<RgbColor>());
        Assert.Equal(new RgbColor(255, 0, 0), TlLed(anim, 0, 1, 0, 0));
        Assert.Equal(new RgbColor(204, 51, 0), TlLed(anim, 0, 1, 0, 1));
        Assert.Equal(new RgbColor(0, 255, 0), TlLed(anim, 0, 1, 0, 5));
        Assert.Equal(new RgbColor(0, 0, 255), TlLed(anim, 0, 1, 0, 10));
        // One frame later the wheel has moved on by one LED.
        Assert.Equal(new RgbColor(204, 51, 0), TlLed(anim, 1, 1, 0, 0));
    }

    [Fact]
    public void TL_runway_runs_along_the_chain_and_back()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "runway", 1, 2, 0, FourColours);
        Assert.Equal(2 * (13 + 2 - 1), anim.FrameCount);
        Assert.Equal(FourColours[0], TlLed(anim, 0, 1, 0, 0));
        Assert.Equal(FourColours[1], TlLed(anim, 0, 1, 0, 1));
        Assert.Equal(FourColours[0], TlLed(anim, 14, 1, 0, 12));
    }

    [Fact]
    public void TL_stack_keeps_the_pile_while_the_next_block_slides()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "stack", 1, 2, 1, FourColours);
        Assert.Equal(4 * (91 + 13), anim.FrameCount);
        // Frame 13 starts the second block: it enters at the first LED while the first one stays piled at the end.
        Assert.Equal(FourColours[0], TlLed(anim, 13, 1, 0, 0));
        Assert.Equal(default, TlLed(anim, 13, 1, 0, 11));
        Assert.Equal(FourColours[0], TlLed(anim, 13, 1, 0, 12));
    }

    [Fact]
    public void TL_color_cycle_blocks_differ_between_the_halves_as_in_LConnect()
    {
        var top = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "colorCycle", 1, 2, 1, FourColours, scope: Slv3FanScope.Top);
        var bottom = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "colorCycle", 1, 2, 1, FourColours, scope: Slv3FanScope.Bottom);
        Assert.Equal(FourColours[1], TlLed(top, 0, 1, 0, 3));
        Assert.Equal(FourColours[1], TlLed(bottom, 0, 1, 0, 13 + 4));
    }

    [Fact]
    public void TL_twinkle_flashes_every_LED_once_a_loop()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "twinkle", 1, 2, 0, FourColours);
        Assert.Equal(200, anim.FrameCount);
        for (var led = 0; led < 26; led++)
        {
            Assert.Equal(19, Enumerable.Range(0, anim.FrameCount).Count(f => TlLed(anim, f, 1, 0, led) != default));
        }
    }

    // ── TL effects from L-Connect's lighting library ─────────────────────────────

    private static readonly RgbColor PureRed = new(255, 0, 0);
    private static readonly RgbColor PureGreen = new(0, 255, 0);

    [Theory]
    [InlineData(0, 4)]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    public void TL_staggered_steps_through_as_many_colours_as_chosen(int chosen, int colours)
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "staggered", 1, 2, 0, FourColours.Take(chosen).ToArray());
        Assert.Equal(colours * 24, anim.FrameCount);
    }

    [Fact]
    public void TL_staggered_lights_the_halves_crosswise_from_fan_to_fan()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "staggered", 2, 2, 0, FourColours);
        Assert.Equal(FourColours[0], TlLed(anim, 11, 2, 0, 0));
        Assert.Equal(default, TlLed(anim, 11, 2, 0, 13));
        Assert.Equal(default, TlLed(anim, 11, 2, 1, 0));
        Assert.Equal(FourColours[0], TlLed(anim, 11, 2, 1, 13));
        // Twelve frames later the other halves take their turn.
        Assert.Equal(default, TlLed(anim, 23, 2, 0, 0));
        Assert.Equal(FourColours[0], TlLed(anim, 23, 2, 0, 13));
    }

    [Theory]
    [InlineData("staggered")]
    [InlineData("tide")]
    [InlineData("door")]
    [InlineData("render")]
    [InlineData("ripple")]
    [InlineData("reflect")]
    [InlineData("paint")]
    [InlineData("pingPong")]
    [InlineData("coverCycle")]
    [InlineData("wave")]
    [InlineData("collide")]
    [InlineData("electricCurrent")]
    public void TL_colour_stepping_effects_use_only_the_chosen_colours(string key)
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, key, 1, 2, 0, new[] { PureRed, PureGreen });
        bool red = false, green = false;
        for (var f = 0; f < anim.FrameCount; f++)
        {
            for (var led = 0; led < 26; led++)
            {
                var c = TlLed(anim, f, 1, 0, led);
                Assert.True(c.B == 0 && (c.R == 0 || c.G == 0), $"{key}: frame {f} LED {led} is {c}");
                red |= c == PureRed;
                green |= c == PureGreen;
            }
        }
        Assert.True(red && green, $"{key}: both colours should show at full");
    }

    [Fact]
    public void TL_tide_flows_in_from_both_ends_over_the_last_colour()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "tide", 1, 2, 0, FourColours);
        Assert.Equal(4 * 10, anim.FrameCount);
        Assert.Equal(FourColours[0], TlLed(anim, 0, 1, 0, 0));
        Assert.Equal(FourColours[0], TlLed(anim, 0, 1, 0, 12));
        Assert.Equal(FourColours[3], TlLed(anim, 0, 1, 0, 6));
        Assert.Equal(FourColours[0], TlLed(anim, 9, 1, 0, 6));
    }

    [Fact]
    public void TL_render_goes_round_the_chain_one_half_at_a_time()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "render", 2, 2, 0, FourColours);
        // Top halves first, fan after fan, 13 frames each; then the bottom halves from the last fan back.
        Assert.Equal(FourColours[1], TlLed(anim, 25, 2, 1, 0));
        Assert.Equal(FourColours[0], TlLed(anim, 25, 2, 1, 13));
        Assert.Equal(FourColours[1], TlLed(anim, 38, 2, 1, 13));
        Assert.Equal(FourColours[0], TlLed(anim, 38, 2, 0, 13));
        Assert.Equal(FourColours[1], TlLed(anim, 51, 2, 0, 13));
    }

    [Fact]
    public void TL_mixing_bursts_out_in_the_two_colours_mixed()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "mixing", 1, 2, 0, new[] { PureRed, PureGreen });
        Assert.Contains(Enumerable.Range(0, anim.FrameCount), f => TlLed(anim, f, 1, 0, 6) == new RgbColor(255, 255, 0));
    }

    [Fact]
    public void TL_tail_chasing_runs_round_the_top_and_then_the_bottom_half()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "tailChasing", 1, 2, 0, new[] { PureRed, PureGreen });
        Assert.Equal(24, anim.FrameCount);
        // The first colour's head starts on the top half, the second's half a loop on, on the bottom half.
        Assert.Equal(PureRed, TlLed(anim, 0, 1, 0, 2));
        Assert.Equal(PureGreen, TlLed(anim, 0, 1, 0, 23));
        // Half a loop later they have swapped halves.
        Assert.Equal(PureRed, TlLed(anim, 12, 1, 0, 23));
        Assert.Equal(PureGreen, TlLed(anim, 12, 1, 0, 2));
    }

    [Fact]
    public void TL_racing_comets_cross_to_the_other_half_at_every_fan()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "racing", 2, 2, 0, new[] { PureRed, PureGreen });
        for (var f = 0; f < anim.FrameCount; f++)
        {
            for (var i = 0; i < 13; i++)
            {
                Assert.Equal(0, TlLed(anim, f, 2, 0, i).G);
                Assert.Equal(0, TlLed(anim, f, 2, 0, 13 + i).R);
                Assert.Equal(0, TlLed(anim, f, 2, 1, i).R);
                Assert.Equal(0, TlLed(anim, f, 2, 1, 13 + i).G);
            }
        }
    }

    [Fact]
    public void TL_collide_steps_at_16_ms_times_the_speed_level()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, "collide", 1, 2, 0, FourColours);
        Assert.Equal(80.0, anim.IntervalMs, 6);
    }

    [Theory]
    [InlineData("voice")]
    [InlineData("meteorShower")]
    [InlineData("kaleidoscope")]
    public void TL_effects_that_pick_their_own_colours_ignore_the_user_colours(string key)
    {
        Assert.Equal(0, Slv3FanEffects.Find(Slv3FanFamily.Tlv2Led, key)!.ColorsMax);
        var a = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, key, 2, 2, 0, FourColours);
        var b = Slv3FanEffects.Render(Slv3FanFamily.Tlv2Led, key, 2, 2, 0, new[] { new RgbColor(255, 255, 255) });
        Assert.Equal(a.Frames, b.Frames);
    }

    [Fact]
    public void Only_TL_effects_offer_a_scope()
    {
        Assert.All(Slv3FanEffects.CatalogFor(Slv3FanFamily.Slv3Led), info => Assert.False(info.HasScope));
        Assert.All(Slv3FanEffects.CatalogFor(Slv3FanFamily.SlInf), info => Assert.False(info.HasScope));
        Assert.All(Slv3FanEffects.CatalogFor(Slv3FanFamily.Tlv2Lcd), info => Assert.True(info.HasScope));
    }

    private static readonly string[] SlInfKeys =
    {
        "rainbow", "rainbowMorph", "static", "breathing", "runway", "meteor", "twinkle", "taichi",
        "colorCycle", "mopUp", "meteorRainbow", "colorfulMeteor", "lottery", "warning", "voice", "mixing",
        "tide", "scan", "doubleMeteor", "meteorContest", "meteorMix", "returnArc", "doubleArc", "door",
        "heartBeat", "heartBeatRunway", "disco", "electricCurrent", "reflect", "gradientRibbon", "wing",
        "drumming", "boomerang", "candyBox",
    };

    [Fact]
    public void CatalogFor_returns_the_34_SlInf_keys_in_order()
    {
        Assert.Equal(SlInfKeys, Slv3FanEffects.CatalogFor(Slv3FanFamily.SlInf).Select(i => i.Key));
    }

    public static IEnumerable<object[]> SlInfKeysAndFanCounts()
    {
        foreach (var key in SlInfKeys)
        {
            for (var fanCount = 1; fanCount <= Slv3FanEffects.MaxFans; fanCount++)
            {
                yield return new object[] { key, fanCount };
            }
        }
    }

    [Theory]
    [MemberData(nameof(SlInfKeysAndFanCounts))]
    public void SlInf_buffer_length_matches_frame_count_times_fan_geometry(string key, int fanCount)
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.SlInf, key, fanCount, 2, 0, Array.Empty<RgbColor>());
        var ledsPerFan = Slv3Protocol.LedsPerFanFor(Slv3FanFamily.SlInf);
        Assert.Equal(anim.FrameCount * fanCount * ledsPerFan * 3, anim.Frames.Length);
    }

    public static IEnumerable<object[]> SlInfKeysFanCountsAndSpeeds()
    {
        foreach (var key in SlInfKeys)
        {
            for (var fanCount = 1; fanCount <= Slv3FanEffects.MaxFans; fanCount++)
            {
                for (var speed = 0; speed < Slv3StrimerEffects.SpeedLevels; speed++)
                {
                    yield return new object[] { key, fanCount, speed };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(SlInfKeysFanCountsAndSpeeds))]
    public void Every_SlInf_key_fanCount_and_speed_fits_the_wire_budget(string key, int fanCount, int speed)
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.SlInf, key, fanCount, speed, 0, Array.Empty<RgbColor>());
        Assert.True(anim.FrameCount <= 2048);
        var compressed = TinyUz.Compress(anim.Frames);
        Assert.True(compressed.Length <= TinyUz.MaxCompressedLength,
            $"{key} at {fanCount} fans speed {speed}: {compressed.Length} bytes compressed");
    }

    public static IEnumerable<object[]> SlInfKeysMemberData() => SlInfKeys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(SlInfKeysMemberData))]
    public void SlInf_speed_4_is_faster_than_speed_0_when_the_effect_has_speed(string key)
    {
        var info = Slv3FanEffects.Find(Slv3FanFamily.SlInf, key)!;
        if (!info.HasSpeed)
        {
            return;
        }
        var slow = Slv3FanEffects.Render(Slv3FanFamily.SlInf, key, 3, 0, 0, Array.Empty<RgbColor>());
        var fast = Slv3FanEffects.Render(Slv3FanFamily.SlInf, key, 3, 4, 0, Array.Empty<RgbColor>());
        Assert.True(fast.IntervalMs < slow.IntervalMs);
    }

    [Theory]
    [MemberData(nameof(SlInfKeysMemberData))]
    public void SlInf_direction_1_differs_from_0_when_the_effect_has_direction(string key)
    {
        var info = Slv3FanEffects.Find(Slv3FanFamily.SlInf, key)!;
        if (!info.HasDirection)
        {
            return;
        }
        var forward = Slv3FanEffects.Render(Slv3FanFamily.SlInf, key, 3, 2, 0, Array.Empty<RgbColor>());
        var backward = Slv3FanEffects.Render(Slv3FanFamily.SlInf, key, 3, 2, 1, Array.Empty<RgbColor>());
        Assert.False(forward.Frames.AsSpan().SequenceEqual(backward.Frames));
    }

    [Theory]
    [MemberData(nameof(SlInfKeysMemberData))]
    public void SlInf_a_pure_user_colour_reaches_the_output_when_the_effect_uses_colours(string key)
    {
        var info = Slv3FanEffects.Find(Slv3FanFamily.SlInf, key)!;
        if (info.ColorsMax < 1)
        {
            return;
        }
        var pureRed = new RgbColor(255, 0, 0);
        var anim = Slv3FanEffects.Render(Slv3FanFamily.SlInf, key, 3, 2, 0, new[] { pureRed });
        var found = false;
        for (var i = 0; i + 2 < anim.Frames.Length && !found; i += 3)
        {
            if (Math.Abs(anim.Frames[i] - 255) <= 2 && anim.Frames[i + 1] <= 2 && anim.Frames[i + 2] <= 2)
            {
                found = true;
            }
        }
        Assert.True(found, $"{key}: no pixel across the loop reached pure red");
    }

    [Fact]
    public void SlInf_render_is_deterministic()
    {
        var a = Slv3FanEffects.Render(Slv3FanFamily.SlInf, "candyBox", 4, 3, 1, Slv3StrimerEffects.DefaultColors);
        var b = Slv3FanEffects.Render(Slv3FanFamily.SlInf, "candyBox", 4, 3, 1, Slv3StrimerEffects.DefaultColors);
        Assert.Equal(a.FrameCount, b.FrameCount);
        Assert.Equal(a.IntervalMs, b.IntervalMs);
        Assert.Equal(a.Frames, b.Frames);
    }

    [Fact]
    public void CatalogFor_Cl_matches_SlInf_key_list()
    {
        Assert.Equal(SlInfKeys, Slv3FanEffects.CatalogFor(Slv3FanFamily.Cl).Select(i => i.Key));
    }

    public static IEnumerable<object[]> ClKeysAndFanCounts()
    {
        foreach (var key in SlInfKeys)
        {
            for (var fanCount = 1; fanCount <= Slv3FanEffects.MaxFans; fanCount++)
            {
                yield return new object[] { key, fanCount };
            }
        }
    }

    [Theory]
    [MemberData(nameof(ClKeysAndFanCounts))]
    public void Cl_buffer_length_matches_frame_count_times_fan_geometry(string key, int fanCount)
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Cl, key, fanCount, 2, 0, Array.Empty<RgbColor>());
        var ledsPerFan = Slv3Protocol.LedsPerFanFor(Slv3FanFamily.Cl);
        Assert.Equal(anim.FrameCount * fanCount * ledsPerFan * 3, anim.Frames.Length);
    }

    public static IEnumerable<object[]> ClKeysFanCountsAndSpeeds()
    {
        foreach (var key in SlInfKeys)
        {
            for (var fanCount = 1; fanCount <= Slv3FanEffects.MaxFans; fanCount++)
            {
                for (var speed = 0; speed < Slv3StrimerEffects.SpeedLevels; speed++)
                {
                    yield return new object[] { key, fanCount, speed };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(ClKeysFanCountsAndSpeeds))]
    public void Every_Cl_key_fanCount_and_speed_fits_the_wire_budget(string key, int fanCount, int speed)
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Cl, key, fanCount, speed, 0, Array.Empty<RgbColor>());
        Assert.True(anim.FrameCount <= 2048);
        var compressed = TinyUz.Compress(anim.Frames);
        Assert.True(compressed.Length <= TinyUz.MaxCompressedLength,
            $"{key} at {fanCount} fans speed {speed}: {compressed.Length} bytes compressed");
    }

    [Fact]
    public void Cl_render_is_deterministic()
    {
        var a = Slv3FanEffects.Render(Slv3FanFamily.Cl, "candyBox", 4, 3, 1, Slv3StrimerEffects.DefaultColors);
        var b = Slv3FanEffects.Render(Slv3FanFamily.Cl, "candyBox", 4, 3, 1, Slv3StrimerEffects.DefaultColors);
        Assert.Equal(a.FrameCount, b.FrameCount);
        Assert.Equal(a.IntervalMs, b.IntervalMs);
        Assert.Equal(a.Frames, b.Frames);
    }

    private static readonly RgbColor[] Red = { new(255, 0, 0) };

    // The runway marker is at least half the colour; the rest of the ring sits dim.
    private static bool MarkerOn(Slv3StrimerAnimation anim, int frame, int fan, int fanCount)
    {
        var ledsPerFan = Slv3Protocol.LedsPerFanFor(Slv3FanFamily.Slv3Lcd);
        var offset = frame * fanCount * ledsPerFan * 3 + fan * ledsPerFan * 3;
        for (var led = 0; led < ledsPerFan; led++)
        {
            if (anim.Frames[offset + led * 3] > 100) return true;
        }
        return false;
    }

    [Fact]
    public void Merged_runway_carries_one_marker_from_fan_to_fan()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Slv3Lcd, "runway", 3, 2, 0, Red, merge: true);

        Assert.True(MarkerOn(anim, 0, 0, 3));
        Assert.False(MarkerOn(anim, 0, 1, 3));
        Assert.True(MarkerOn(anim, anim.FrameCount / 2, 1, 3));
        Assert.False(MarkerOn(anim, anim.FrameCount / 2, 0, 3));
        Assert.True(MarkerOn(anim, anim.FrameCount - 1, 2, 3));
        for (var f = 0; f < anim.FrameCount; f++)
        {
            var lit = Enumerable.Range(0, 3).Count(fan => MarkerOn(anim, f, fan, 3));
            Assert.InRange(lit, 1, 2); // two only while the marker crosses a fan boundary
        }
    }

    private static int LitLeds(Slv3StrimerAnimation anim, int frame, int fan, int fanCount)
    {
        var ledsPerFan = Slv3Protocol.LedsPerFanFor(Slv3FanFamily.Slv3Lcd);
        var offset = frame * fanCount * ledsPerFan * 3 + fan * ledsPerFan * 3;
        var lit = 0;
        for (var led = 0; led < ledsPerFan; led++)
        {
            if (anim.Frames[offset + led * 3] > 100) lit++;
        }
        return lit;
    }

    [Fact]
    public void Merged_tide_fills_the_chain_fan_by_fan()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Slv3Lcd, "tide", 3, 2, 0, Red, merge: true);
        var ledsPerFan = Slv3Protocol.LedsPerFanFor(Slv3FanFamily.Slv3Lcd);

        Assert.All(Enumerable.Range(0, 3), fan => Assert.Equal(0, LitLeds(anim, 0, fan, 3)));
        Assert.All(Enumerable.Range(0, 3), fan => Assert.Equal(ledsPerFan, LitLeds(anim, anim.FrameCount / 2, fan, 3)));
        // A quarter into the loop the wash is half the chain: fan 0 full, fan 2 still dark.
        var quarter = anim.FrameCount / 4;
        Assert.Equal(ledsPerFan, LitLeds(anim, quarter, 0, 3));
        Assert.Equal(0, LitLeds(anim, quarter, 2, 3));
    }

    [Fact]
    public void Runway_without_merge_sweeps_every_fan_at_once()
    {
        var anim = Slv3FanEffects.Render(Slv3FanFamily.Slv3Lcd, "runway", 3, 2, 0, Red);

        Assert.All(Enumerable.Range(0, 3), fan => Assert.True(MarkerOn(anim, 0, fan, 3)));
    }

    [Fact]
    public void Merge_is_ignored_for_an_effect_without_a_merged_variant()
    {
        var merged = Slv3FanEffects.Render(Slv3FanFamily.Slv3Lcd, "meteor", 3, 2, 0, Red, merge: true);
        var plain = Slv3FanEffects.Render(Slv3FanFamily.Slv3Lcd, "meteor", 3, 2, 0, Red);

        Assert.Equal(plain.Frames, merged.Frames);
    }
}
