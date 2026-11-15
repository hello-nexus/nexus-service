using System.Collections.Generic;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Strimer;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;

namespace Nexus.Service.Tests.Strimer;

public class StrimerEffectLooksTests
{
    [Theory]
    [InlineData("ch0", "ch0")]
    [InlineData("ch1", "ch1")]
    [InlineData("p0", "p0")]
    [InlineData("p11", "p11")]
    [InlineData("p12", null)]
    [InlineData("p01", null)]
    [InlineData("ch2", null)]
    [InlineData("all", null)]
    [InlineData(null, null)]
    public void NormalizeTarget_accepts_only_cables_and_the_twelve_ports(string? input, string? expected)
    {
        Assert.Equal(expected, StrimerEffectLooks.NormalizeTarget(input));
    }

    [Fact]
    public void A_target_with_no_look_of_its_own_shows_its_cable_then_the_shared_effect()
    {
        var ls = new StrimerLightingSettings { Mode = "wave" };
        Assert.Equal("wave", StrimerEffectLooks.Resolve(ls, "p8").Mode.Key);

        ls.Targets["ch1"] = new StrimerTargetLook { Mode = "tide" };
        Assert.Equal("tide", StrimerEffectLooks.Resolve(ls, "p8").Mode.Key);
        Assert.Equal("wave", StrimerEffectLooks.Resolve(ls, "p2").Mode.Key);
    }

    [Fact]
    public void Seeding_copies_what_the_target_showed_including_its_speed_and_palette()
    {
        var ls = new StrimerLightingSettings { Mode = "mixing", Direction = 1, Brightness = 3 };
        ls.ModeColors["mixing"] = new List<string> { "#010203", "#040506" };
        var seeded = StrimerEffectLooks.Seed(ls, "ch0");

        Assert.Equal("mixing", seeded.Mode);
        Assert.Equal(1, seeded.Direction);
        Assert.Equal(3, seeded.Brightness);
        Assert.Equal(2, seeded.ModeSpeeds["mixing"]);
        Assert.Equal(new List<string> { "#010203", "#040506" }, seeded.ModeColors["mixing"]);
    }

    [Fact]
    public void The_shared_effect_shows_the_effect_mode_while_the_Lighting_page_drives_the_cable()
    {
        var ls = new StrimerLightingSettings { Mode = "custom", EffectMode = "drizzling" };
        var look = StrimerEffectLooks.Shared(ls);
        Assert.Equal("drizzling", look.Mode.Key);
        Assert.Equal(4, look.Speed);
        Assert.Equal(6, look.Colors.Length);
    }

