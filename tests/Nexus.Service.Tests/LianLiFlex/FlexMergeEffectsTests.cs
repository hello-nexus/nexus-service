using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Peripherals.LianLiFlex;
using Xunit;
using RgbColor = Nexus.Service.Peripherals.Hyte.Np50.RgbColor;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexMergeEffectsTests
{
    private static readonly RgbColor[] Palette =
    {
        new(255, 0, 150), new(0, 215, 255), new(0, 255, 0), new(255, 215, 0),
    };

    private static readonly Lazy<JsonDocument> Vectors = new(() => JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "LianLiFlex", "Data", "flex-merge-vectors.json"))));

    public static IEnumerable<object[]> StripKeys() =>
        FlexMergeEffects.MergeKeys(FlexFamily.TlFlex).Where(k => !FlexMergeEffects.IsPerReceiver(k)).Select(k => new object[] { k });

    public static IEnumerable<object[]> RecordedCases() =>
        Vectors.Value.RootElement.GetProperty("recorded").EnumerateArray().Select((_, i) => new object[] { i });

    private static (string Mac, FlexFamily Family, int Fans) Tl(string mac, int fans = 1) => (mac, FlexFamily.TlFlex, fans);

    private static (string Mac, FlexFamily Family, int Fans) Inf(string mac, int fans = 1) => (mac, FlexFamily.SlInfFlex, fans);

    private static IReadOnlyList<FlexMergeSlice> Run(
        string key, IReadOnlyList<(string Mac, FlexFamily Family, int Fans)> p, int direction = 0, int speed = 3, int brightness = 4) =>
        FlexMergeEffects.Render(key, p, speed, direction, brightness, Palette);

    [Theory]
    [MemberData(nameof(RecordedCases))]
    public void Recorded_merge_vector_is_byte_exact(int index)
    {
        var v = Vectors.Value.RootElement.GetProperty("recorded")[index];
        var participants = v.GetProperty("participants").EnumerateArray()
            .Select(p => (p.GetProperty("mac").GetString()!, p.GetProperty("family").GetString() == "tl" ? FlexFamily.TlFlex : FlexFamily.SlInfFlex, p.GetProperty("fans").GetInt32()))
            .ToList();
        var colors = v.GetProperty("colors").EnumerateArray()
            .Select(c => new RgbColor((byte)c[0].GetInt32(), (byte)c[1].GetInt32(), (byte)c[2].GetInt32())).ToList();

        var slices = FlexMergeEffects.Render(
            v.GetProperty("key").GetString()!, participants, v.GetProperty("speedLevel").GetInt32(),
            v.GetProperty("direction").GetInt32(), v.GetProperty("brightnessLevel").GetInt32(), colors);

        var slice = Assert.Single(slices);
        Assert.Equal(v.GetProperty("frames").GetInt32(), slice.FrameCount);
        Assert.Equal(v.GetProperty("leds").GetInt32(), slice.Leds);
        Assert.Equal(v.GetProperty("intervalMs").GetDouble(), slice.IntervalMs);
        var expected = Convert.FromBase64String(v.GetProperty("rawB64").GetString()!);
        Assert.Equal(expected.Length, slice.Frames.Length);
        var firstDiff = Array.FindIndex(Enumerable.Range(0, expected.Length).ToArray(), i => expected[i] != slice.Frames[i]);
        Assert.True(firstDiff < 0, $"first differing byte {firstDiff} (frame {firstDiff / (slice.Leds * 3)}, led {firstDiff % (slice.Leds * 3) / 3})");
    }

    [Fact]
    public void Catalog_is_the_twelve_merge_effects_for_both_families()
    {
        foreach (var family in new[] { FlexFamily.TlFlex, FlexFamily.SlInfFlex })
        {
            Assert.Equal(12, FlexMergeEffects.MergeKeys(family).Count);
            Assert.All(FlexMergeEffects.MergeKeys(family), k => Assert.True(FlexMergeEffects.IsMergeable(family, k)));
        }
        Assert.False(FlexMergeEffects.IsMergeable(FlexFamily.TlFlex, "voice"));
        Assert.True(FlexMergeEffects.IsPerReceiver("twinkle"));
        Assert.True(FlexMergeEffects.IsPerReceiver("rainbowMorph"));
        Assert.Throws<NotSupportedException>(() => Run("twinkle", new[] { Tl("A") }));
        Assert.Throws<ArgumentException>(() => Run("voice", new[] { Tl("A") }));
    }

    [Theory]
    [InlineData(0, 84)]
    [InlineData(3, 48)]
    [InlineData(4, 36)]
    public void Interval_is_the_speed_multiplier_times_twelve(int level, double expected)
    {
        Assert.Equal(expected, Run("rainbow", new[] { Tl("A") }, speed: level)[0].IntervalMs);
    }

    [Theory]
    [MemberData(nameof(StripKeys))]
    public void Slice_shape_matches_the_receiver(string key)
    {
        var slices = Run(key, new[] { Tl("A", 2), Inf("B", 3) });
        Assert.Equal(52, slices[0].Leds);
        Assert.Equal(132, slices[1].Leds);
        Assert.All(slices, s => Assert.Equal(s.FrameCount * s.Leds * 3, s.Frames.Length));
        Assert.Equal(slices[0].FrameCount, slices[1].FrameCount);
        Assert.Equal(slices[0].IntervalMs, slices[1].IntervalMs);
    }

    [Theory]
    [MemberData(nameof(StripKeys))]
    public void Two_receivers_tile_one_continuous_effect(string key)
    {
        var pair = Run(key, new[] { Tl("A"), Tl("B") });
        var whole = Run(key, new[] { Tl("AB", 2) })[0];
        Assert.Equal(whole.FrameCount, pair[0].FrameCount);
        var joined = new byte[whole.Frames.Length];
        for (var f = 0; f < whole.FrameCount; f++)
        {
            Array.Copy(pair[0].Frames, f * 78, joined, f * 156, 78);
            Array.Copy(pair[1].Frames, f * 78, joined, (f * 156) + 78, 78);
        }
        Assert.Equal(whole.Frames, joined);
    }

    [Theory]
    [MemberData(nameof(StripKeys))]
    public void Two_inf_receivers_tile_one_continuous_effect(string key)
    {
        var pair = Run(key, new[] { Inf("A"), Inf("B") });
        var whole = Run(key, new[] { Inf("AB", 2) })[0];
        var joined = new byte[whole.Frames.Length];
        for (var f = 0; f < whole.FrameCount; f++)
        {
            Array.Copy(pair[0].Frames, f * 132, joined, f * 264, 132);
            Array.Copy(pair[1].Frames, f * 132, joined, (f * 264) + 132, 132);
        }
        Assert.Equal(whole.Frames, joined);
    }

    [Theory]
    [InlineData("rainbow")]
    [InlineData("meteor")]
    [InlineData("runway")]
    public void Sequence_order_decides_which_receiver_gets_which_slice(string key)
    {
        var ab = Run(key, new[] { Tl("A"), Tl("B") });
        var ba = Run(key, new[] { Tl("B"), Tl("A") });
        Assert.Equal("B", ba[0].Mac);
        Assert.Equal(ab[0].Frames, ba[0].Frames);
        Assert.Equal(ab[1].Frames, ba[1].Frames);
        Assert.NotEqual(ab[0].Frames, ab[1].Frames);
    }

    [Fact]
    public void Receiver_that_is_later_in_the_sequence_sees_the_strip_later()
    {
        var slices = Run("rainbow", new[] { Tl("A"), Tl("B") });
        Assert.Equal("A", slices[0].Mac);
        Assert.Equal("B", slices[1].Mac);
    }

    [Theory]
    [InlineData("meteor")]
    [InlineData("coverCycle")]
    public void Strip_direction_mirrors_the_whole_strip(string key)
    {
        var right = Run(key, new[] { Tl("A", 2) }, direction: 0)[0];
        var left = Run(key, new[] { Tl("A", 2) }, direction: 1)[0];
        Assert.NotEqual(right.Frames, left.Frames);
        Assert.Equal(right.FrameCount, left.FrameCount);
        for (var f = 0; f < right.FrameCount; f++)
        {
            for (var dot = 0; dot < 26; dot++)
            {
                var mirrored = 25 - dot;
                var a = ((f * 52) + ((dot / 13) * 26) + (dot % 13)) * 3;
                var b = ((f * 52) + ((mirrored / 13) * 26) + (mirrored % 13)) * 3;
                Assert.Equal(right.Frames.AsSpan(a, 3).ToArray(), left.Frames.AsSpan(b, 3).ToArray());
            }
        }
    }

    [Theory]
    [InlineData("static")]
    [InlineData("breathing")]
    [InlineData("runway")]
    public void Direction_has_no_effect_on_effects_that_do_not_read_it(string key)
    {
        Assert.Equal(Run(key, new[] { Tl("A", 2) }, direction: 0)[0].Frames, Run(key, new[] { Tl("A", 2) }, direction: 1)[0].Frames);
    }

    [Fact]
    public void Slice_direction_mirrors_only_that_receivers_dots()
    {
        var plain = FlexMergeEffects.Render(
            "runway", new[] { new FlexMergeParticipant("A", FlexFamily.TlFlex, 1), new FlexMergeParticipant("B", FlexFamily.TlFlex, 1) },
            3, 0, 4, Palette);
        var flipped = FlexMergeEffects.Render(
            "runway", new[] { new FlexMergeParticipant("A", FlexFamily.TlFlex, 1), new FlexMergeParticipant("B", FlexFamily.TlFlex, 1, SliceDirection: 1) },
            3, 0, 4, Palette);
        Assert.Equal(plain[0].Frames, flipped[0].Frames);
        Assert.NotEqual(plain[1].Frames, flipped[1].Frames);
        for (var f = 0; f < plain[1].FrameCount; f++)
        {
            for (var n = 0; n < 13; n++)
            {
                var a = ((f * 26) + n) * 3;
                var b = ((f * 26) + 12 - n) * 3;
                Assert.Equal(plain[1].Frames.AsSpan(a, 3).ToArray(), flipped[1].Frames.AsSpan(b, 3).ToArray());
            }
        }
    }

    [Fact]
    public void Tl_halves_repeat_the_same_dots()
    {
        var s = Run("meteor", new[] { Tl("A") })[0];
        for (var f = 0; f < s.FrameCount; f++)
        {
            Assert.Equal(s.Frames.AsSpan((f * 78), 39).ToArray(), s.Frames.AsSpan((f * 78) + 39, 39).ToArray());
        }
    }

    [Fact]
    public void Mixed_tl_and_inf_receivers_share_one_strip()
    {
        var mixed = Run("rainbow", new[] { Tl("T"), Inf("I") });
        var asInfFirst = Run("rainbow", new[] { Inf("I"), Tl("T") });
        Assert.Equal(24, mixed[0].FrameCount);
        Assert.NotEqual(mixed[1].Frames, asInfFirst[0].Frames);
        Assert.Equal(26, mixed[0].Leds);
        Assert.Equal(44, mixed[1].Leds);
    }

    [Fact]
    public void Brightness_zero_is_black_and_full_matches_the_generator_scale()
    {
        Assert.All(Run("rainbow", new[] { Tl("A") }, brightness: 0)[0].Frames, b => Assert.Equal(0, b));
        var full = Run("static", new[] { Tl("A") })[0];
        Assert.Equal(1, full.FrameCount);
        Assert.Equal(new byte[] { 254, 0, 149 }, full.Frames.AsSpan(0, 3).ToArray());
    }

    [Fact]
    public void Colors_above_the_power_cap_are_scaled_before_use()
    {
        var white = FlexMergeEffects.Render("static", new[] { Tl("A") }, 3, 0, 4, new[] { new RgbColor(255, 255, 255) })[0];
        Assert.True(white.Frames[0] + white.Frames[1] + white.Frames[2] <= 600);
    }

    [Fact]
    public void Rejects_empty_and_oversized_participants()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Run("rainbow", Array.Empty<(string, FlexFamily, int)>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run("rainbow", new[] { Tl("A", 0) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Run("rainbow", new[] { Tl("A", 60) }));
    }
}
