using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Nexus.Service.Peripherals.LianLiFlex;
using Xunit;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexTlExtendedEffectsTests
{
    private const int Leds = 26;
    private static readonly string[] SeededKeys = { "voice", "meteorShower", "kaleidoscope" };

    private static readonly Lazy<JsonDocument> Vectors = new(() => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "LianLiFlex", "Data", "flex-tl-extended-vectors.json"))));

    private static readonly RgbColor[] Custom =
    {
        new(10, 200, 30), new(250, 20, 120), new(90, 90, 250), new(200, 160, 10),
    };

    private static FlexEffectRequest Req(
        FlexScope scope = FlexScope.All, int fans = 1, int direction = 0, int level = 255,
        IReadOnlyList<RgbColor>? colors = null) =>
        new(scope, fans, direction, level, colors ?? Array.Empty<RgbColor>());

    public static IEnumerable<object[]> AllKeys() => FlexTlExtendedEffects.Keys.Select(k => new object[] { k });

    public static IEnumerable<object[]> ExactKeys() =>
        FlexTlExtendedEffects.Keys.Where(k => !SeededKeys.Contains(k)).Select(k => new object[] { k });

    public static IEnumerable<object[]> SeededKeyRows() => SeededKeys.Select(k => new object[] { k });

    public static IEnumerable<object[]> SettingCases() =>
        Vectors.Value.RootElement.GetProperty("settingCases").EnumerateArray().Select((_, i) => new object[] { i });

    private static JsonElement Expected(string key) =>
        Vectors.Value.RootElement.GetProperty("expected").EnumerateArray()
            .First(e => e.GetProperty("key").GetString() == key);

    private static (int R, int G, int B) Led(byte[] frames, int frame, int led, int leds = Leds)
    {
        var o = (frame * leds + led) * 3;
        return (frames[o], frames[o + 1], frames[o + 2]);
    }

    private static bool AnyDifference(byte[] a, byte[] b) => a.Length != b.Length || !a.AsSpan().SequenceEqual(b);

    [Fact]
    public void Catalog_lists_the_twenty_keys_and_all_are_implemented()
    {
        Assert.Equal(20, FlexTlExtendedEffects.Keys.Count);
        Assert.Equal(20, FlexTlExtendedEffects.Keys.Distinct().Count());
        Assert.All(FlexTlExtendedEffects.Keys, k => Assert.True(FlexTlExtendedEffects.IsImplemented(k)));
        Assert.False(FlexTlExtendedEffects.IsImplemented("rainbow"));
        Assert.Throws<NotSupportedException>(() => FlexTlExtendedEffects.Render("rainbow", Req()));
    }

    [Fact]
    public void Collide_uses_the_slower_interval_base()
    {
        Assert.Equal(16.0, FlexTlExtendedEffects.Render("collide", Req()).IntervalBaseMs);
        Assert.Equal(11.0, FlexTlExtendedEffects.Render("wave", Req()).IntervalBaseMs);
    }

    [Theory]
    [MemberData(nameof(ExactKeys))]
    public void Default_settings_match_the_expected_frames_byte_for_byte(string key)
    {
        var rec = Expected(key);
        var expected = Convert.FromBase64String(rec.GetProperty("rawFramesB64").GetString()!);

        var anim = FlexTlExtendedEffects.Render(key, Req());

        Assert.Equal(rec.GetProperty("frames").GetInt32(), anim.FrameCount);
        Assert.Equal(rec.GetProperty("leds").GetInt32(), anim.Leds);
        Assert.Equal(expected, anim.Frames);
        var interval = rec.GetProperty("intervalMs").GetDouble();
        Assert.Equal(interval, anim.IntervalBaseMs * 4);
    }

    [Theory]
    [MemberData(nameof(SeededKeyRows))]
    public void Seeded_effects_use_only_the_expected_palette_and_stay_in_the_expected_length_band(string key)
    {
        var rec = Expected(key);
        var expected = Convert.FromBase64String(rec.GetProperty("rawFramesB64").GetString()!);
        var expectedFrames = rec.GetProperty("frames").GetInt32();

        var anim = FlexTlExtendedEffects.Render(key, Req());

        Assert.Equal(Leds, anim.Leds);
        Assert.Equal(anim.FrameCount * Leds * 3, anim.Frames.Length);
        Assert.Subset(RandomPalette(key), DistinctColors(expected));
        Assert.Subset(RandomPalette(key), DistinctColors(anim.Frames));
        if (key == "kaleidoscope")
        {
            Assert.Equal(expectedFrames, anim.FrameCount);
        }
        else
        {
            Assert.InRange(anim.FrameCount, expectedFrames * 3 / 4, expectedFrames * 5 / 4);
        }
    }

    [Fact]
    public void Voice_frames_are_one_color_lit_symmetrically_around_the_middle()
    {
        var anim = FlexTlExtendedEffects.Render("voice", Req());
        var litFrames = 0;
        for (var f = 0; f < anim.FrameCount; f++)
        {
            var lit = Enumerable.Range(0, Leds).Select(l => Led(anim.Frames, f, l)).Where(c => c != (0, 0, 0)).ToList();
            Assert.True(lit.Distinct().Count() <= 1);
            litFrames += lit.Count > 0 ? 1 : 0;
        }
        Assert.True(litFrames > anim.FrameCount / 2);
    }

    [Fact]
    public void Meteor_shower_ends_on_an_all_black_frame()
    {
        var anim = FlexTlExtendedEffects.Render("meteorShower", Req());
        var last = anim.Frames.AsSpan((anim.FrameCount - 1) * Leds * 3, Leds * 3);
        Assert.True(last.IndexOfAnyExcept((byte)0) < 0);
        Assert.True(anim.Frames.AsSpan(0, Leds * 3).IndexOfAnyExcept((byte)0) >= 0);
    }

    [Theory]
    [MemberData(nameof(SeededKeyRows))]
    public void Seed_makes_the_random_effects_reproducible(string key)
    {
        var a = FlexTlExtendedEffects.Render(key, Req(), 11);
        var b = FlexTlExtendedEffects.Render(key, Req(), 11);
        var c = FlexTlExtendedEffects.Render(key, Req(), 12);

        Assert.Equal(a.Frames, b.Frames);
        Assert.True(AnyDifference(a.Frames, c.Frames));
    }

    [Theory]
    [MemberData(nameof(SettingCases))]
    public void Non_default_settings_match_the_stored_vectors(int index)
    {
        var row = Vectors.Value.RootElement.GetProperty("settingCases")[index];
        var colors = row.GetProperty("colors").EnumerateArray()
            .Select(c => new RgbColor(c[0].GetByte(), c[1].GetByte(), c[2].GetByte())).ToArray();
        var scope = Enum.Parse<FlexScope>(row.GetProperty("scope").GetString()!);
        var req = new FlexEffectRequest(
            scope, row.GetProperty("fans").GetInt32(), row.GetProperty("direction").GetInt32(),
            row.GetProperty("level").GetInt32(), colors);

        var anim = FlexTlExtendedEffects.Render(row.GetProperty("key").GetString()!, req, row.GetProperty("seed").GetInt32());

        Assert.Equal(row.GetProperty("frames").GetInt32(), anim.FrameCount);
        Assert.Equal(row.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(anim.Frames)).ToLowerInvariant());
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void Up_and_Down_scopes_black_the_other_half_and_together_rebuild_the_whole(string key)
    {
        var all = FlexTlExtendedEffects.Render(key, Req(fans: 2), 5);
        var up = FlexTlExtendedEffects.Render(key, Req(FlexScope.Up, 2), 5);
        var down = FlexTlExtendedEffects.Render(key, Req(FlexScope.Down, 2), 5);

        Assert.Equal(all.FrameCount, up.FrameCount);
        Assert.Equal(all.FrameCount, down.FrameCount);
        for (var f = 0; f < all.FrameCount; f++)
        {
            for (var l = 0; l < 2 * Leds; l++)
            {
                var upperHalf = l % Leds < 13;
                var (r, g, b) = Led(all.Frames, f, l, 2 * Leds);
                Assert.Equal((upperHalf ? (r, g, b) : (0, 0, 0)), Led(up.Frames, f, l, 2 * Leds));
                Assert.Equal((upperHalf ? (0, 0, 0) : (r, g, b)), Led(down.Frames, f, l, 2 * Leds));
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void Zero_brightness_renders_only_black(string key)
    {
        var anim = FlexTlExtendedEffects.Render(key, Req(level: 0, colors: Custom), 3);

        Assert.True(anim.FrameCount > 0);
        Assert.True(anim.Frames.AsSpan().IndexOfAnyExcept((byte)0) < 0);
    }

    [Theory]
    [InlineData("staggered")]
    [InlineData("tide")]
    [InlineData("mixing")]
    [InlineData("voice")]
    [InlineData("door")]
    [InlineData("ripple")]
    [InlineData("intertwine")]
    [InlineData("collide")]
    [InlineData("electricCurrent")]
    [InlineData("kaleidoscope")]
    public void Direction_does_not_change_effects_that_ignore_it(string key)
    {
        var cw = FlexTlExtendedEffects.Render(key, Req(direction: 0, colors: Custom), 9);
        var ccw = FlexTlExtendedEffects.Render(key, Req(direction: 1, colors: Custom), 9);

        Assert.Equal(cw.Frames, ccw.Frames);
    }

    [Theory]
    [InlineData("render")]
    [InlineData("reflect")]
    [InlineData("tailChasing")]
    [InlineData("paint")]
    [InlineData("pingPong")]
    [InlineData("coverCycle")]
    [InlineData("wave")]
    [InlineData("racing")]
    [InlineData("lottery")]
    [InlineData("meteorShower")]
    public void Direction_changes_the_animation_for_directional_effects(string key)
    {
        var cw = FlexTlExtendedEffects.Render(key, Req(fans: 2, direction: 0, colors: Custom), 9);
        var ccw = FlexTlExtendedEffects.Render(key, Req(fans: 2, direction: 1, colors: Custom), 9);

        Assert.True(AnyDifference(cw.Frames, ccw.Frames));
    }

    [Theory]
    [InlineData("paint")]
    [InlineData("pingPong")]
    [InlineData("coverCycle")]
    [InlineData("wave")]
    public void Sweeps_start_at_the_low_fans_clockwise_and_the_high_fans_anticlockwise(string key)
    {
        var cw = FlexTlExtendedEffects.Render(key, Req(fans: 4, direction: 0, colors: Custom));
        var ccw = FlexTlExtendedEffects.Render(key, Req(fans: 4, direction: 1, colors: Custom));

        Assert.True(ChangedFanCentroid(cw, 4, 4) < 1.5);
        Assert.True(ChangedFanCentroid(ccw, 4, 4) > 1.5);
    }

    [Theory]
    [InlineData("door")]
    [InlineData("render")]
    [InlineData("ripple")]
    [InlineData("paint")]
    [InlineData("coverCycle")]
    [InlineData("tide")]
    public void Solid_color_effects_draw_only_palette_colors_and_black(string key)
    {
        var level = 200;
        var anim = FlexTlExtendedEffects.Render(key, Req(fans: 3, direction: 1, level: level, colors: Custom));
        var allowed = Custom.Select(c => (c.R * level >> 8, c.G * level >> 8, c.B * level >> 8))
            .Append((0, 0, 0)).ToHashSet();

        for (var f = 0; f < anim.FrameCount; f++)
        {
            for (var l = 0; l < 3 * Leds; l++)
            {
                Assert.Contains(Led(anim.Frames, f, l, 3 * Leds), allowed);
            }
        }
    }

    [Theory]
    [InlineData("tailChasing")]
    [InlineData("racing")]
    [InlineData("intertwine")]
    [InlineData("collide")]
    public void Effects_needing_two_colors_pad_a_short_palette_from_the_defaults(string key)
    {
        var defaults = FlexTlExtendedEffects.DefaultColors(key);
        var padded = FlexTlExtendedEffects.Render(key, Req(colors: new[] { Custom[0] }));
        var explicitTwo = FlexTlExtendedEffects.Render(key, Req(colors: new[] { Custom[0], defaults[1] }));
        var none = FlexTlExtendedEffects.Render(key, Req());
        var allDefaults = FlexTlExtendedEffects.Render(key, Req(colors: defaults));

        Assert.Equal(explicitTwo.Frames, padded.Frames);
        Assert.Equal(allDefaults.Frames, none.Frames);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-2)]
    public void Fan_counts_outside_one_to_four_render_four_fans(int fans)
    {
        var anim = FlexTlExtendedEffects.Render("wave", Req(fans: fans));
        var four = FlexTlExtendedEffects.Render("wave", Req(fans: 4));

        Assert.Equal(4 * Leds, anim.Leds);
        Assert.Equal(four.Frames, anim.Frames);
    }

    [Theory]
    [InlineData(FlexScope.Inner)]
    [InlineData(FlexScope.Center)]
    [InlineData(FlexScope.Outer)]
    public void Non_TL_scopes_are_rejected(FlexScope scope)
    {
        Assert.Throws<ArgumentException>(() => FlexTlExtendedEffects.Render("wave", Req(scope)));
    }

    private static HashSet<(int, int, int)> RandomPalette(string key)
    {
        var wheel = new (int, int, int)[]
        {
            (255, 0, 0), (255, 128, 0), (255, 255, 0), (128, 255, 0), (0, 255, 0), (0, 255, 128), (0, 255, 255),
            (0, 128, 255), (0, 0, 255), (128, 0, 255), (255, 0, 255), (255, 0, 128), (255, 255, 255),
        };
        var set = wheel.Select(c => (c.Item1 * 255 >> 8, c.Item2 * 255 >> 8, c.Item3 * 255 >> 8)).ToHashSet();
        set.Add((0, 0, 0));
        return set;
    }

    private static HashSet<(int, int, int)> DistinctColors(byte[] frames)
    {
        var set = new HashSet<(int, int, int)>();
        for (var i = 0; i + 2 < frames.Length; i += 3)
        {
            set.Add((frames[i], frames[i + 1], frames[i + 2]));
        }
        return set;
    }

    private static double ChangedFanCentroid(FlexRawAnimation a, int fans, int frame)
    {
        double sum = 0;
        double n = 0;
        for (var l = 0; l < fans * Leds; l++)
        {
            if (Led(a.Frames, 0, l, fans * Leds) != Led(a.Frames, frame, l, fans * Leds))
            {
                sum += l / Leds;
                n++;
            }
        }
        return n == 0 ? double.NaN : sum / n;
    }
}