    private static StrimerLightingSettings Migrated(string strimerJson, out NexusSettings settings)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-strimer-mig-" + System.Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var path = System.IO.Path.Combine(dir, "settings.json");
            System.IO.File.WriteAllText(
                path,
                strimerJson == ""
                    ? "{\"schemaVersion\":32,\"devices\":{}}"
                    : "{\"schemaVersion\":32,\"devices\":{\"strimerLighting\":" + strimerJson + "}}");
            var store = new JsonConfigStore(path);
            try
            {
                settings = store.Load();
                return settings.Devices.StrimerLighting;
            }
            finally
            {
                store.Dispose();
            }
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Settings_saved_before_per_cable_effects_load_as_every_port_using_the_saved_effect()
    {
        var ls = Migrated("{\"mode\":\"breathing\",\"speed\":2,\"colors\":[\"#112233\"]}", out var s);
        Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
        Assert.Empty(ls.Targets);
        var plan = StrimerEffectLooks.BuildPlan(ls, new MasterLevel(1f, false));
        Assert.Equal(12, plan.Count);
        Assert.All(plan, z => Assert.Equal(0x02, z.Mode.EffectByte));
        Assert.Equal(0x11, plan[0].Colors[0]);
        Assert.Equal(0x00, plan[0].Speed);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 3)]
    public void Migration_keeps_the_speed_byte_the_effect_already_sent(int oldLevel, int newLevel)
    {
        var oldBytes = new byte[] { 0x02, 0x01, 0x00, 0xFE, 0xFF };
        var ls = Migrated("{\"mode\":\"wave\",\"speed\":" + oldLevel + "}", out _);
        var plan = StrimerEffectLooks.BuildPlan(ls, new MasterLevel(1f, false));
        Assert.Equal(newLevel, ls.ModeSpeeds["wave"]);
        Assert.Equal(oldBytes[oldLevel], plan[0].Speed);
    }

    [Fact]
    public void An_untouched_saved_block_still_keeps_its_effect_at_the_speed_it_showed()
    {
        var ls = Migrated("{\"mode\":\"rainbow\",\"speed\":2}", out _);
        Assert.Equal(2, ls.ModeSpeeds["rainbow"]);
        Assert.Equal(0x00, StrimerEffectLooks.BuildPlan(ls, new MasterLevel(1f, false))[0].Speed);
        Assert.Equal(4, StrimerEffectLooks.Shared(new StrimerLightingSettings { Mode = "wave" }).Speed);
    }

    [Fact]
    public void A_block_that_already_has_the_per_effect_keys_is_left_alone()
    {
        var ls = Migrated("{\"mode\":\"breathing\",\"speed\":3,\"colors\":[\"#111111\"],\"modeSpeeds\":{},\"modeColors\":{}}", out var s);
        Assert.Equal(NexusSettings.CurrentSchemaVersion, s.SchemaVersion);
        Assert.Empty(ls.ModeSpeeds);
        Assert.Equal(3, ls.Speed);
        Assert.Single(ls.Colors);
    }

    [Fact]
    public void Migrating_twice_changes_nothing_the_second_time()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexus-strimer-mig-" + System.Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var path = System.IO.Path.Combine(dir, "settings.json");
            System.IO.File.WriteAllText(path, "{\"schemaVersion\":32,\"devices\":{\"strimerLighting\":{\"mode\":\"wave\",\"speed\":3,\"colors\":[\"#010203\"]}}}");
            var first = new JsonConfigStore(path);
            first.Load();
            first.Dispose();
            var second = new JsonConfigStore(path);
            try
            {
                var ls = second.Load().Devices.StrimerLighting;
                Assert.Equal(4, ls.Speed);
                Assert.Equal(4, ls.ModeSpeeds["wave"]);
                Assert.Equal(new List<string> { "#010203" }, ls.ModeColors["wave"]);
                Assert.Single(ls.Colors);
            }
            finally
            {
                second.Dispose();
            }
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_document_with_no_Strimer_block_is_left_without_saved_speeds()
    {
        var ls = Migrated("", out _);
        Assert.Empty(ls.ModeSpeeds);
        Assert.Empty(ls.ModeColors);
    }

    [Fact]
    public void Migration_moves_the_shared_colors_to_the_effect_they_were_set_for_and_keeps_the_old_list()
    {
        var ls = Migrated("{\"mode\":\"mixing\",\"colors\":[\"#010203\",\"#040506\"]}", out _);
        Assert.Equal(2, ls.Colors.Count);
        Assert.Equal(new List<string> { "#010203", "#040506" }, ls.ModeColors["mixing"]);
        Assert.Equal("#ff0000", StrimerLightingModes.ResolveColors(ls, StrimerLightingModes.Find("wave")!)[0]);
    }

    [Fact]
    public void The_four_strip_harness_gets_four_ports_and_the_matching_latch_mask()
    {
        var ls = new StrimerLightingSettings { Mode = "rainbow" };
        var plan = StrimerEffectLooks.BuildPlan(ls, new MasterLevel(1f, false), StrimerProtocol.GpuZoneCountDual);
        Assert.Equal(10, plan.Count);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 }, plan.ConvertAll(z => z.Zone));

        var hid = new StrimerFirmwareCommitTests.FakeHid();
        var hub = new StrimerHub();
        hub.Attach(hid);
        StrimerLightingFrameWriter.CommitFirmwareMode(hub, plan);
        var latch = hid.Writes[^1];
        Assert.Equal(new byte[] { 0xE0, 0x2C, 0x03, 0xFF }, new[] { latch[0], latch[1], latch[2], latch[3] });
    }

    [Fact]
    public void Migration_follows_the_effect_the_page_shows_when_the_Lighting_page_drives_the_cable()
    {
        var ls = Migrated("{\"mode\":\"custom\",\"effectMode\":\"wave\",\"speed\":3,\"colors\":[\"#010203\"]}", out _);
        Assert.Equal(4, ls.ModeSpeeds["wave"]);
        Assert.Equal(new List<string> { "#010203" }, ls.ModeColors["wave"]);
    }

    [Fact]
    public void Migration_falls_back_to_rainbow_with_no_remembered_effect_or_an_unknown_one()
    {
        Assert.Equal(4, Migrated("{\"mode\":\"custom\",\"speed\":3}", out _).ModeSpeeds["rainbow"]);
        Assert.Equal(4, Migrated("{\"mode\":\"gone\",\"speed\":3}", out _).ModeSpeeds["rainbow"]);
    }

    [Fact]
    public void Switching_from_per_port_effects_to_a_channel_wide_one_rewrites_only_the_first_port_of_each_cable()
    {
        var hid = new StrimerFirmwareCommitTests.FakeHid();
        var hub = new StrimerHub();
        hub.Attach(hid);
        var ls = new StrimerLightingSettings { Mode = "rainbow" };
        StrimerLightingFrameWriter.CommitFirmwareMode(hub, StrimerEffectLooks.BuildPlan(ls, new MasterLevel(1f, false)));
        hid.Writes.Clear();

        ls.Mode = "drizzling";
        StrimerLightingFrameWriter.CommitFirmwareMode(hub, StrimerEffectLooks.BuildPlan(ls, new MasterLevel(1f, false)));

        Assert.Equal(new byte[] { 0x10, 0x30, 0x16, 0x36, 0x2C }, hid.Writes.ConvertAll(w => w[1]));
        Assert.Equal(new byte[] { 0x00, 0x41 }, new[] { hid.Writes[4][2], hid.Writes[4][3] });
    }

    [Theory]
    [InlineData("ch0", "ripple", false, null)]
    [InlineData("p3", "ripple", false, "channel-wide")]
    [InlineData("p3", "wave", false, null)]
    [InlineData("p3", "ripple", true, null)]
    [InlineData("p3", "custom", false, "Lighting page")]
    [InlineData("ch1", "custom", false, "Lighting page")]
    [InlineData("p10", "wave", false, "harness")]
    [InlineData("p9", "wave", false, null)]
    [InlineData("zz", "wave", false, "unknown target")]
    public void Patch_validation_for_a_target(string target, string mode, bool reset, string? errorPart)
    {
        var request = new StrimerLightingRequest { Target = target, Mode = mode, ResetTarget = reset };
        var error = DevicesRoutes.ValidateStrimerPatch(new StrimerLightingSettings { Mode = "rainbow" }, StrimerProtocol.GpuZoneCountDual, request);
        if (errorPart == null) Assert.Null(error); else Assert.Contains(errorPart, error);
    }

    [Fact]
    public void A_port_inheriting_a_channel_wide_effect_refuses_an_edit_that_names_no_per_port_effect()
    {
        var ls = new StrimerLightingSettings { Mode = "drizzling" };
        Assert.NotNull(DevicesRoutes.ValidateStrimerPatch(ls, 6, new StrimerLightingRequest { Target = "p2", Speed = 1 }));
        Assert.Null(DevicesRoutes.ValidateStrimerPatch(ls, 6, new StrimerLightingRequest { Target = "p2", Mode = "wave" }));
    }

    [Fact]
    public void Patch_validation_rejects_a_color_that_is_not_a_hex_triplet()
    {
        var ls = new StrimerLightingSettings();
        Assert.NotNull(DevicesRoutes.ValidateStrimerPatch(ls, 6, new StrimerLightingRequest { Colors = new[] { "red" } }));
        Assert.Null(DevicesRoutes.ValidateStrimerPatch(ls, 6, new StrimerLightingRequest { Colors = new[] { "#aabbcc" } }));
    }
}
