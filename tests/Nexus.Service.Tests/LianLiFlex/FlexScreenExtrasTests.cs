using System;
using System.IO;
using Nexus.Service.Lighting.Capture;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Rendering;
using Nexus.Service.Routes;
using SkiaSharp;
using Xunit;

namespace Nexus.Service.Tests.LianLiFlex;

public class FlexScreenExtrasTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flex-extras-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class FakeSource : IScreenFrameSource
    {
        public int W = 64;
        public int H = 36;
        public string? Monitor;
        public bool Stopped;
        public void Start(string monitorId, int width, int height) => Monitor = monitorId;
        public void Stop() => Stopped = true;
        public byte[]? TryAcquireFrame(out int width, out int height)
        {
            width = W;
            height = H;
            var data = new byte[W * H * 3];
            for (var i = 0; i < data.Length; i += 3)
            {
                data[i] = 200;
            }
            return data;
        }
    }

    private byte[] SpritePng()
    {
        using var bmp = RenderKit.NewImage(40, 30, SKColors.Red);
        return RenderKit.EncodePng(bmp);
    }

    [Fact]
    public void Sprites_import_list_load_and_delete()
    {
        var lib = new FlexSpriteLibrary(Path.Combine(_dir, "s"));
        var (sprite, error) = lib.Import(SpritePng(), "dot.png");
        Assert.Null(error);
        Assert.Equal(40, sprite!.Width);
        Assert.Single(lib.List());
        using (var loaded = lib.Load(sprite.Id))
        {
            Assert.NotNull(loaded);
        }
        Assert.True(lib.Delete(sprite.Id));
        Assert.Empty(lib.List());
        Assert.Null(lib.Load("../x"));
        Assert.NotNull(lib.Import(new byte[] { 1, 2, 3 }, "x.png").Error);
    }

    [Fact]
    public void An_oversized_sprite_is_scaled_to_fit_the_display()
    {
        var lib = new FlexSpriteLibrary(Path.Combine(_dir, "s"));
        using var big = RenderKit.NewImage(800, 400, SKColors.Blue);
        var (sprite, _) = lib.Import(RenderKit.EncodePng(big), "big.png");
        Assert.Equal(400, sprite!.Width);
        Assert.Equal(200, sprite.Height);
    }

    [Fact]
    public void Content_validation_covers_the_extra_types()
    {
        var lib = new FlexSpriteLibrary(Path.Combine(_dir, "s"));
        var id = lib.Import(SpritePng(), "dot.png").Sprite!.Id;
        var ok = new Slv3LcdContentRequest { Serial = "S", ContentType = "animation2d", Overlay = new LianLiFlexOverlaySettings { SpriteMediaId = id } };
        Assert.Null(LianLiFlexRoutes.ValidateExtraContent(ok, lib));
        Assert.NotNull(LianLiFlexRoutes.ValidateExtraContent(new Slv3LcdContentRequest { Serial = "S", ContentType = "animation2d" }, lib));
        Assert.NotNull(LianLiFlexRoutes.ValidateExtraContent(new Slv3LcdContentRequest { Serial = "S", ContentType = "animation2d", Overlay = new LianLiFlexOverlaySettings { SpriteMediaId = "nope" } }, lib));
        Assert.NotNull(LianLiFlexRoutes.ValidateExtraContent(new Slv3LcdContentRequest { Serial = "S", ContentType = "animation2d", Overlay = new LianLiFlexOverlaySettings { SpriteMediaId = id, Type = "zigzag" } }, lib));
        Assert.NotNull(LianLiFlexRoutes.ValidateExtraContent(new Slv3LcdContentRequest { Serial = "S", ContentType = "animation2d", Overlay = new LianLiFlexOverlaySettings { SpriteMediaId = id, Speed = 11 } }, lib));
        Assert.Null(LianLiFlexRoutes.ValidateExtraContent(new Slv3LcdContentRequest { Serial = "S", ContentType = "screen" }, lib));
        Assert.NotNull(LianLiFlexRoutes.ValidateExtraContent(new Slv3LcdContentRequest { Serial = "S", ContentType = "screen", ScreenSource = new LianLiFlexScreenSource { CropSize = 2 } }, lib));
        Assert.False(LianLiFlexRoutes.IsExtraContent("clock"));
    }

    [Fact]
    public void The_crop_square_is_centered_by_default_and_clamped_when_set()
    {
        Assert.Equal((14, 0, 36), FlexScreenCapture.SquareOf(null, 64, 36));
        var (x, y, edge) = FlexScreenCapture.SquareOf(new LianLiFlexScreenSource { CropX = 0.99, CropY = 0.99, CropSize = 0.5 }, 64, 36);
        Assert.Equal(18, edge);
        Assert.Equal(64 - 18, x);
        Assert.Equal(36 - 18, y);
    }

    [Fact]
    public void Screen_content_renders_a_square_jpeg_and_releases_the_capture()
    {
        var source = new FakeSource();
        var capture = new FlexScreenCapture(() => source, () => "primary");
        var extra = new FlexExtraContent(new Slv3LcdMediaLibrary(Path.Combine(_dir, "m")), new FlexSpriteLibrary(Path.Combine(_dir, "s")), capture);
        var settings = new LianLiWirelessScreenSettings { ContentType = "screen" };
        var (jpeg, delay) = extra.Render("S", settings);
        Assert.NotNull(jpeg);
        Assert.Equal("primary", source.Monitor);
        Assert.Equal(FlexScreenCapture.FrameIntervalMs, delay);
        using var decoded = RenderKit.Decode(jpeg!);
        Assert.Equal(400, decoded!.Width);
        Assert.Equal(400, decoded.Height);
        extra.ReleaseExtra("S");
        Assert.True(source.Stopped);
    }

    [Fact]
    public void Animation_content_renders_frames_and_waits_without_a_sprite()
    {
        var sprites = new FlexSpriteLibrary(Path.Combine(_dir, "s"));
        var id = sprites.Import(SpritePng(), "dot.png").Sprite!.Id;
        var now = new DateTime(2026, 1, 1);
        var extra = new FlexExtraContent(new Slv3LcdMediaLibrary(Path.Combine(_dir, "m")), sprites, new FlexScreenCapture(() => null, () => ""), () => now);
        var settings = new LianLiWirelessScreenSettings { ContentType = "animation2d", Overlay = new LianLiFlexOverlaySettings { SpriteMediaId = id } };
        var (jpeg, delay) = extra.Render("S", settings);
        Assert.NotNull(jpeg);
        Assert.True(delay > 0);
        settings.Overlay = new LianLiFlexOverlaySettings { SpriteMediaId = "gone" };
        Assert.Null(extra.Render("S", settings).Jpeg);
        extra.Dispose();
    }

    [Fact]
    public void The_modular_sources_table_marks_availability()
    {
        var list = LianLiFlexRoutes.ModularSources(k => k == "cpuTemp");
        Assert.Contains(list, s => s.Key == "cpuTemp" && s.Available);
        Assert.Contains(list, s => s.Key == "gpuTemp" && !s.Available);
    }
}
