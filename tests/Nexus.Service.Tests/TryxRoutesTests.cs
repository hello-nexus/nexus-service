using System;
using System.Text.Json;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxRoutesTests
{
    private static readonly TryxOverlayConfig CurrentOverlay = new()
    {
        Filter = "blur",
        Opacity = 80,
    };

    [Fact]
    public void BuildOverlayConfigFromRequest_coalesces_a_json_null_label_and_device_to_empty_string()
    {
        // System.Text.Json overrides a non-nullable string property's "" initializer
        // with an explicit JSON null, so this must be exercised through the real
        // source-gen deserializer (constructing TryxOverlayItem in C# can't repro it).
        const string json = """
        {
          "items": [ { "sensorId": "s1", "device": null, "label": null, "x": 0.1, "y": 0.2 } ],
          "font": "roboto-regular",
          "size": 100,
          "color": "#ffffff"
        }
        """;
        var body = JsonSerializer.Deserialize(json, AppJsonContext.Default.TryxOverlayRequest)!;

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Single(cfg.Items);
        Assert.Equal("s1", cfg.Items[0].SensorId);
        Assert.Equal("", cfg.Items[0].Device);
        Assert.Equal("", cfg.Items[0].Label);
    }

    [Fact]
    public void BuildOverlayConfigFromRequest_maps_items_in_order()
    {
        var body = new TryxOverlayRequest
        {
            Items =
            [
                new TryxOverlayItem { SensorId = "/amdcpu/0/temperature/0", Device = "cpu", Label = "CPU Temp", X = 0.03, Y = 0.10 },
                new TryxOverlayItem { SensorId = "/gpu-nvidia/0/temperature/0", Device = "gpu", Label = "GPU Temp", X = 0.50, Y = 0.60 },
            ],
            Font = "roboto-bold",
            Size = 120,
            Color = "#112233",
            Align = "center",
            Docked = true,
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal(2, cfg.Items.Count);
        Assert.Equal("/amdcpu/0/temperature/0", cfg.Items[0].SensorId);
        Assert.Equal("cpu", cfg.Items[0].Device);
        Assert.Equal("CPU Temp", cfg.Items[0].Label);
        Assert.Equal(0.03, cfg.Items[0].X);
        Assert.Equal(0.10, cfg.Items[0].Y);
        Assert.Equal("/gpu-nvidia/0/temperature/0", cfg.Items[1].SensorId);
        Assert.Equal("roboto-bold", cfg.Font);
        Assert.Equal(120, cfg.Size);
        Assert.Equal("#112233", cfg.Color);
        Assert.Equal("center", cfg.Align);
        Assert.True(cfg.Docked);
    }

    [Fact]
    public void BuildOverlayConfigFromRequest_truncates_more_than_four_items()
    {
        var body = new TryxOverlayRequest
        {
            Items =
            [
                new TryxOverlayItem { SensorId = "s1", Device = "cpu", Label = "a", X = 0, Y = 0 },
                new TryxOverlayItem { SensorId = "s2", Device = "cpu", Label = "b", X = 0, Y = 0.1 },
                new TryxOverlayItem { SensorId = "s3", Device = "cpu", Label = "c", X = 0, Y = 0.2 },
                new TryxOverlayItem { SensorId = "s4", Device = "cpu", Label = "d", X = 0, Y = 0.3 },
                new TryxOverlayItem { SensorId = "s5", Device = "cpu", Label = "e", X = 0, Y = 0.4 },
            ],
            Font = "roboto-regular",
            Size = 100,
            Color = "#ffffff",
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal(4, cfg.Items.Count);
        Assert.Equal(["s1", "s2", "s3", "s4"], cfg.Items.ConvertAll(i => i.SensorId));
    }

    [Fact]
    public void BuildOverlayConfigFromRequest_skips_blank_sensor_ids()
    {
        var body = new TryxOverlayRequest
        {
            Items =
            [
                new TryxOverlayItem { SensorId = "", Device = "cpu", Label = "a", X = 0, Y = 0 },
                new TryxOverlayItem { SensorId = "   ", Device = "cpu", Label = "b", X = 0, Y = 0 },
                new TryxOverlayItem { SensorId = "s3", Device = "cpu", Label = "c", X = 0.1, Y = 0.2 },
            ],
            Font = "roboto-regular",
            Size = 100,
            Color = "#ffffff",
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal(["s3"], cfg.Items.ConvertAll(i => i.SensorId));
        Assert.Equal(0.1, cfg.Items[0].X);
        Assert.Equal(0.2, cfg.Items[0].Y);
    }

    [Theory]
    [InlineData(-0.5, 0.0)]
    [InlineData(1.5, 1.0)]
    public void BuildOverlayConfigFromRequest_clamps_x_and_y_to_0_1(double input, double clamped)
    {
        var body = new TryxOverlayRequest
        {
            Items = [new TryxOverlayItem { SensorId = "s1", Device = "cpu", Label = "a", X = input, Y = input }],
            Font = "roboto-regular",
            Size = 100,
            Color = "#ffffff",
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal(clamped, cfg.Items[0].X);
        Assert.Equal(clamped, cfg.Items[0].Y);
    }

    [Theory]
    [InlineData("roboto-bold", "roboto-bold")]
    [InlineData("monospace", "monospace")]
    [InlineData("Comic Sans MS", "roboto-regular")]
    [InlineData("", "roboto-regular")]
    public void BuildOverlayConfigFromRequest_falls_back_to_roboto_regular_for_an_invalid_font(
        string requestedFont, string expectedFont)
    {
        var body = new TryxOverlayRequest
        {
            Items = [new TryxOverlayItem { SensorId = "s1", Device = "cpu", Label = "a", X = 0, Y = 0 }],
            Font = requestedFont,
            Size = 100,
            Color = "#ffffff",
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal(expectedFont, cfg.Font);
    }

    [Theory]
    [InlineData(10, 50)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(150, 150)]
    [InlineData(200, 150)]
    public void BuildOverlayConfigFromRequest_clamps_size_to_50_150(int input, int clamped)
    {
        var body = new TryxOverlayRequest
        {
            Items = [new TryxOverlayItem { SensorId = "s1", Device = "cpu", Label = "a", X = 0, Y = 0 }],
            Font = "roboto-regular",
            Size = input,
            Color = "#ffffff",
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal(clamped, cfg.Size);
    }

    [Fact]
    public void BuildOverlayConfigFromRequest_defaults_a_blank_color_to_white()
    {
        var body = new TryxOverlayRequest
        {
            Items = [new TryxOverlayItem { SensorId = "s1", Device = "cpu", Label = "a", X = 0, Y = 0 }],
            Font = "roboto-regular",
            Size = 100,
            Color = "  ",
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal("#ffffff", cfg.Color);
    }

    [Theory]
    [InlineData("left", "left")]
    [InlineData("center", "center")]
    [InlineData("right", "right")]
    [InlineData("bogus", "left")]
    [InlineData("", "left")]
    public void BuildOverlayConfigFromRequest_falls_back_to_left_for_an_invalid_align(string requestedAlign, string expectedAlign)
    {
        var body = new TryxOverlayRequest
        {
            Items = [new TryxOverlayItem { SensorId = "s1", Device = "cpu", Label = "a", X = 0, Y = 0 }],
            Font = "roboto-regular",
            Size = 100,
            Color = "#ffffff",
            Align = requestedAlign,
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal(expectedAlign, cfg.Align);
    }

    [Fact]
    public void BuildOverlayConfigFromRequest_preserves_filter_and_opacity_from_the_current_overlay()
    {
        var body = new TryxOverlayRequest
        {
            Items = [new TryxOverlayItem { SensorId = "s1", Device = "cpu", Label = "a", X = 0, Y = 0 }],
            Font = "roboto-regular",
            Size = 100,
            Color = "#ffffff",
        };

        var cfg = TryxRoutes.BuildOverlayConfigFromRequest(body, CurrentOverlay);

        Assert.Equal("blur", cfg.Filter);
        Assert.Equal(80, cfg.Opacity);
    }

    // ── /tryx/status overlay.items round-trip ──

    [Fact]
    public void BuildOverlayItems_projects_each_item_verbatim()
    {
        var overlay = new TryxOverlayConfig
        {
            Items =
            [
                new TryxOverlaySensorItem { SensorId = "s1", Device = "cpu", Label = "CPU Temp", X = 0.03, Y = 0.10 },
                new TryxOverlaySensorItem { SensorId = "s2", Device = "gpu", Label = "GPU Temp", X = 0.50, Y = 0.60 },
            ],
        };

        var items = TryxRoutes.BuildOverlayItems(overlay);

        Assert.Equal(2, items.Length);
        Assert.Equal("s1", items[0].SensorId);
        Assert.Equal("cpu", items[0].Device);
        Assert.Equal("CPU Temp", items[0].Label);
        Assert.Equal(0.03, items[0].X);
        Assert.Equal(0.10, items[0].Y);
        Assert.Equal("s2", items[1].SensorId);
        Assert.Equal(0.50, items[1].X);
        Assert.Equal(0.60, items[1].Y);
    }

    [Fact]
    public void BuildSlideshowSnapshot_projects_every_field()
    {
        var snapshot = TryxRoutes.BuildSlideshowSnapshot(new TryxSlideshowConfig
        {
            Enabled = true, IntervalSec = 300, Shuffle = true, FinishVideos = false,
        });

        Assert.True(snapshot.Enabled);
        Assert.Equal(300, snapshot.IntervalSec);
        Assert.True(snapshot.Shuffle);
        Assert.False(snapshot.FinishVideos);
    }

    [Fact]
    public void BuildOverlayItems_returns_empty_for_no_configured_items()
    {
        var overlay = new TryxOverlayConfig { Items = [] };

        var items = TryxRoutes.BuildOverlayItems(overlay);

        Assert.Empty(items);
    }

    [Fact]
    public void ResolveAvailablePresets_returns_only_the_ids_the_panel_reported()
    {
        var presets = TryxRoutes.ResolveAvailablePresets(new[] { "default_01", "default_02" });

        Assert.Equal(2, presets.Count);
        Assert.Equal("default_01", presets[0].Id);
        Assert.Equal("default_02", presets[1].Id);
        Assert.All(presets, p => Assert.False(string.IsNullOrEmpty(p.Name)));
    }

    [Fact]
    public void ResolveAvailablePresets_falls_back_to_the_first_six_when_the_panel_has_not_reported_yet()
    {
        var presets = TryxRoutes.ResolveAvailablePresets(Array.Empty<string>());

        Assert.Equal(6, presets.Count);
        Assert.Equal(
            new[] { "default_01", "default_02", "default_03", "default_04", "default_05", "default_06" },
            presets.ConvertAll(p => p.Id));
    }

    [Fact]
    public void ResolveAvailablePresets_ignores_ids_that_are_not_in_the_known_catalog()
    {
        var presets = TryxRoutes.ResolveAvailablePresets(new[] { "default_01", "start", "screensaver" });

        Assert.Single(presets);
        Assert.Equal("default_01", presets[0].Id);
    }

    [Fact]
    public void ResolveAvailablePresets_lists_an_uncataloged_default_wallpaper_under_its_raw_id()
    {
        var presets = TryxRoutes.ResolveAvailablePresets(new[] { "default_01", "default_07" });

        Assert.Equal(2, presets.Count);
        Assert.Equal("default_01", presets[0].Id);
        Assert.NotEqual("default_01", presets[0].Name);
        Assert.Equal("default_07", presets[1].Id);
        Assert.Equal("default_07", presets[1].Name);
    }
}
