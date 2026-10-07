using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.LianLi;
using Xunit;

namespace Nexus.Service.Tests.LianLi;

public class LianLiLightingModesTests
{
    private static readonly LianLiFanFamily[] Families =
    {
        LianLiFanFamily.Sl, LianLiFanFamily.Al, LianLiFanFamily.SlInfinity, LianLiFanFamily.SlV2, LianLiFanFamily.AlV2,
    };

    [Fact]
    public void Every_family_lists_custom_first_and_no_key_twice()
    {
        foreach (var family in Families)
        {
            var keys = LianLiLightingModes.CatalogFor(family).Select(m => m.Key).ToArray();
            Assert.Equal("custom", keys[0]);
            Assert.Equal(keys.Length, keys.Distinct().Count());
            Assert.Contains("static", keys);
            Assert.Contains("breathing", keys);
        }
    }

    [Theory]
    [InlineData(LianLiFanFamily.Sl, 14)]
    [InlineData(LianLiFanFamily.Al, 19)]
    [InlineData(LianLiFanFamily.SlInfinity, 21)]
    [InlineData(LianLiFanFamily.SlV2, 18)]
    [InlineData(LianLiFanFamily.AlV2, 27)]
    public void Catalog_size_per_family(LianLiFanFamily family, int count)
    {
        Assert.Equal(count, LianLiLightingModes.CatalogFor(family).Count);
    }

    // SL-Infinity keeps the bytes verified on fw 1.4 for the keys it already shipped.
    [Theory]
    [InlineData("static", 0x01)]
    [InlineData("breathing", 0x02)]
    [InlineData("spectrumCycle", 0x04)]
    [InlineData("rainbowWave", 0x05)]
    [InlineData("colorCycle", 0x18)]
    [InlineData("meteor", 0x19)]
    [InlineData("runway", 0x1A)]
    [InlineData("voice", 0x2A)]
    [InlineData("mixing", 0x38)]
    [InlineData("stack", 0x39)]
    [InlineData("tide", 0x3A)]
    [InlineData("mopUp", 0x44)]
    [InlineData("heartBeat", 0x42)]
    [InlineData("electricCurrent", 0x41)]
    public void Sl_infinity_effect_bytes(string key, byte expected)
    {
        Assert.Equal(expected, LianLiLightingModes.Find(LianLiFanFamily.SlInfinity, key)!.EffectByte);
    }

    [Theory]
    [InlineData("runway", 0x46)]
    [InlineData("mopUp", 0x47)]
    [InlineData("mixing", 0x48)]
    [InlineData("stack", 0x49)]
    [InlineData("tide", 0x4A)]
    [InlineData("scan", 0x4B)]
    [InlineData("door", 0x4C)]
    [InlineData("heartBeatRunway", 0x4D)]
    [InlineData("electricCurrent", 0x4E)]
    public void Sl_infinity_merged_bytes(string key, byte expected)
    {
        var m = LianLiLightingModes.Find(LianLiFanFamily.SlInfinity, key)!;
        Assert.Equal(expected, m.MergedEffectByte);
        Assert.True(m.MergesOn(LianLiFanProfiles.Default));
    }

    [Theory]
    [InlineData(LianLiFanFamily.Sl, "rainbowWave", 0x05)]
    [InlineData(LianLiFanFamily.Sl, "colorCycle", 0x23)]
    [InlineData(LianLiFanFamily.SlV2, "tunnel", 0x29)]
    [InlineData(LianLiFanFamily.Al, "rainbowWave", 0x28)]
    [InlineData(LianLiFanFamily.Al, "spectrumCycle", 0x35)]
    [InlineData(LianLiFanFamily.Al, "taichi", 0x2C)]
    [InlineData(LianLiFanFamily.Al, "contest", 0x33)]
    [InlineData(LianLiFanFamily.AlV2, "rainbowWave", 0x2B)]
    [InlineData(LianLiFanFamily.AlV2, "stack", 0x43)]
    [InlineData(LianLiFanFamily.AlV2, "twinkle", 0x3A)]
    public void Each_family_uses_its_own_numbering(LianLiFanFamily family, string key, byte expected)
    {
        Assert.Equal(expected, LianLiLightingModes.Find(family, key)!.EffectByte);
    }

    [Fact]
    public void Sl_v1_lacks_the_v2_effects()
    {
        foreach (var key in new[] { "voice", "groove", "render", "tunnel" })
        {
            Assert.Null(LianLiLightingModes.Find(LianLiFanFamily.Sl, key));
            Assert.NotNull(LianLiLightingModes.Find(LianLiFanFamily.SlV2, key));
        }
    }

    [Fact]
    public void Merges_exist_only_on_sl_infinity()
    {
        foreach (var family in Families)
        {
            if (family == LianLiFanFamily.SlInfinity) continue;
            Assert.All(LianLiLightingModes.CatalogFor(family), m => Assert.Equal(0, m.MergedEffectByte));
        }
    }

