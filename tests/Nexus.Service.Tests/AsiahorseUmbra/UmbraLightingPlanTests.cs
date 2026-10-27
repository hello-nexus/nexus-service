using System.Linq;
using Nexus.Service.Peripherals.AsiahorseUmbra;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.AsiahorseUmbra;

public class UmbraLightingPlanTests
{
    private static UmbraPort[] Ports(params int[] counts)
    {
        var ports = new UmbraPort[UmbraProtocol.PortCount];
        for (var i = 0; i < ports.Length; i++)
        {
            ports[i] = new UmbraPort(i < counts.Length ? counts[i] : 0, 0, true, i, 0);
        }
        return ports;
    }

    private static byte[] Cmds(byte[][] frames) => frames.Select(f => f[4]).ToArray();

    [Fact]
    public void Nexus_source_only_takes_authority()
    {
        var frames = UmbraLightingPlan.Build(new AsiahorseUmbraSettings(), Ports(0, 0, 8), 1f);

        Assert.Single(frames);
        Assert.Equal(UmbraProtocol.EncodeAuthority(true), frames[0]);
    }

    [Fact]
    public void ArgbInput_source_hands_the_leds_to_the_header()
    {
        var frames = UmbraLightingPlan.Build(new AsiahorseUmbraSettings { Source = "argbInput" }, Ports(8), 1f);

        Assert.Single(frames);
        Assert.Equal(UmbraProtocol.EncodeAuthority(false), frames[0]);
    }

    [Fact]
    public void Firmware_sync_frames_follow_the_port_color_order()
    {
        var settings = new AsiahorseUmbraSettings { Source = "firmware" };
        var plain = UmbraLightingPlan.Build(settings, Ports(8), 1f);
        var prefs = new System.Collections.Generic.Dictionary<string, LightingDevicePreference>
        {
            [Nexus.Service.Lighting.AsiahorseUmbraLightingDeviceProvider.PortId(0)] = new() { ColorOrder = "GRB" },
        };
        var ordered = UmbraLightingPlan.Build(settings, Ports(8), 1f, prefs);

        Assert.NotEqual(UmbraLightingPlan.Signature(plain), UmbraLightingPlan.Signature(ordered));
    }

    [Fact]
    public void Firmware_sync_takes_authority_then_plays_the_sync_effect()
    {
        var frames = UmbraLightingPlan.Build(new AsiahorseUmbraSettings { Source = "firmware" }, Ports(8), 1f);

        Assert.Equal(new byte[] { 0xFD, 0x03 }, Cmds(frames));
        Assert.Equal(0xFF, frames[1][5]);
        Assert.Equal(0xFF, frames[1][6]);
        // Rainbow at the vendor defaults: speed 3, forward, 60 % brightness.
        Assert.Equal(new byte[] { 0x01, 0x03, 0x01, 0x99 }, frames[1][7..11]);
    }

    [Fact]
    public void Firmware_with_light_off_sends_the_off_command()
    {
        var frames = UmbraLightingPlan.Build(new AsiahorseUmbraSettings { Source = "firmware", LightOn = false }, Ports(8), 1f);

        Assert.Equal(new byte[] { 0xFD, 0x06 }, Cmds(frames));
    }

    [Fact]
    public void Firmware_at_zero_master_brightness_sends_the_off_command()
    {
        var frames = UmbraLightingPlan.Build(new AsiahorseUmbraSettings { Source = "firmware" }, Ports(8), 0f);

        Assert.Equal(new byte[] { 0xFD, 0x06 }, Cmds(frames));
    }

    [Fact]
    public void Firmware_merge_uses_the_merge_selector_and_ids()
    {
        var s = new AsiahorseUmbraSettings { Source = "firmware", Group = "merge" };
        s.Merge.Effect = "runway";

        var frames = UmbraLightingPlan.Build(s, Ports(8), 1f);

        Assert.Equal(0xFE, frames[1][5]);
        Assert.Equal(0xFE, frames[1][6]);
        // Merge runway is id 1; merge effects default to speed level 4, wire byte 02 as the vendor app sends it.
        Assert.Equal(new byte[] { 0x01, 0x02 }, frames[1][7..9]);
    }

    [Fact]
    public void Firmware_async_sends_one_frame_per_port_with_leds()
    {
        var s = new AsiahorseUmbraSettings { Source = "firmware", Group = "async" };

        var frames = UmbraLightingPlan.Build(s, Ports(8, 0, 12), 1f);

        Assert.Equal(new byte[] { 0xFD, 0x03, 0x03 }, Cmds(frames));
        Assert.Equal(0xFD, frames[1][5]);
        Assert.Equal(0, frames[1][6]);
        Assert.Equal(0xFD, frames[2][5]);
        Assert.Equal(2, frames[2][6]);
    }

