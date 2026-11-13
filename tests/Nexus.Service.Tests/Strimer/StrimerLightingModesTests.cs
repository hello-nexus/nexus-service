using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Strimer;

public class StrimerLightingModesTests
{
    // ── Speed codes ──

    [Fact]
    public void SpeedCodes_has_five_entries_with_correct_bytes()
    {
        Assert.Equal(5, StrimerLightingModes.SpeedCodes.Length);
        Assert.Equal(0x02, StrimerLightingModes.SpeedCodes[0]);
        Assert.Equal(0x01, StrimerLightingModes.SpeedCodes[1]);
        Assert.Equal(0x00, StrimerLightingModes.SpeedCodes[2]);
        Assert.Equal(0xFF, StrimerLightingModes.SpeedCodes[3]);
        Assert.Equal(0xFE, StrimerLightingModes.SpeedCodes[4]);
    }

    // ── Brightness codes ──

    [Fact]
    public void BrightnessCodes_has_five_entries_with_correct_bytes()
    {
        Assert.Equal(5, StrimerLightingModes.BrightnessCodes.Length);
        Assert.Equal(0x08, StrimerLightingModes.BrightnessCodes[0]);
        Assert.Equal(0x03, StrimerLightingModes.BrightnessCodes[1]);
        Assert.Equal(0x02, StrimerLightingModes.BrightnessCodes[2]);
        Assert.Equal(0x01, StrimerLightingModes.BrightnessCodes[3]);
        Assert.Equal(0x00, StrimerLightingModes.BrightnessCodes[4]);
    }

    // ── Direction (inverted vs SLI) ──

    [Theory]
    [InlineData(0, 0x01)]
    [InlineData(1, 0x00)]
    public void DirectionByte_inverts_direction(int direction, byte expected)
    {
        Assert.Equal(expected, StrimerLightingModes.DirectionByte(direction));
    }

    // ── Catalog ──

    [Fact]
    public void Catalog_has_28_modes()
    {
        Assert.Equal(28, StrimerLightingModes.Catalog.Length);
    }

    [Theory]
    [InlineData("off",           0x00)]
    [InlineData("custom",        0x01)]
    [InlineData("static",        0x01)]
    [InlineData("breathing",     0x02)]
    [InlineData("flashing",      0x03)]
    [InlineData("rainbowMorph",  0x04)]
    [InlineData("rainbow",       0x05)]
    [InlineData("spectrumCycle", 0x06)]
    [InlineData("wave",          0x18)]
    [InlineData("snooker",       0x19)]
    [InlineData("mixing",        0x1A)]
    [InlineData("pingPong",      0x1B)]
    [InlineData("runway",        0x1C)]
    [InlineData("painting",      0x1D)]
    [InlineData("tide",          0x1E)]
    [InlineData("blowUp",        0x1F)]
    [InlineData("meteor",        0x20)]
    [InlineData("shockWave",     0x21)]
    [InlineData("ripple",        0x22)]
    [InlineData("voice",         0x23)]
    [InlineData("bulletStack",   0x24)]
    [InlineData("drizzling",     0x25)]
    [InlineData("fadeOut",       0x26)]
    [InlineData("colorTransfer", 0x27)]
    [InlineData("crossOver",     0x28)]
    [InlineData("twinkle",       0x29)]
    [InlineData("contest",       0x2A)]
    [InlineData("parallel",      0x2B)]
    public void ModeKeys_map_to_correct_effect_bytes(string key, byte expectedByte)
    {
        var mode = StrimerLightingModes.Find(key);
        Assert.NotNull(mode);
        Assert.Equal(expectedByte, mode!.EffectByte);
    }

    [Theory]
    [InlineData("drizzling", 6, true, true)]
    [InlineData("wave", 6, false, true)]
    [InlineData("mixing", 2, false, false)]
    [InlineData("runway", 2, false, false)]
    [InlineData("breathing", 1, false, false)]
    [InlineData("rainbow", 0, false, true)]
    [InlineData("bulletStack", 0, true, true)]
    [InlineData("twinkle", 0, true, false)]
    [InlineData("voice", 6, true, false)]
    [InlineData("shockWave", 6, true, true)]
    public void Mode_options_match_the_channel_effect_table(string key, int colors, bool complete, bool direction)
    {
        var mode = StrimerLightingModes.Find(key)!;
        Assert.Equal(colors, mode.ColorsMax);
        Assert.Equal(colors, mode.ColorsMin);
        Assert.Equal(complete, mode.Complete);
        Assert.Equal(direction, mode.HasDirection);
        Assert.True(mode.HasSpeed);
    }

    [Fact]
    public void Six_color_modes_default_to_the_shared_palette()
    {
        Assert.Equal(
            new[] { "#ff0000", "#ff6900", "#ffd700", "#00ff00", "#0000ff", "#aa00ff" },
            StrimerLightingModes.Find("drizzling")!.DefaultColors);
        Assert.Equal(new[] { "#ff0000", "#0000ff" }, StrimerLightingModes.Find("mixing")!.DefaultColors);
    }

    [Fact]
    public void Direction_byte_is_zero_for_a_mode_without_direction()
    {
        Assert.Equal(0x00, StrimerLightingModes.DirectionByte(StrimerLightingModes.Find("ripple")!, 0));
        Assert.Equal(0x01, StrimerLightingModes.DirectionByte(StrimerLightingModes.Find("drizzling")!, 0));
        Assert.Equal(0x00, StrimerLightingModes.DirectionByte(StrimerLightingModes.Find("drizzling")!, 1));
    }

    [Fact]
    public void ResolveColors_falls_back_per_slot_to_the_modes_default()
    {
        var mode = StrimerLightingModes.Find("drizzling")!;
        var ls = new StrimerLightingSettings();
        ls.ModeColors["drizzling"] = new List<string> { "#222222", "bad" };
        var resolved = StrimerLightingModes.ResolveColors(ls, mode);
        Assert.Equal(6, resolved.Length);
        Assert.Equal("#222222", resolved[0]);
        Assert.Equal("#ff6900", resolved[1]);
        Assert.Equal("#ff0000", StrimerLightingModes.ResolveColors(ls, StrimerLightingModes.Find("wave")!)[0]);
    }

    [Fact]
    public void Find_returns_null_for_unknown_key()
    {
        Assert.Null(StrimerLightingModes.Find("doesNotExist"));
    }
}