    // AL and AL v2 animate a whole fan from the inner channel; the split ring pairs commit on both.
    [Theory]
    [InlineData(LianLiFanFamily.Al, "taichi", true)]
    [InlineData(LianLiFanFamily.Al, "static", false)]
    [InlineData(LianLiFanFamily.Al, "runway", false)]
    [InlineData(LianLiFanFamily.AlV2, "spectrumCycle", false)]
    [InlineData(LianLiFanFamily.AlV2, "wave", true)]
    [InlineData(LianLiFanFamily.SlInfinity, "tide", false)]
    [InlineData(LianLiFanFamily.SlV2, "tide", false)]
    public void Whole_fan_modes(LianLiFanFamily family, string key, bool wholeFan)
    {
        Assert.Equal(wholeFan, LianLiLightingModes.Find(family, key)!.WholeFan);
    }

    [Theory]
    [InlineData(LianLiFanFamily.SlInfinity, "static", 4)]
    [InlineData(LianLiFanFamily.SlV2, "static", 6)]
    [InlineData(LianLiFanFamily.AlV2, "breathing", 6)]
    [InlineData(LianLiFanFamily.SlInfinity, "runway", 2)]
    [InlineData(LianLiFanFamily.SlInfinity, "heartBeat", 1)]
    [InlineData(LianLiFanFamily.Sl, "neon", 0)]
    public void ColorsMax_follows_the_default_palette(LianLiFanFamily family, string key, int colors)
    {
        var m = LianLiLightingModes.Find(family, key)!;
        Assert.Equal(colors, m.ColorsMax);
        Assert.Equal(colors, m.DefaultColors.Count);
        Assert.All(m.DefaultColors, c => Assert.Matches("^#[0-9A-F]{6}$", c));
    }

    [Fact]
    public void Custom_and_static_have_no_speed_or_direction()
    {
        var stat = LianLiLightingModes.Find(LianLiFanFamily.SlInfinity, "static")!;
        Assert.False(LianLiLightingModes.Custom.HasSpeed);
        Assert.False(LianLiLightingModes.Custom.HasDirection);
        Assert.False(stat.HasSpeed);
        Assert.False(stat.HasDirection);
    }

    [Fact]
    public void Find_returns_null_for_unknown_key()
    {
        Assert.Null(LianLiLightingModes.Find(LianLiFanFamily.SlInfinity, "unknown"));
        Assert.Null(LianLiLightingModes.Find(LianLiFanFamily.SlInfinity, ""));
        Assert.Null(LianLiLightingModes.Find(LianLiFanFamily.SlInfinity, "taichi"));
    }

    [Theory]
    [InlineData(LianLiFanFamily.SlInfinity)]
    [InlineData(LianLiFanFamily.Al)]
    [InlineData(LianLiFanFamily.AlV2)]
    public void Two_ring_families_have_a_catalog_per_ring_with_unique_keys_and_static(LianLiFanFamily family)
    {
        foreach (var outer in new[] { false, true })
        {
            var catalog = LianLiLightingModes.RingCatalogFor(family, outer);
            Assert.NotEmpty(catalog);
            Assert.Equal(catalog.Count, catalog.Select(m => m.Key).Distinct().Count());
            Assert.NotNull(LianLiLightingModes.FindRing(family, outer, "static"));
            Assert.DoesNotContain(catalog, m => m.Key == "custom");
        }
    }

    [Theory]
    [InlineData(LianLiFanFamily.Sl)]
    [InlineData(LianLiFanFamily.SlV2)]
    public void One_ring_families_have_no_ring_catalog(LianLiFanFamily family)
    {
        Assert.Empty(LianLiLightingModes.RingCatalogFor(family, outer: false));
        Assert.Empty(LianLiLightingModes.RingCatalogFor(family, outer: true));
    }

    [Fact]
    public void The_same_ring_byte_names_different_effects_on_each_ring()
    {
        Assert.Equal(0x1C, LianLiLightingModes.FindRing(LianLiFanFamily.SlInfinity, outer: false, "taichi")!.EffectByte);
        Assert.Null(LianLiLightingModes.FindRing(LianLiFanFamily.SlInfinity, outer: true, "taichi"));
        Assert.Equal(0x30, LianLiLightingModes.FindRing(LianLiFanFamily.SlInfinity, outer: true, "reflect")!.EffectByte);
        Assert.True(LianLiLightingModes.FindRing(LianLiFanFamily.Al, outer: true, "staticColorful")!.CornerPalette);
    }

    [Theory]
    [InlineData(0, 0x02)]
    [InlineData(2, 0x00)]
    [InlineData(4, 0xFE)]
    public void SpeedCodes_index_to_byte(int index, byte expected)
    {
        Assert.Equal(expected, LianLiLightingModes.SpeedCodes[index]);
    }

    [Theory]
    [InlineData(0, 0x08)]
    [InlineData(4, 0x00)]
    public void BrightnessCodes_index_to_byte(int index, byte expected)
    {
        Assert.Equal(expected, LianLiLightingModes.BrightnessCodes[index]);
    }

    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(1, 0x01)]
    [InlineData(99, 0x00)]
    public void DirectionByte_maps_one_to_rtl(int direction, byte expected)
    {
        Assert.Equal(expected, LianLiLightingModes.DirectionByte(direction));
    }
}
