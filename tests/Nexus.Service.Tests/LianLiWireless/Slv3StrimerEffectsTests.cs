using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LianLiWireless;
using Xunit;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3StrimerEffectsTests
{
    private static readonly (int Lanes, int LedsPerLane)[] Geometries =
    {
        (4, 29), (4, 22), (6, 22), (6, 29),
    };

    private static readonly string[] ExpectedKeys =
    {
        "rainbow", "rainbowWave", "rainbowMorph", "static", "breathing", "wave", "painting",
        "colorTransfer", "fadeOut", "contest", "crossOver", "bulletStack", "twinkle", "parallel",
        "shockWave", "ripple", "voice", "drizzling", "endless", "shuttleRun", "river", "hourglass",
        "pioneer", "electricCurrent", "transformation", "gradientRibbon", "snooker", "mixing",
        "pingPong", "runway", "tide", "blowUp", "meteor", "stack",
    };

    [Fact]
    public void Catalog_has_the_34_keys_in_order()
    {
        Assert.Equal(ExpectedKeys, Slv3StrimerEffects.Catalog.Select(i => i.Key));
    }

    [Fact]
    public void Find_returns_info_for_known_key_and_null_for_unknown()
    {
        Assert.NotNull(Slv3StrimerEffects.Find("rainbow"));
        Assert.Null(Slv3StrimerEffects.Find("notARealEffect"));
    }

    public static IEnumerable<object[]> AllKeysAndGeometries()
    {
        foreach (var key in ExpectedKeys)
        {
            foreach (var (lanes, ledsPerLane) in Geometries)
            {
                yield return new object[] { key, lanes, ledsPerLane };
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllKeysAndGeometries))]
    public void Buffer_length_matches_frame_count_times_geometry(string key, int lanes, int ledsPerLane)
    {
        var anim = Slv3StrimerEffects.Render(key, lanes, ledsPerLane, 2, 0, Array.Empty<RgbColor>());
        Assert.Equal(anim.FrameCount * lanes * ledsPerLane * 3, anim.Frames.Length);
    }

    public static IEnumerable<object[]> AllKeysGeometriesAndSpeeds()
    {
        foreach (var key in ExpectedKeys)
        {
            foreach (var (lanes, ledsPerLane) in Geometries)
            {
                for (var speed = 0; speed < Slv3StrimerEffects.SpeedLevels; speed++)
                {
                    yield return new object[] { key, lanes, ledsPerLane, speed };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllKeysGeometriesAndSpeeds))]
    public void Every_key_geometry_and_speed_fits_the_wire_budget(string key, int lanes, int ledsPerLane, int speed)
    {
        var anim = Slv3StrimerEffects.Render(key, lanes, ledsPerLane, speed, 0, Array.Empty<RgbColor>());
        Assert.True(anim.FrameCount <= 2048);
        var compressed = TinyUz.Compress(anim.Frames);
        Assert.True(compressed.Length <= TinyUz.MaxCompressedLength,
            $"{key} at {lanes}x{ledsPerLane} speed {speed}: {compressed.Length} bytes compressed");
    }

    [Theory]
    [MemberData(nameof(ExpectedKeysMemberData))]
    public void Speed_4_is_faster_than_speed_0_when_the_effect_has_speed(string key)
    {
        var info = Slv3StrimerEffects.Find(key)!;
        if (!info.HasSpeed)
        {
            return;
        }
        var slow = Slv3StrimerEffects.Render(key, 6, 29, 0, 0, Array.Empty<RgbColor>());
        var fast = Slv3StrimerEffects.Render(key, 6, 29, 4, 0, Array.Empty<RgbColor>());
        Assert.True(fast.IntervalMs < slow.IntervalMs);
    }

    [Theory]
    [MemberData(nameof(ExpectedKeysMemberData))]
    public void Direction_1_differs_from_0_when_the_effect_has_direction(string key)
    {
        var info = Slv3StrimerEffects.Find(key)!;
        if (!info.HasDirection)
        {
            return;
        }
        var forward = Slv3StrimerEffects.Render(key, 6, 29, 2, 0, Array.Empty<RgbColor>());
        var backward = Slv3StrimerEffects.Render(key, 6, 29, 2, 1, Array.Empty<RgbColor>());
        Assert.False(forward.Frames.AsSpan().SequenceEqual(backward.Frames));
    }

    [Theory]
    [MemberData(nameof(ExpectedKeysMemberData))]
    public void A_pure_user_colour_reaches_the_output_when_the_effect_uses_colours(string key)
    {
        var info = Slv3StrimerEffects.Find(key)!;
        if (info.ColorsMax < 1)
        {
            return;
        }
        var pureRed = new RgbColor(255, 0, 0);
        var anim = Slv3StrimerEffects.Render(key, 6, 29, 2, 0, new[] { pureRed });
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

    public static IEnumerable<object[]> ExpectedKeysMemberData() => ExpectedKeys.Select(k => new object[] { k });

    [Fact]
    public void Render_is_deterministic()
    {
        var a = Slv3StrimerEffects.Render("twinkle", 6, 29, 3, 1, Slv3StrimerEffects.DefaultColors);
        var b = Slv3StrimerEffects.Render("twinkle", 6, 29, 3, 1, Slv3StrimerEffects.DefaultColors);
        Assert.Equal(a.FrameCount, b.FrameCount);
        Assert.Equal(a.IntervalMs, b.IntervalMs);
        Assert.Equal(a.Frames, b.Frames);
    }

    [Fact]
    public void Render_throws_for_unknown_key()
    {
        Assert.Throws<ArgumentException>(() =>
            Slv3StrimerEffects.Render("notARealEffect", 6, 29, 2, 0, Array.Empty<RgbColor>()));
    }

    [Theory]
    [InlineData(3, 29)]
    [InlineData(4, 30)]
    [InlineData(5, 22)]
    public void Render_throws_for_unsupported_geometry(int lanes, int ledsPerLane)
    {
        Assert.Throws<ArgumentException>(() =>
            Slv3StrimerEffects.Render("rainbow", lanes, ledsPerLane, 2, 0, Array.Empty<RgbColor>()));
    }

    [Fact]
    public void LaneEffectKeys_lists_the_six_single_lane_modes()
    {
        Assert.Equal(
            new[] { "rainbow", "wave", "static", "breathing", "rainbowMorph", "painting" },
            Slv3StrimerEffects.LaneEffectKeys);
    }

    public static IEnumerable<object[]> LaneGeometries() => Geometries.Select(g => new object[] { g.Lanes, g.LedsPerLane });

    [Theory]
    [MemberData(nameof(LaneGeometries))]
    public void RenderPerLane_fits_the_wire_budget(int lanes, int ledsPerLane)
    {
        var settings = new List<(string Key, int Direction, RgbColor Color)>();
        for (var i = 0; i < lanes; i++)
        {
            var key = Slv3StrimerEffects.LaneEffectKeys[i % Slv3StrimerEffects.LaneEffectKeys.Count];
            settings.Add((key, i % 2, Slv3StrimerEffects.DefaultColors[i % 6]));
        }
        var anim = Slv3StrimerEffects.RenderPerLane(lanes, ledsPerLane, 2, settings);
        Assert.Equal(anim.FrameCount * lanes * ledsPerLane * 3, anim.Frames.Length);
        Assert.True(anim.FrameCount <= 2048);
        var compressed = TinyUz.Compress(anim.Frames);
        Assert.True(compressed.Length <= TinyUz.MaxCompressedLength);
    }

    [Fact]
    public void RenderPerLane_isolates_each_lane_to_its_own_setting()
    {
        const int lanes = 6;
        const int ledsPerLane = 29;
        var baseline = new List<(string Key, int Direction, RgbColor Color)>();
        for (var i = 0; i < lanes; i++)
        {
            baseline.Add(("static", 0, new RgbColor(10, 20, 30)));
        }
        var changed = new List<(string Key, int Direction, RgbColor Color)>(baseline)
        {
            [2] = ("breathing", 1, new RgbColor(200, 100, 50)),
        };

        var a = Slv3StrimerEffects.RenderPerLane(lanes, ledsPerLane, 2, baseline);
        var b = Slv3StrimerEffects.RenderPerLane(lanes, ledsPerLane, 2, changed);

        var stride = ledsPerLane * 3;
        for (var lane = 0; lane < lanes; lane++)
        {
            var laneA = SliceLane(a, lanes, ledsPerLane, lane);
            var laneB = SliceLane(b, lanes, ledsPerLane, lane);
            if (lane == 2)
            {
                Assert.False(laneA.SequenceEqual(laneB));
            }
            else
            {
                Assert.True(laneA.SequenceEqual(laneB));
            }
        }
        _ = stride;
    }

    private static byte[] SliceLane(Slv3StrimerAnimation anim, int lanes, int ledsPerLane, int lane)
    {
        var laneStride = ledsPerLane * 3;
        var frameStride = lanes * laneStride;
        var result = new byte[anim.FrameCount * laneStride];
        for (var f = 0; f < anim.FrameCount; f++)
        {
            Array.Copy(anim.Frames, f * frameStride + lane * laneStride, result, f * laneStride, laneStride);
        }
        return result;
    }
}
