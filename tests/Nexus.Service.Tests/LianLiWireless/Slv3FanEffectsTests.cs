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
}
