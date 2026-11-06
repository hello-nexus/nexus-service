using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Panel;

/// <summary>Quarter turns on non-square glass: which families take them, what size they render, and the per-orientation layout.</summary>
public class PanelPortraitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "panel-portrait-" + Guid.NewGuid().ToString("N")[..8]);

    public PanelPortraitTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static PanelDeviceCapabilities Caps(string surface, string? family, int width, int height, bool portraitMode = false) => new()
    {
        Surface = surface,
        Family = family,
        CssWidth = width,
        CssHeight = height,
        SupportsPortrait = portraitMode ? true : null,
    };

    private static PanelLayoutDto Layout(string widgetType, string id = "w") => new()
    {
        LayoutSchemaVersion = 2,
        Surface = PanelSurfaces.LcdWide,
        Pages = { new PanelPageDto { Id = "page-" + widgetType, Widgets = { new PanelWidgetDto { Id = id, Type = widgetType, Size = "2x2" } } } },
    };

    private static string Json(PanelLayoutDto? layout) =>
        JsonSerializer.Serialize(layout, PersistenceJsonContext.Default.PanelLayoutDto);

    // The families step 2 covers. Render size is what the page draws at a quarter turn; null where the glass takes none.
    [Theory]
    [InlineData("thermalright Vision Max", PanelSurfaces.LcdWide, "thermalright-lcd", 854, 480, "0,90,180,270", 480, 854)]
    [InlineData("thermalright Wonder Vision", PanelSurfaces.LcdWide, "thermalright-lcd", 1600, 720, "0,90,180,270", 720, 1600)]
    [InlineData("thermalright TL-M10", PanelSurfaces.LcdWide, "thermalright-lcd", 1920, 462, "0,90,180,270", 462, 1920)]
    [InlineData("thermalright Mjolnir/Frozen/Stream", PanelSurfaces.LcdSquare, "thermalright-lcd", 640, 480, "0,90,180,270", 480, 640)]
    [InlineData("thermalright square", PanelSurfaces.LcdSquare, "thermalright-lcd", 480, 480, "0,90,180,270", 480, 480)]
    [InlineData("asus ryujin", PanelSurfaces.LcdSquare, "asus-ryujin-lcd", 320, 240, "0,90,180,270", 240, 320)]
    [InlineData("rog glacial", PanelSurfaces.LcdSquare, "asus-glacial-lcd", 720, 1280, "0,90,180,270", 1280, 720)]
    [InlineData("aftershock glacier matrix", PanelSurfaces.LcdWide, "zmatrices-lcd", 1120, 540, "0,90,180,270", 540, 1120)]
    [InlineData("zmatrices max e87", PanelSurfaces.LcdWide, "zmatrices-max-e87", 1280, 440, "0,90,180,270", 440, 1280)]
    [InlineData("hydroshift2 round", PanelSurfaces.LcdRound, "lianli-hydroshift2-lcd", 480, 480, "0,90,180,270", 480, 480)]
    // Kept at the half turn.
    [InlineData("hydroshift2 curved", PanelSurfaces.LcdWide, HydroShift2CurveLcdDriver.Id, 1920, 462, "0,180", null, null)]
    [InlineData("apex oled", PanelSurfaces.LcdWide, ApexOledPanelDiscovery.Id, 128, 40, "0,180", null, null)]
    [InlineData("fan tl", PanelSurfaces.LcdRound, "lianli-tl-lcd", 400, 400, "0,180", null, null)]
    [InlineData("corsair link", PanelSurfaces.LcdRound, CorsairLinkLcd.DeviceId, 480, 480, "0,180", null, null)]
    // The strip renegotiates its own geometry.
    [InlineData("screen88", PanelSurfaces.Monitor, UniversalScreen88Driver.Id, 1920, 480, "0,90,180,270", 1920, 480)]
    public void Each_family_offers_the_turns_it_can_show_and_renders_the_swapped_size(
        string name, string surface, string family, int width, int height, string rotations, int? renderW, int? renderH)
    {
        var registry = new PanelDeviceRegistry(new InMemoryConfigStore());
        var record = registry.Allocate(name, Caps(surface, family, width, height, portraitMode: family == UniversalScreen88Driver.Id));
        var supported = rotations.Split(',').Select(int.Parse).ToArray();

        var clone = registry.Get(record.Id)!;
        Assert.Equal(supported, clone.SupportedRotations);

        if (supported.Contains(90))
        {
            var turned = registry.Patch(record.Id, new PanelDevicePatch { Rotation = 90 })!;
            Assert.Equal(90, turned.Rotation);
            // The capabilities keep the glass's own size for the strip and every other glass alike.
            Assert.Equal((width, height), (turned.Capabilities!.CssWidth, turned.Capabilities.CssHeight));
            Assert.Equal((renderW, renderH), (turned.RenderWidth, turned.RenderHeight));
        }
        else
        {
            Assert.Null(clone.RenderWidth);
            Assert.Null(clone.RenderHeight);
            Assert.False(PanelRotation.TryResolve(clone, new PanelDevicePatch { Rotation = 90 }, out _));
        }
    }

    [Fact]
    public void Every_jpeg_panel_model_is_square_so_the_quarter_turn_it_offers_keeps_its_size()
    {
        foreach (var model in new[]
        {
            JpegPanelModel.GalahadIiLcd, JpegPanelModel.HydroShiftLcd, JpegPanelModel.CorsairCapellix, JpegPanelModel.TlLcd,
        })
        {
            Assert.Equal(model.Width, model.Height);
        }
    }

    [Fact]
    public void Render_size_swaps_only_with_a_quarter_class_rotation()
    {
        var registry = new PanelDeviceRegistry(new InMemoryConfigStore());
        var record = registry.Allocate("Vision Max", Caps(PanelSurfaces.LcdWide, "thermalright-lcd", 854, 480));

        Assert.Equal((854, 480), (registry.Get(record.Id)!.RenderWidth, registry.Get(record.Id)!.RenderHeight));
        foreach (var (rotation, swapped) in new[] { (90, true), (180, false), (270, true), (0, false) })
        {
            var clone = registry.Patch(record.Id, new PanelDevicePatch { Rotation = rotation })!;
            Assert.Equal(swapped ? (480, 854) : (854, 480), (clone.RenderWidth, clone.RenderHeight));
        }
    }

    // ── layout per orientation ──

    private static (PanelDeviceRegistry Registry, InMemoryConfigStore Store, string Id) Turnable()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var record = registry.Allocate("Vision Max", Caps(PanelSurfaces.LcdWide, "thermalright-lcd", 854, 480));
        return (registry, store, record.Id);
    }

    [Fact]
    public void Turning_the_glass_never_rearranges_the_other_orientations_layout()
    {
        var (registry, store, id) = Turnable();
        var landscape = Layout("clock");
        var portrait = Layout("weather");

        registry.Patch(id, new PanelDevicePatch { Layout = landscape, WidgetSize = "small", GridSlots = 6 });
        var landscapeJson = Json(landscape);

        var turned = registry.Patch(id, new PanelDevicePatch { Rotation = 90 })!;
        // An empty slot reads as a layout nobody has saved: null, which the web fills with its default.
        Assert.Null(turned.Layout);
        Assert.Null(turned.WidgetSize);
        Assert.Null(turned.GridSlots);

        registry.Patch(id, new PanelDevicePatch { Layout = portrait, WidgetSize = "large", GridSlots = 4 });
        var stored = store.Load().PanelDevices[id];
        Assert.Equal(landscapeJson, Json(stored.Layout));
        Assert.Equal("small", stored.WidgetSize);
        Assert.Equal(6, stored.GridSlots);
        Assert.Equal(Json(portrait), Json(stored.PortraitLayout));
        Assert.Equal("large", stored.PortraitWidgetSize);
        Assert.Equal(4, stored.PortraitGridSlots);

        // 270 is the same class as 90.
        Assert.Equal(Json(portrait), Json(registry.Patch(id, new PanelDevicePatch { Rotation = 270 })!.Layout));
        // And back: 180 is the landscape class.
        var back = registry.Patch(id, new PanelDevicePatch { Rotation = 180 })!;
        Assert.Equal(landscapeJson, Json(back.Layout));
        Assert.Equal("small", back.WidgetSize);
        Assert.Equal(6, back.GridSlots);
        Assert.Null(back.PortraitLayout);
    }

    [Fact]
    public void A_patch_that_turns_the_glass_and_saves_a_layout_saves_it_for_the_new_orientation()
    {
        var (registry, store, id) = Turnable();
        registry.Patch(id, new PanelDevicePatch { Layout = Layout("clock") });

        registry.Patch(id, new PanelDevicePatch { Rotation = 90, Layout = Layout("weather") });

        var stored = store.Load().PanelDevices[id];
        Assert.Equal("clock", stored.Layout!.Pages.Single().Widgets.Single().Type);
        Assert.Equal("weather", stored.PortraitLayout!.Pages.Single().Widgets.Single().Type);
    }

    [Fact]
    public void Square_glass_keeps_one_layout()
    {
        const string surface = PanelSurfaces.LcdSquare;
        const int width = 480;
        const int height = 480;
        const bool portraitMode = false;
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var record = registry.Allocate("Glass", Caps(surface, null, width, height, portraitMode));
        registry.Patch(record.Id, new PanelDevicePatch { Layout = Layout("clock"), WidgetSize = "small" });

        var turned = registry.Patch(record.Id, new PanelDevicePatch { Rotation = 90 })!;

        Assert.Equal("clock", turned.Layout!.Pages.Single().Widgets.Single().Type);
        Assert.Equal("small", turned.WidgetSize);
        registry.Patch(record.Id, new PanelDevicePatch { Layout = Layout("weather") });
        Assert.Null(store.Load().PanelDevices[record.Id].PortraitLayout);
        Assert.Equal("weather", store.Load().PanelDevices[record.Id].Layout!.Pages.Single().Widgets.Single().Type);
    }

    [Fact]
    public void The_other_slot_never_reaches_the_wire()
    {
        var (registry, _, id) = Turnable();
        registry.Patch(id, new PanelDevicePatch { Layout = Layout("clock") });
        registry.Patch(id, new PanelDevicePatch { Rotation = 90 });
        registry.Patch(id, new PanelDevicePatch { Layout = Layout("weather") });

        foreach (var clone in new[] { registry.Get(id)!, registry.List().Single(), registry.Patch(id, new PanelDevicePatch { Mirror = true })! })
        {
            Assert.Null(clone.PortraitLayout);
            Assert.Null(clone.PortraitWidgetSize);
            Assert.Null(clone.PortraitGridSlots);
        }
    }

    [Fact]
    public void A_record_that_never_turned_stores_exactly_what_it_stored_before()
    {
        var (registry, store, id) = Turnable();
        registry.Patch(id, new PanelDevicePatch { Layout = Layout("clock"), WidgetSize = "small", GridSlots = 6, Rotation = 180 });

        var text = JsonSerializer.Serialize(store.Load().PanelDevices[id], PersistenceJsonContext.Default.PanelDeviceRecord);

        Assert.DoesNotContain("portrait", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("renderWidth", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Both_layouts_survive_a_restart()
    {
        var path = Path.Combine(_dir, "settings.json");
        string id;
        using (var store = new JsonConfigStore(path))
        {
            var registry = new PanelDeviceRegistry(store);
            id = registry.Allocate("Vision Max", Caps(PanelSurfaces.LcdWide, "thermalright-lcd", 854, 480)).Id;
            registry.Patch(id, new PanelDevicePatch { Layout = Layout("clock"), WidgetSize = "small" });
            registry.Patch(id, new PanelDevicePatch { Rotation = 90, Layout = Layout("weather"), WidgetSize = "large", GridSlots = 4 });
            store.FlushNow();
        }

        using var reloaded = new JsonConfigStore(path);
        var again = new PanelDeviceRegistry(reloaded);

        var turned = again.Get(id)!;
        Assert.Equal(90, turned.Rotation);
        Assert.Equal("weather", turned.Layout!.Pages.Single().Widgets.Single().Type);
        Assert.Equal("large", turned.WidgetSize);
        Assert.Equal(4, turned.GridSlots);
        var flat = again.Patch(id, new PanelDevicePatch { Rotation = 0 })!;
        Assert.Equal("clock", flat.Layout!.Pages.Single().Widgets.Single().Type);
        Assert.Equal("small", flat.WidgetSize);
    }

    [Fact]
    public void A_personalization_reset_clears_both_slots()
    {
        var (registry, store, id) = Turnable();
        registry.Patch(id, new PanelDevicePatch { Layout = Layout("clock") });
        registry.Patch(id, new PanelDevicePatch { Rotation = 90, Layout = Layout("weather"), WidgetSize = "large", GridSlots = 4 });

        registry.ResetToDefaults(id);

        var stored = store.Load().PanelDevices[id];
        Assert.Null(stored.PortraitLayout);
        Assert.Null(stored.PortraitWidgetSize);
        Assert.Null(stored.PortraitGridSlots);
        Assert.Equal(90, stored.Rotation);
    }

    [Fact]
    public void Removing_an_app_widget_reaches_the_slot_that_is_not_showing()
    {
        var (registry, store, id) = Turnable();
        registry.Patch(id, new PanelDevicePatch { Layout = Layout("app:thing", "a") });
        registry.Patch(id, new PanelDevicePatch { Rotation = 90, Layout = Layout("app:thing", "b") });
        registry.Patch(id, new PanelDevicePatch { Rotation = 0 });

        var changed = registry.RemoveWidgetType("app:thing");

        Assert.Equal(new[] { id }, changed);
        var stored = store.Load().PanelDevices[id];
        Assert.Empty(stored.Layout!.Pages.Single().Widgets);
        Assert.Empty(stored.PortraitLayout!.Pages.Single().Widgets);
    }

    // ── presets and profiles carry both slots ──

    [Fact]
    public void A_preset_saves_and_restores_both_layouts()
    {
        var (registry, store, id) = Turnable();
        registry.Patch(id, new PanelDevicePatch { Layout = Layout("clock"), WidgetSize = "small" });
        registry.Patch(id, new PanelDevicePatch { Rotation = 90, Layout = Layout("weather"), WidgetSize = "large", GridSlots = 4 });
        var first = registry.CreatePreset(id, "A", out _)!.ActiveId!;
        var second = registry.CreatePreset(id, "B", out _)!.ActiveId!;
        Assert.NotEqual(first, second);

        // B takes other layouts in both orientations; leaving A took A's two layouts with it.
        registry.Patch(id, new PanelDevicePatch { Layout = Layout("media") });
        registry.Patch(id, new PanelDevicePatch { Rotation = 0, Layout = Layout("calendar") });
        var changed = store.Load().PanelDevices[id];
        Assert.Equal("calendar", changed.Layout!.Pages.Single().Widgets.Single().Type);
        Assert.Equal("media", changed.PortraitLayout!.Pages.Single().Widgets.Single().Type);

        registry.ActivatePreset(id, first);

        var restored = store.Load().PanelDevices[id];
        Assert.Equal("clock", restored.Layout!.Pages.Single().Widgets.Single().Type);
        Assert.Equal("weather", restored.PortraitLayout!.Pages.Single().Widgets.Single().Type);
        Assert.Equal("large", restored.PortraitWidgetSize);
        Assert.Equal(4, restored.PortraitGridSlots);
    }

    [Fact]
    public void A_profile_carries_both_layouts_to_the_matching_panel()
    {
        var live = new NexusSettings();
        live.PanelDevices["p"] = new PanelDeviceRecord
        {
            Id = "p",
            Capabilities = Caps(PanelSurfaces.LcdWide, "thermalright-lcd", 854, 480),
            Layout = Layout("clock"),
            PortraitLayout = Layout("weather"),
            PortraitWidgetSize = "large",
            PortraitGridSlots = 4,
            Rotation = 90,
        };
        var file = new NexusSettings();
        PanelProfileData.WriteTo(file, live);

        var other = new NexusSettings();
        other.PanelDevices["p"] = new PanelDeviceRecord { Id = "p", Capabilities = Caps(PanelSurfaces.LcdWide, "thermalright-lcd", 854, 480) };
        PanelProfileData.ApplyToLive(other, file);

        var applied = other.PanelDevices["p"];
        Assert.Equal("clock", applied.Layout!.Pages.Single().Widgets.Single().Type);
        Assert.Equal("weather", applied.PortraitLayout!.Pages.Single().Widgets.Single().Type);
        Assert.Equal("large", applied.PortraitWidgetSize);
        Assert.Equal(4, applied.PortraitGridSlots);
        // Mount settings stay with the live panel.
        Assert.Null(applied.Rotation);
        Assert.False(PanelProfileData.IsDefaultLook(applied));
    }

    // ── a user who never turns the glass past a half turn sees nothing change ──

    [Theory]
    [InlineData(0)]
    [InlineData(180)]
    public void Below_a_quarter_turn_the_geometry_the_layout_and_the_session_are_what_they_were(int rotation)
    {
        var landscape = Layout("clock");
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var families = new (string Surface, string Family, int W, int H)[]
        {
            (PanelSurfaces.LcdWide, "thermalright-lcd", 854, 480),
            (PanelSurfaces.LcdWide, "thermalright-lcd", 1600, 720),
            (PanelSurfaces.LcdWide, "thermalright-lcd", 1920, 462),
            (PanelSurfaces.LcdSquare, "thermalright-lcd", 640, 480),
            (PanelSurfaces.LcdSquare, "asus-ryujin-lcd", 320, 240),
            (PanelSurfaces.LcdSquare, "asus-glacial-lcd", 720, 1280),
            (PanelSurfaces.LcdWide, "zmatrices-lcd", 1120, 540),
            (PanelSurfaces.LcdWide, "zmatrices-max-e87", 1280, 440),
            (PanelSurfaces.LcdWide, HydroShift2CurveLcdDriver.Id, 1920, 462),
        };
        foreach (var f in families)
        {
            var record = registry.Allocate(f.Family + f.W, Caps(f.Surface, f.Family, f.W, f.H));
            registry.Patch(record.Id, new PanelDevicePatch { Layout = landscape, WidgetSize = "small", GridSlots = 6 });
            if (rotation != 0)
            {
                registry.Patch(record.Id, new PanelDevicePatch { Rotation = rotation });
            }

            var clone = registry.Get(record.Id)!;
            Assert.Equal((f.W, f.H), (clone.Capabilities!.CssWidth, clone.Capabilities.CssHeight));
            // Glass that takes no quarter turn reports no render size; the rest render the glass's own size.
            var takesQuarter = f.Family != HydroShift2CurveLcdDriver.Id;
            Assert.Equal(takesQuarter ? ((int?)f.W, (int?)f.H) : (null, null), (clone.RenderWidth, clone.RenderHeight));
            Assert.Equal(Json(landscape), Json(clone.Layout));
            Assert.Equal("small", clone.WidgetSize);
            Assert.Equal(6, clone.GridSlots);
            Assert.False(PanelLayoutSlots.UsesPortraitSlot(clone));
            Assert.Null(store.Load().PanelDevices[record.Id].PortraitLayout);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(180)]
    public void The_strip_below_a_quarter_turn_keeps_its_geometry_and_layout(int rotation)
    {
        var landscape = Layout("clock");
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var record = registry.Allocate("Strip", Caps(PanelSurfaces.Monitor, UniversalScreen88Driver.Id, 1920, 480, portraitMode: true));
        registry.Patch(record.Id, new PanelDevicePatch { Layout = landscape, WidgetSize = "small", GridSlots = 6, Rotation = rotation });

        var clone = registry.Get(record.Id)!;

        Assert.Equal((1920, 480), (clone.Capabilities!.CssWidth, clone.Capabilities.CssHeight));
        Assert.Equal((1920, 480), (clone.RenderWidth, clone.RenderHeight));
        Assert.Equal(Json(landscape), Json(clone.Layout));
        Assert.Equal(("small", 6), (clone.WidgetSize, clone.GridSlots));
        Assert.Equal(Json(landscape), Json(store.Load().PanelDevices[record.Id].Layout));
        Assert.Null(store.Load().PanelDevices[record.Id].PortraitLayout);
    }
}
