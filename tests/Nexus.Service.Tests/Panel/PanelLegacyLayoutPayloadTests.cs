using System.Linq;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Panel;

/// <summary>A profile or preset an older build saved holds one layout; applying it must not touch the slot it knows nothing about.</summary>
public class PanelLegacyLayoutPayloadTests
{
    private static PanelDeviceCapabilities Strip() => new()
    {
        Surface = PanelSurfaces.Monitor, Family = "lianli-screen88", CssWidth = 1920, CssHeight = 480, SupportsPortrait = true,
    };

    private static PanelDeviceCapabilities Vision() => new()
    {
        Surface = PanelSurfaces.LcdWide, Family = "thermalright-lcd", CssWidth = 854, CssHeight = 480,
    };

    private static PanelLayoutDto Layout(string type) => new()
    {
        LayoutSchemaVersion = 2,
        Surface = PanelSurfaces.Monitor,
        Pages = { new PanelPageDto { Id = "p", Widgets = { new PanelWidgetDto { Id = "w", Type = type, Size = "2x2" } } } },
    };

    private static string Type(PanelLayoutDto? layout) => layout!.Pages.Single().Widgets.Single().Type;

    private static PanelDeviceRecord Live(PanelDeviceCapabilities caps, int rotation, bool strip) => new()
    {
        Id = "p",
        Capabilities = caps,
        Rotation = rotation == 0 ? null : rotation,
        Layout = Layout("live-landscape"),
        WidgetSize = "small",
        PortraitLayout = Layout("live-portrait"),
        PortraitWidgetSize = "large",
        LayoutSlots = strip ? true : null,
    };

    private static void Apply(PanelDeviceRecord live, PanelDeviceRecord saved)
    {
        var current = new NexusSettings();
        current.PanelDevices["p"] = live;
        var file = new NexusSettings { HasPanelsData = true };
        file.PanelDevices["p"] = saved;
        PanelProfileData.ApplyToLive(current, file);
    }

    [Theory]
    [InlineData(0, "old", "live-portrait")]
    [InlineData(180, "old", "live-portrait")]
    [InlineData(90, "live-landscape", "old")]
    [InlineData(270, "live-landscape", "old")]
    public void An_old_profile_on_the_strip_fills_the_slot_the_record_shows(int rotation, string landscape, string portrait)
    {
        var live = Live(Strip(), rotation, strip: true);

        Apply(live, new PanelDeviceRecord { Id = "p", Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.Monitor }, Layout = Layout("old"), WidgetSize = "mid" });

        Assert.Equal(landscape, Type(live.Layout));
        Assert.Equal(portrait, Type(live.PortraitLayout));
        Assert.Equal(rotation is 90 or 270 ? ("small", "mid") : ("mid", "large"), (live.WidgetSize, live.PortraitWidgetSize));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void An_old_profile_on_non_square_glass_fills_the_landscape_slot_and_leaves_the_other(int rotation)
    {
        var live = Live(Vision(), rotation, strip: false);

        Apply(live, new PanelDeviceRecord { Id = "p", Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.LcdWide }, Layout = Layout("old") });

        Assert.Equal("old", Type(live.Layout));
        Assert.Equal("live-portrait", Type(live.PortraitLayout));
        Assert.Equal("large", live.PortraitWidgetSize);
    }

    [Fact]
    public void A_profile_that_carries_both_slots_keeps_applying_both()
    {
        var live = Live(Vision(), 90, strip: false);
        var saved = new PanelDeviceRecord
        {
            Id = "p", Capabilities = new PanelDeviceCapabilities { Surface = PanelSurfaces.LcdWide },
            Layout = Layout("new-landscape"), PortraitLayout = Layout("new-portrait"),
        };

        Apply(live, saved);

        Assert.Equal("new-landscape", Type(live.Layout));
        Assert.Equal("new-portrait", Type(live.PortraitLayout));
        Assert.Null(live.PortraitWidgetSize);
    }

    [Fact]
    public void A_strip_profile_written_by_this_build_applies_both_slots_even_when_one_is_empty()
    {
        var source = new NexusSettings();
        var written = Live(Strip(), 0, strip: true);
        written.PortraitLayout = null;
        written.PortraitWidgetSize = null;
        source.PanelDevices["p"] = written;
        var file = new NexusSettings();
        PanelProfileData.WriteTo(file, source);
        Assert.True(file.PanelDevices["p"].LayoutSlots);

        var live = Live(Strip(), 90, strip: true);
        Apply(live, file.PanelDevices["p"]);

        Assert.Equal("live-landscape", Type(live.Layout));
        Assert.Null(live.PortraitLayout);
    }

    [Fact]
    public void An_old_preset_on_the_strip_fills_the_slot_the_record_shows_and_a_new_one_applies_both()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var record = registry.Allocate("Strip", Strip());
        registry.Patch(record.Id, new PanelDevicePatch { Layout = Layout("landscape") });
        registry.Patch(record.Id, new PanelDevicePatch { Rotation = 90, Layout = Layout("portrait") });
        var stored = store.Load().PanelDevices[record.Id];
        stored.Presets = new() { new PanelPreset { Id = "old", Name = "Old", Layout = Layout("from-old-build") } };

        registry.ActivatePreset(record.Id, "old");

        Assert.Equal("landscape", Type(stored.Layout));
        Assert.Equal("from-old-build", Type(stored.PortraitLayout));

        // A preset this build captured carries the marker and both slots.
        var made = registry.CreatePreset(record.Id, "New", out _)!.ActiveId!;
        registry.ActivatePreset(record.Id, "old");
        registry.ActivatePreset(record.Id, made);
        Assert.Equal("landscape", Type(stored.Layout));
        Assert.Equal("from-old-build", Type(stored.PortraitLayout));
    }

    [Fact]
    public void An_old_preset_on_non_square_glass_leaves_the_portrait_slot()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var record = registry.Allocate("Vision", Vision());
        registry.Patch(record.Id, new PanelDevicePatch { Layout = Layout("landscape") });
        registry.Patch(record.Id, new PanelDevicePatch { Rotation = 90, Layout = Layout("portrait") });
        var stored = store.Load().PanelDevices[record.Id];
        stored.Presets = new() { new PanelPreset { Id = "old", Name = "Old", Layout = Layout("from-old-build") } };

        registry.ActivatePreset(record.Id, "old");

        Assert.Equal("from-old-build", Type(stored.Layout));
        Assert.Equal("portrait", Type(stored.PortraitLayout));
    }

    [Fact]
    public void The_marker_does_not_make_a_default_strip_look_customized()
    {
        var registry = new PanelDeviceRegistry(new InMemoryConfigStore());
        var record = registry.Allocate("Strip", Strip());

        Assert.False(registry.IsCustomized(record.Id));
        Assert.True(PanelProfileData.IsDefaultLook(new PanelDeviceRecord { Id = "x", Capabilities = Strip(), LayoutSlots = true }));
    }
}
