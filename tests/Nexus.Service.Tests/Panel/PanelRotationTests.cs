using System;
using System.IO;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Panel;

public class PanelRotationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panel-rotation-" + Guid.NewGuid().ToString("N")[..8]);

    public PanelRotationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static PanelDeviceCapabilities Caps(string surface, int width = 480, int height = 480, bool portrait = false) => new()
    {
        Surface = surface,
        CssWidth = width,
        CssHeight = height,
        SupportsPortrait = portrait ? true : null,
    };

    private static PanelDeviceRecord Record(PanelDeviceCapabilities caps) => new() { Id = "p1", Capabilities = caps };

    [Theory]
    [InlineData(PanelSurfaces.LcdSquare, 480, 480, new[] { 0, 90, 180, 270 })]
    [InlineData(PanelSurfaces.LcdRound, 400, 400, new[] { 0, 90, 180, 270 })]
    // Glass that is not square renders the swapped size at a quarter turn (Panel/PanelPortraitTests covers the families).
    [InlineData(PanelSurfaces.LcdSquare, 640, 480, new[] { 0, 90, 180, 270 })]
    [InlineData(PanelSurfaces.LcdSquare, 320, 240, new[] { 0, 90, 180, 270 })]
    [InlineData(PanelSurfaces.LcdSquare, 720, 1280, new[] { 0, 90, 180, 270 })]
    [InlineData(PanelSurfaces.LcdWide, 1600, 720, new[] { 0, 90, 180, 270 })]
    // A glass whose size is unknown yet cannot be turned on a guess.
    [InlineData(PanelSurfaces.LcdWide, 0, 0, new[] { 0, 180 })]
    // Surfaces no orientation filter reaches take none.
    [InlineData(PanelSurfaces.LegacyKraken, 320, 320, new[] { 0 })]
    [InlineData(PanelSurfaces.Y70, 0, 0, new[] { 0 })]
    [InlineData(PanelSurfaces.Phone, 0, 0, new[] { 0 })]
    [InlineData(PanelSurfaces.Monitor, 1920, 480, new[] { 0 })]
    public void Supported_rotations_follow_the_surface_and_the_glass_shape(string surface, int width, int height, int[] expected)
    {
        Assert.Equal(expected, PanelRotation.SupportedFor(Caps(surface, width, height)));
    }

    [Fact]
    public void The_universal_screen_takes_every_rotation_through_its_portrait_mode()
    {
        Assert.Equal(new[] { 0, 90, 180, 270 }, PanelRotation.SupportedFor(Caps(PanelSurfaces.Monitor, 1920, 480, portrait: true)));
        Assert.Equal(new[] { 0 }, PanelRotation.SupportedFor(null));
    }

    [Fact]
    public void The_registry_clone_carries_rotation_and_what_the_glass_supports()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var record = registry.Allocate("Cooler", Caps(PanelSurfaces.LcdSquare));

        var clone = registry.Get(record.Id)!;

        Assert.Equal(0, clone.Rotation);
        Assert.Equal(new[] { 0, 90, 180, 270 }, clone.SupportedRotations);
        Assert.Null(store.Load().PanelDevices[record.Id].SupportedRotations);
        Assert.Null(clone.Flip180);
        Assert.Null(clone.Portrait);
    }

    [Fact]
    public void A_patch_stores_a_supported_rotation_and_a_zero_clears_it()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var record = registry.Allocate("Cooler", Caps(PanelSurfaces.LcdSquare));

        var turned = registry.Patch(record.Id, new PanelDevicePatch { Rotation = 270 })!;
        Assert.Equal(270, turned.Rotation);
        Assert.Equal(270, store.Load().PanelDevices[record.Id].Rotation);

        var cleared = registry.Patch(record.Id, new PanelDevicePatch { Rotation = 0 })!;
        Assert.Equal(0, cleared.Rotation);
        Assert.Null(store.Load().PanelDevices[record.Id].Rotation);
    }

    [Theory]
    [InlineData(PanelSurfaces.LcdSquare, 640, 480, 135)]
    [InlineData(PanelSurfaces.LcdWide, 1600, 720, 360)]
    [InlineData(PanelSurfaces.LcdSquare, 480, 480, 45)]
    [InlineData(PanelSurfaces.LcdSquare, 480, 480, 360)]
    [InlineData(PanelSurfaces.LcdSquare, 480, 480, -90)]
    [InlineData(PanelSurfaces.Y70, 0, 0, 180)]
    public void A_rotation_the_glass_does_not_take_is_rejected_and_not_stored(string surface, int width, int height, int rotation)
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var record = registry.Allocate("Cooler", Caps(surface, width, height));
        var patch = new PanelDevicePatch { Rotation = rotation };

        Assert.False(PanelRotation.TryResolve(registry.Get(record.Id)!, patch, out _));
        registry.Patch(record.Id, patch);

        Assert.Null(store.Load().PanelDevices[record.Id].Rotation);
    }

    [Fact]
    public void Mirror_alone_leaves_the_rotation_alone()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var record = registry.Allocate("Cooler", Caps(PanelSurfaces.LcdSquare));
        registry.Patch(record.Id, new PanelDevicePatch { Rotation = 90 });

        var after = registry.Patch(record.Id, new PanelDevicePatch { Mirror = true })!;

        Assert.Equal(90, after.Rotation);
        Assert.True(after.Mirror);
    }

    [Fact]
    public void The_legacy_flip_alias_maps_to_a_half_turn()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var record = registry.Allocate("Cooler", Caps(PanelSurfaces.LcdSquare));

        Assert.Equal(180, registry.Patch(record.Id, new PanelDevicePatch { Flip180 = true })!.Rotation);
        Assert.Equal(0, registry.Patch(record.Id, new PanelDevicePatch { Flip180 = false })!.Rotation);
        Assert.Null(store.Load().PanelDevices[record.Id].Flip180);
    }

    [Fact]
    public void An_explicit_rotation_beats_the_legacy_aliases()
    {
        var current = Record(Caps(PanelSurfaces.LcdSquare));

        Assert.True(PanelRotation.TryResolve(current, new PanelDevicePatch { Rotation = 90, Flip180 = true }, out var rotation));
        Assert.Equal(90, rotation);
    }

    [Fact]
    public void The_legacy_aliases_change_one_component_of_the_strip_and_keep_the_other()
    {
        var registry = new PanelDeviceRegistry(new InMemoryConfigStore());
        var record = registry.Allocate("Strip", Caps(PanelSurfaces.Monitor, 1920, 480, portrait: true));

        Assert.Equal(90, registry.Patch(record.Id, new PanelDevicePatch { Portrait = true })!.Rotation);
        Assert.Equal(270, registry.Patch(record.Id, new PanelDevicePatch { Flip180 = true })!.Rotation);
        Assert.Equal(180, registry.Patch(record.Id, new PanelDevicePatch { Portrait = false })!.Rotation);
        Assert.Equal(90, registry.Patch(record.Id, new PanelDevicePatch { Portrait = true, Flip180 = false })!.Rotation);
    }

    [Fact]
    public void The_legacy_portrait_alias_is_ignored_where_the_glass_has_no_portrait_mode()
    {
        var current = Record(Caps(PanelSurfaces.LcdSquare));

        Assert.True(PanelRotation.TryResolve(current, new PanelDevicePatch { Portrait = true }, out var rotation));
        Assert.Equal(0, rotation);
    }

    [Fact]
    public void A_legacy_flip_is_rejected_on_a_surface_that_cannot_turn()
    {
        Assert.False(PanelRotation.TryResolve(Record(Caps(PanelSurfaces.Y70)), new PanelDevicePatch { Flip180 = true }, out _));
        Assert.True(PanelRotation.TryResolve(Record(Caps(PanelSurfaces.Y70)), new PanelDevicePatch { Flip180 = false }, out _));
    }

    [Fact]
    public void Resetting_hardware_settings_returns_the_rotation_to_zero()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var record = registry.Allocate("Cooler", Caps(PanelSurfaces.LcdSquare));
        registry.Patch(record.Id, new PanelDevicePatch { Rotation = 180, Mirror = true });

        var reset = registry.ResetHardwareSettings(record.Id)!;

        Assert.Equal(0, reset.Rotation);
        Assert.Null(reset.Mirror);
        Assert.Null(store.Load().PanelDevices[record.Id].Rotation);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 180)]
    [InlineData(false, true, 90)]
    [InlineData(true, true, 270)]
    public void The_strip_keeps_its_combined_output_across_the_migration(bool flip, bool portrait, int expected)
    {
        var settings = new NexusSettings();
        settings.PanelDevices["strip"] = new PanelDeviceRecord
        {
            Id = "strip",
            Capabilities = Caps(PanelSurfaces.Monitor, 1920, 480, portrait: true),
            Flip180 = flip ? true : null,
            Portrait = portrait ? true : null,
        };

        var changed = PanelRotationMigration.Apply(settings);

        var record = settings.PanelDevices["strip"];
        Assert.Equal(flip || portrait, changed);
        Assert.Equal(expected == 0 ? null : expected, record.Rotation);
        Assert.Null(record.Flip180);
        Assert.Null(record.Portrait);
    }

    [Fact]
    public void A_stored_flip_on_a_cooler_becomes_a_half_turn_and_one_that_never_took_effect_is_dropped()
    {
        var settings = new NexusSettings();
        settings.PanelDevices["cooler"] = new PanelDeviceRecord { Id = "cooler", Capabilities = Caps(PanelSurfaces.LcdSquare), Flip180 = true, Mirror = true };
        settings.PanelDevices["wide"] = new PanelDeviceRecord { Id = "wide", Capabilities = Caps(PanelSurfaces.LcdWide, 1600, 720), Flip180 = true };
        settings.PanelDevices["y70"] = new PanelDeviceRecord { Id = "y70", Capabilities = Caps(PanelSurfaces.Y70), Flip180 = true };
        settings.PanelDevices["off"] = new PanelDeviceRecord { Id = "off", Capabilities = Caps(PanelSurfaces.LcdSquare), Flip180 = false };

        Assert.True(PanelRotationMigration.Apply(settings));

        Assert.Equal(180, settings.PanelDevices["cooler"].Rotation);
        Assert.True(settings.PanelDevices["cooler"].Mirror);
        Assert.Equal(180, settings.PanelDevices["wide"].Rotation);
        Assert.Null(settings.PanelDevices["y70"].Rotation);
        Assert.Null(settings.PanelDevices["off"].Rotation);
        Assert.All(settings.PanelDevices.Values, r => Assert.Null(r.Flip180));
        Assert.False(PanelRotationMigration.Apply(settings));
    }

    [Theory]
    [InlineData("lianli-tl-lcd")]
    [InlineData("lianli-flex-lcd")]
    [InlineData("lianli-wireless-lcd")]
    public void The_fan_lcds_take_only_the_software_half_turn(string family)
    {
        var caps = Caps(PanelSurfaces.LcdRound, 400, 400);
        caps.Family = family;

        Assert.Equal(new[] { 0, 180 }, PanelRotation.SupportedFor(caps));
        Assert.False(PanelRotation.TryResolve(Record(caps), new PanelDevicePatch { Rotation = 90 }, out _));
    }

    [Fact]
    public void A_stored_fan_panel_flip_becomes_a_half_turn_with_no_fold_into_the_screen_setting()
    {
        var settings = new NexusSettings();
        var caps = Caps(PanelSurfaces.LcdRound, 400, 400);
        caps.Family = "lianli-tl-lcd";
        settings.PanelDevices["fan"] = new PanelDeviceRecord { Id = "fan", Capabilities = caps, Flip180 = true };

        PanelRotationMigration.Apply(settings);

        Assert.Equal(180, settings.PanelDevices["fan"].Rotation);
        Assert.Empty(settings.Devices.TlLcdScreens);
    }

    [Fact]
    public void The_corsair_link_lcd_takes_only_the_software_half_turn_and_a_stored_flip_becomes_it()
    {
        var caps = Caps(PanelSurfaces.LcdRound);
        caps.Family = Nexus.Service.Peripherals.CorsairLink.CorsairLinkLcd.DeviceId;
        Assert.Equal(new[] { 0, 180 }, PanelRotation.SupportedFor(caps));
        Assert.False(PanelRotation.TryResolve(Record(caps), new PanelDevicePatch { Rotation = 90 }, out _));

        var settings = new NexusSettings();
        settings.Devices.Corsair.LcdRotation = 1;
        settings.PanelDevices["link"] = new PanelDeviceRecord { Id = "link", Capabilities = caps, Flip180 = true };

        PanelRotationMigration.Apply(settings);

        Assert.Equal(180, settings.PanelDevices["link"].Rotation);
        Assert.Equal(1, settings.Devices.Corsair.LcdRotation);
    }

    [Fact]
    public void A_record_that_already_has_a_rotation_keeps_it()
    {
        var settings = new NexusSettings();
        settings.PanelDevices["cooler"] = new PanelDeviceRecord { Id = "cooler", Capabilities = Caps(PanelSurfaces.LcdSquare), Rotation = 90, Flip180 = true };

        PanelRotationMigration.Apply(settings);

        Assert.Equal(90, settings.PanelDevices["cooler"].Rotation);
        Assert.Null(settings.PanelDevices["cooler"].Flip180);
    }

    [Fact]
    public void The_registry_reads_a_legacy_flag_that_was_never_migrated()
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.PanelDevices["cooler"] = new PanelDeviceRecord { Id = "cooler", Capabilities = Caps(PanelSurfaces.LcdSquare), Flip180 = true });

        Assert.Equal(180, new PanelDeviceRegistry(store).Get("cooler")!.Rotation);
    }

    [Fact]
    public void Loading_a_pre_rotation_settings_file_migrates_it_and_the_screen_looks_the_same()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 30,
          "panelDevices": {
            "strip": { "id": "strip", "flip180": true, "portrait": true,
              "capabilities": { "surface": "monitor", "supportsPortrait": true, "cssWidth": 480, "cssHeight": 1920 } },
            "cooler": { "id": "cooler", "flip180": true, "mirror": true,
              "capabilities": { "surface": "lcd-square", "cssWidth": 480, "cssHeight": 480 } }
          }
        }
        """);

        using var store = new JsonConfigStore(path);
        var loaded = store.Load();

        Assert.Equal(NexusSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal(270, loaded.PanelDevices["strip"].Rotation);
        Assert.Equal(180, loaded.PanelDevices["cooler"].Rotation);
        Assert.True(loaded.PanelDevices["cooler"].Mirror);

        var rewritten = File.ReadAllText(path);
        Assert.DoesNotContain("flip180", rewritten);
        Assert.DoesNotContain("\"portrait\"", rewritten);
    }

    [Fact]
    public void A_downgrade_that_writes_the_flags_back_is_folded_again_on_the_next_load()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, $$"""
        {
          "schemaVersion": {{NexusSettings.CurrentSchemaVersion}},
          "panelDevices": { "cooler": { "id": "cooler", "flip180": true,
            "capabilities": { "surface": "lcd-square", "cssWidth": 480, "cssHeight": 480 } } }
        }
        """);

        using var store = new JsonConfigStore(path);

        Assert.Equal(180, store.Load().PanelDevices["cooler"].Rotation);
    }

    [Fact]
    public void The_wire_record_names_rotation_and_what_is_supported_and_leaves_the_legacy_flags_out()
    {
        var registry = new PanelDeviceRegistry(new InMemoryConfigStore());
        var record = registry.Allocate("Cooler", Caps(PanelSurfaces.LcdSquare));
        registry.Patch(record.Id, new PanelDevicePatch { Rotation = 90 });

        var json = JsonSerializer.Serialize(registry.Get(record.Id)!, AppJsonContext.Default.PanelDeviceRecord);

        Assert.Contains("\"rotation\":90", json);
        Assert.Contains("\"supportedRotations\":[0,90,180,270]", json);
        Assert.DoesNotContain("flip180", json);
        Assert.DoesNotContain("\"portrait\"", json);
    }

    [Fact]
    public void A_default_response_clone_is_not_customized_but_a_chosen_rotation_is()
    {
        var registry = new PanelDeviceRegistry(new InMemoryConfigStore());
        var record = registry.Allocate("Cooler", Caps(PanelSurfaces.LcdSquare));

        Assert.False(PanelDeviceRegistry.IsCustomized(registry.Get(record.Id)!));
        Assert.False(registry.IsCustomized(record.Id));

        registry.Patch(record.Id, new PanelDevicePatch { Rotation = 90 });

        Assert.True(PanelDeviceRegistry.IsCustomized(registry.Get(record.Id)!));
        Assert.True(registry.IsCustomized(record.Id));
    }
}
