using System;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.LianLi;

namespace Nexus.Service.Tests.LianLi;

public class LianLiLightingModesTests
{
    // ── Per-family support ──

    // The SL v1 firmware has no voice/groove/render/tunnel (0x26..0x29).
    [Fact]
    public void Sl_v1_catalog_drops_the_sl_infinity_only_effects()
    {
        var sl = LianLiLightingModes.CatalogFor(LianLiFanFamily.Sl).Select(m => m.Key).ToArray();
        var sli = LianLiLightingModes.CatalogFor(LianLiFanFamily.SlInfinity).Select(m => m.Key).ToArray();

        Assert.Equal(LianLiLightingModes.Catalog.Length, sli.Length);
        Assert.Equal(sli.Length - 4, sl.Length);
        foreach (var key in new[] { "voice", "groove", "render", "tunnel" })
        {
            Assert.Contains(key, sli);
            Assert.DoesNotContain(key, sl);
        }
        foreach (var key in new[] { "custom", "static", "breathing", "spectrumCycle", "rainbowWave", "staggered",
                     "tide", "runway", "mixing", "stack", "neon", "colorCycle", "meteor", "stackMultiColor" })
        {
            Assert.Contains(key, sl);
        }
    }

    [Fact]
    public void SupportedBy_gates_only_the_sl_family()
    {
        var voice = LianLiLightingModes.Find("voice")!;
        Assert.False(voice.SupportedBy(LianLiFanFamily.Sl));
        Assert.True(voice.SupportedBy(LianLiFanFamily.SlInfinity));
        Assert.True(voice.SupportedBy(LianLiFanFamily.SlV2));
        Assert.True(LianLiLightingModes.Find("static")!.SupportedBy(LianLiFanFamily.Sl));
    }

    // ── Catalog completeness ──

    [Fact]
    public void Catalog_contains_all_expected_keys()
    {
        var keys = LianLiLightingModes.Catalog.Select(m => m.Key).ToArray();
        Assert.Contains("custom", keys);
        Assert.Contains("static", keys);
        Assert.Contains("breathing", keys);
        Assert.Contains("spectrumCycle", keys);
        Assert.Contains("rainbowWave", keys);
        Assert.Contains("staggered", keys);
        Assert.Contains("tide", keys);
        Assert.Contains("runway", keys);
        Assert.Contains("mixing", keys);
        Assert.Contains("stack", keys);
        Assert.Contains("neon", keys);
        Assert.Contains("colorCycle", keys);
        Assert.Contains("meteor", keys);
        Assert.Contains("voice", keys);
        Assert.Contains("groove", keys);
        Assert.Contains("stackMultiColor", keys);
        Assert.Contains("render", keys);
        Assert.Contains("tunnel", keys);
        Assert.Equal(18, keys.Length);
    }