    [Fact]
    public void Merge_and_async_keep_the_plain_defaults_for_fade_out_and_gradient()
    {
        // Captured: merge Fade Out and async Fade Out run forward; async Gradient Rainbow at brightness 153.
        Assert.Equal("forward", UmbraEffects.Defaults("merge", UmbraEffects.Find("fadeOut")!).Direction);
        Assert.Equal("forward", UmbraEffects.Defaults("async", UmbraEffects.Find("fadeOut")!).Direction);
        Assert.Equal(3, UmbraEffects.Defaults("async", UmbraEffects.Find("gradientRainbow")!).Brightness);
        Assert.Equal("reverse", UmbraEffects.Defaults("sync", UmbraEffects.Find("fadeOut")!).Direction);
        Assert.Equal(5, UmbraEffects.Defaults("sync", UmbraEffects.Find("gradientRainbow")!).Brightness);
    }

    [Fact]
    public void Async_static_sends_its_four_colors_unreplicated()
    {
        var s = new AsiahorseUmbraSettings { Source = "firmware", Group = "async" };
        s.Async["0"] = new UmbraEffectGroupSettings { Effect = "static" };

        var frames = UmbraLightingPlan.Build(s, Ports(6), 1f);

        // Captured vendor frame for async Static: effect 0 in the segment slot, default colors as stored.
        Assert.Equal(0x00, frames[1][13]);
        Assert.Equal(
            new byte[] { 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00 },
            frames[1][23..35]);
    }

    [Fact]
    public void Async_uses_the_port_state_when_present()
    {
        var s = new AsiahorseUmbraSettings { Source = "firmware", Group = "async" };
        s.Async["2"] = new UmbraEffectGroupSettings { Effect = "breath" };

        var frames = UmbraLightingPlan.Build(s, Ports(0, 0, 12), 1f);

        // Breath is sync id 3 and lands in the segment slot of the async block.
        Assert.Equal(3, frames[1][13]);
    }

    [Fact]
    public void An_effect_missing_from_the_merge_group_falls_back_to_rainbow()
    {
        var s = new AsiahorseUmbraSettings { Source = "firmware", Group = "merge" };
        s.Merge.Effect = "breath";

        var frames = UmbraLightingPlan.Build(s, Ports(8), 1f);

        Assert.Equal(0x00, frames[1][7]);
    }

    [Fact]
    public void An_unknown_stored_source_is_treated_as_nexus()
    {
        var frames = UmbraLightingPlan.Build(new AsiahorseUmbraSettings { Source = "bogus" }, Ports(8), 1f);

        Assert.Single(frames);
        Assert.Equal(UmbraProtocol.EncodeAuthority(true), frames[0]);
    }

    [Fact]
    public void Stored_values_restore_per_effect()
    {
        var s = new AsiahorseUmbraSettings { Source = "firmware" };
        s.Sync.Effect = "runway";
        s.Sync.Values["runway"] = new UmbraEffectValues { Speed = 5, Brightness = 5, Direction = "reverse", Colors = { "#112233", "#000000", "#000000", "#000000" } };

        var frames = UmbraLightingPlan.Build(s, Ports(8), 1f);

        Assert.Equal(new byte[] { 0x05, 0x01, 0x00, 0xFF, 0x11, 0x22, 0x33 }, frames[1][7..14]);
    }

    [Fact]
    public void Static_repeats_color_one_in_every_slot()
    {
        var s = new AsiahorseUmbraSettings { Source = "firmware" };
        s.Sync.Effect = "static";
        s.Sync.Values["static"] = new UmbraEffectValues { Colors = { "#0a0b0c", "#ffffff", "#ffffff", "#ffffff" } };

        var frames = UmbraLightingPlan.Build(s, Ports(8), 1f);

        Assert.Equal(new byte[] { 10, 11, 12, 10, 11, 12, 10, 11, 12, 10, 11, 12 }, frames[1][11..23]);
    }

    [Theory]
    [InlineData(3, 1f, 153)]
    [InlineData(5, 1f, 255)]
    [InlineData(5, 0.5f, 153)]
    [InlineData(5, 0.998f, 255)]
    [InlineData(1, 1f, 51)]
    [InlineData(3, 0.1f, 51)]
    [InlineData(3, 0.05f, 50)]
    public void MasterBrightness_caps_the_firmware_level_and_never_drops_below_the_vendor_minimum(int level, float master, int expected)
    {
        Assert.Equal(expected, UmbraEffects.ScaleBrightness(level, master));
    }