    [Fact]
    public void Catalog_has_no_duplicate_keys()
    {
        var keys = LianLiLightingModes.Catalog.Select(m => m.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    // ── Effect bytes ──

    [Theory]
    [InlineData("custom",        0x01)]
    [InlineData("static",        0x01)]
    [InlineData("breathing",     0x02)]
    [InlineData("spectrumCycle", 0x04)]
    [InlineData("rainbowWave",   0x05)]
    [InlineData("staggered",     0x18)]
    [InlineData("tide",          0x1A)]
    [InlineData("runway",        0x1C)]
    [InlineData("mixing",        0x1E)]
    [InlineData("stack",         0x20)]
    [InlineData("neon",          0x22)]
    [InlineData("colorCycle",    0x23)]
    [InlineData("meteor",        0x24)]
    [InlineData("voice",         0x26)]
    [InlineData("groove",          0x27)]
    [InlineData("stackMultiColor", 0x21)]
    [InlineData("render",          0x28)]
    [InlineData("tunnel",          0x29)]
    public void Find_returns_correct_effect_byte(string key, byte expected)
    {
        var m = LianLiLightingModes.Find(key);
        Assert.NotNull(m);
        Assert.Equal(expected, m!.EffectByte);
    }

    // ── Speed codes ──

    [Theory]
    [InlineData(0, 0x02)]
    [InlineData(1, 0x01)]
    [InlineData(2, 0x00)]
    [InlineData(3, 0xFF)]
    [InlineData(4, 0xFE)]
    public void SpeedCodes_index_to_byte(int index, byte expected)
    {
        Assert.Equal(expected, LianLiLightingModes.SpeedCodes[index]);
    }

    // ── Brightness codes ──

    [Theory]
    [InlineData(0, 0x08)] // off
    [InlineData(1, 0x03)]
    [InlineData(2, 0x02)]
    [InlineData(3, 0x01)]
    [InlineData(4, 0x00)] // full
    public void BrightnessCodes_index_to_byte(int index, byte expected)
    {
        Assert.Equal(expected, LianLiLightingModes.BrightnessCodes[index]);
    }

    // ── Direction bytes ──

    [Fact]
    public void DirectionByte_zero_is_LTR()
    {
        Assert.Equal(0x00, LianLiLightingModes.DirectionByte(0));
    }

    [Fact]
    public void DirectionByte_one_is_RTL()
    {
        Assert.Equal(0x01, LianLiLightingModes.DirectionByte(1));
    }

    [Fact]
    public void DirectionByte_other_values_return_LTR()
    {
        Assert.Equal(0x00, LianLiLightingModes.DirectionByte(99));
    }

    // ── Find ──

    [Fact]
    public void Find_returns_null_for_unknown_key()
    {
        Assert.Null(LianLiLightingModes.Find("unknown"));
        Assert.Null(LianLiLightingModes.Find(""));
    }

    // ── ColorsMax ──

    [Theory]
    [InlineData("custom", 0)]
    [InlineData("rainbowWave", 0)]
    [InlineData("spectrumCycle", 0)]
    [InlineData("neon", 0)]
    [InlineData("voice", 0)]
    [InlineData("stack", 1)]
    [InlineData("staggered", 2)]
    [InlineData("colorCycle", 3)]
    [InlineData("breathing", 6)]
    [InlineData("static", 6)]
    public void ColorsMax_per_mode(string key, int expected)
    {
        var m = LianLiLightingModes.Find(key);
        Assert.NotNull(m);
        Assert.Equal(expected, m!.ColorsMax);
    }

    // ── HasSpeed / HasDirection ──

    [Fact]
    public void Custom_and_static_have_no_speed_or_direction()
    {
        var custom = LianLiLightingModes.Find("custom");
        var stat = LianLiLightingModes.Find("static");
        Assert.NotNull(custom); Assert.False(custom!.HasSpeed); Assert.False(custom.HasDirection);
        Assert.NotNull(stat);   Assert.False(stat!.HasSpeed);   Assert.False(stat.HasDirection);
    }

    [Theory]
    [InlineData("rainbowWave")]
    [InlineData("stack")]
    [InlineData("colorCycle")]
    public void Modes_with_direction_have_it(string key)
    {
        var m = LianLiLightingModes.Find(key);
        Assert.NotNull(m);
        Assert.True(m!.HasDirection);
    }

    [Theory]
    [InlineData("stackMultiColor", true, true, 0)]
    [InlineData("render",          true, true, 4)]
    [InlineData("tunnel",          true, true, 4)]
    public void New_modes_have_correct_properties(string key, bool hasSpeed, bool hasDirection, int colorsMax)
    {
        var m = LianLiLightingModes.Find(key);
        Assert.NotNull(m);
        Assert.Equal(hasSpeed,     m!.HasSpeed);
        Assert.Equal(hasDirection, m.HasDirection);
        Assert.True(m.HasBrightness);
        Assert.Equal(colorsMax,    m.ColorsMax);
    }
}