    [Fact]
    public void A_continuous_master_ramp_commits_once_per_firmware_level()
    {
        var s = new AsiahorseUmbraSettings { Source = "firmware" };
        s.Sync.Values["rainbow"] = new UmbraEffectValues { Brightness = 5 };
        int? last = null;
        var commits = 0;
        for (var i = 1000; i >= 1; i--)
        {
            var sig = UmbraLightingPlan.Signature(UmbraLightingPlan.Build(s, Ports(8), i / 1000f));
            if (sig != last)
            {
                commits++;
                last = sig;
            }
        }

        Assert.True(commits <= 6, $"commits {commits}");
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(3, 3)]
    [InlineData(5, 1)]
    public void Speed_maps_the_percent_steps_to_the_wire_byte(int speed, int expected)
    {
        Assert.Equal(expected, UmbraEffects.SpeedByte(speed));
    }

    [Fact]
    public void Vendor_defaults_hold_per_effect()
    {
        var gradient = UmbraEffects.Defaults("sync", UmbraEffects.Find("gradientRainbow")!);
        var fadeOut = UmbraEffects.Defaults("sync", UmbraEffects.Find("fadeOut")!);
        var mergeTile = UmbraEffects.Defaults("merge", UmbraEffects.Find("tile")!);
        var staticFx = UmbraEffects.Defaults("sync", UmbraEffects.Find("static")!);
        var plain = UmbraEffects.Defaults("sync", UmbraEffects.Find("breath")!);

        Assert.Equal(5, gradient.Brightness);
        Assert.Equal("reverse", fadeOut.Direction);
        Assert.Equal(4, mergeTile.Speed);
        Assert.All(staticFx.Colors, c => Assert.Equal("#ff0000", c));
        Assert.Equal(new[] { "#ff0000", "#00ff00", "#0000ff", "#ffff00" }, plain.Colors);
        Assert.Equal(3, plain.Speed);
        Assert.Equal(3, plain.Brightness);
    }

    [Fact]
    public void Catalog_matches_the_wire_tables()
    {
        Assert.Equal(15, UmbraEffects.Catalog.Length);
        Assert.Equal(Enumerable.Range(0, 15), UmbraEffects.Catalog.Select(e => e.SyncId));
        var merge = UmbraEffects.Catalog.Where(e => e.InMerge).OrderBy(e => e.MergeId).Select(e => e.Key).ToArray();
        Assert.Equal(new[] { "rainbow", "runway", "tile", "fadeOut", "trail", "scan", "brick", "pulse", "flash", "address" }, merge);
    }

    [Fact]
    public void Signature_changes_with_the_frames()
    {
        var a = UmbraLightingPlan.Build(new AsiahorseUmbraSettings { Source = "firmware" }, Ports(8), 1f);
        var b = UmbraLightingPlan.Build(new AsiahorseUmbraSettings { Source = "firmware" }, Ports(8), 0.3f);

        Assert.Equal(UmbraLightingPlan.Signature(a), UmbraLightingPlan.Signature(a));
        Assert.NotEqual(UmbraLightingPlan.Signature(a), UmbraLightingPlan.Signature(b));
    }

    [Fact]
    public void Null_collections_from_hand_edited_settings_do_not_throw()
    {
        var s = new AsiahorseUmbraSettings { Source = "firmware", Sync = null!, Merge = null!, Async = null! };

        var sync = UmbraLightingPlan.Build(s, Ports(8), 1f);
        s.Group = "merge";
        var merge = UmbraLightingPlan.Build(s, Ports(8), 1f);
        s.Group = "async";
        var async = UmbraLightingPlan.Build(s, Ports(8), 1f);

        Assert.Equal(2, sync.Length);
        Assert.Equal(2, merge.Length);
        Assert.Equal(2, async.Length);
    }

    [Fact]
    public void Null_values_and_colors_fall_back_to_the_vendor_defaults()
    {
        var s = new AsiahorseUmbraSettings { Source = "firmware" };
        s.Sync.Values = null!;
        var viaNullValues = UmbraLightingPlan.Build(s, Ports(8), 1f);
        s.Sync.Values = new() { ["rainbow"] = new UmbraEffectValues { Colors = null! } };
        var viaNullColors = UmbraLightingPlan.Build(s, Ports(8), 1f);

        Assert.Equal(viaNullValues[1], viaNullColors[1]);
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00 }, viaNullColors[1][11..14]);
    }
}
