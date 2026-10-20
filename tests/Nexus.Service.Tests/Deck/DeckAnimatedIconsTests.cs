using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Nexus.Service.Deck;
using Nexus.Service.Migration;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Rendering;
using Nexus.Service.Tests.Rendering;
using Xunit;

namespace Nexus.Service.Tests.Deck;

/// <summary>GIF / MP4 / WebM deck key icons: store sniffing and caps, preset package round trip, the Nexus 2 tile import and the physical key renderer.</summary>
public sealed class DeckAnimatedIconsTests : IDisposable
{
    private static readonly byte[] OnePxGif = Convert.FromBase64String("R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-deck-animated-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly DeckImageStore _images;

    public DeckAnimatedIconsTests() => _images = new DeckImageStore(Path.Combine(_dir, "images"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] Mp4(int length = 64)
    {
        var bytes = new byte[length];
        bytes[3] = 0x18;
        "ftypisom"u8.CopyTo(bytes.AsSpan(4));
        bytes[^1] = 7;
        return bytes;
    }

    private static byte[] WebM(int length = 64)
    {
        var bytes = new byte[length];
        new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }.CopyTo(bytes, 0);
        bytes[^1] = 9;
        return bytes;
    }

    private static byte[] Padded(byte[] head, int length)
    {
        var bytes = new byte[length];
        head.CopyTo(bytes, 0);
        return bytes;
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ".png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ".jpg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'7', (byte)'a' }, ".gif")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, ".gif")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p' }, ".mp4")]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, ".webm")]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, null)]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'0', (byte)'a' }, null)]
    public void SniffExtension_RecognizesEachType(byte[] head, string? expected) =>
        Assert.Equal(expected, DeckImageStore.SniffExtension(head));

    [Theory]
    [InlineData(".png", "image/png")]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".gif", "image/gif")]
    [InlineData(".mp4", "video/mp4")]
    [InlineData(".webm", "video/webm")]
    public void StoreThenTryLoad_ServesTheMatchingContentType(string ext, string contentType)
    {
        var bytes = ext switch
        {
            ".png" => Padded(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 24),
            ".jpg" => Padded(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, 24),
            ".gif" => OnePxGif,
            ".mp4" => Mp4(),
            _ => WebM(),
        };
        var id = _images.Store(bytes)!;
        Assert.True(File.Exists(Path.Combine(_images.RootDir, id + ext)));
        var loaded = _images.TryLoad(id);
        Assert.Equal(contentType, loaded!.Value.ContentType);
        Assert.Equal(bytes, loaded.Value.Bytes);
    }

    [Theory]
    [InlineData(".png", false)]
    [InlineData(".jpg", false)]
    [InlineData(".gif", true)]
    [InlineData(".mp4", true)]
    [InlineData(".webm", true)]
    public void Store_AppliesTheCapForTheSniffedType(string ext, bool animated)
    {
        byte[] Make(int length) => ext switch
        {
            ".png" => Padded(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, length),
            ".jpg" => Padded(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, length),
            ".gif" => Padded("GIF89a"u8.ToArray(), length),
            ".mp4" => Padded(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p' }, length),
            _ => Padded(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, length),
        };
        var cap = animated ? DeckImageStore.MaxAnimatedBytes : DeckImageStore.MaxBytes;
        Assert.NotNull(_images.Store(Make(cap)));
        Assert.Null(_images.Store(Make(cap + 1)));
    }

    [Fact]
    public void TryLoadStill_ReturnsAGifItselfAndNothingForAVideoWithoutAPoster()
    {
        var gifId = _images.Store(OnePxGif)!;
        var mp4Id = _images.Store(Mp4())!;
        Assert.Equal(OnePxGif, _images.TryLoadStill(gifId));
        Assert.Null(_images.TryLoadStill(mp4Id));
    }

    [Fact]
    public void PresetPackage_RoundTripsGifAndMp4Assets()
    {
        var gifId = _images.Store(OnePxGif)!;
        var mp4 = Mp4(2 * 1024 * 1024);
        var mp4Id = _images.Store(mp4)!;
        var preset = new DeckPreset
        {
            Id = "p-anim",
            Name = "Animated",
            Cols = 2,
            Rows = 1,
            Deck = new DeckConfig
            {
                Pages =
                {
                    new DeckPage
                    {
                        Slots =
                        {
                            new DeckSlot { Icon = new DeckIcon { Kind = "image", Value = gifId } },
                            new DeckSlot { Icon = new DeckIcon { Kind = "image", Value = mp4Id } },
                        },
                    },
                },
            },
        };

        var zip = DeckPresetPackage.Write(preset, _images);
        var result = DeckPresetPackage.Read(new ZipPackageSource(new MemoryStream(zip)));

        Assert.True(result.Ok, result.Error);
        Assert.Equal(".gif", result.Assets[gifId].Ext);
        Assert.Equal(OnePxGif, result.Assets[gifId].Bytes);
        Assert.Equal(".mp4", result.Assets[mp4Id].Ext);
        Assert.Equal(mp4, result.Assets[mp4Id].Bytes);
    }

    [Fact]
    public void PresetPackage_RejectsAnAssetWhoseContentDisagreesWithItsExtension()
    {
        var id = _images.Store(OnePxGif)!;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = new DeckPackageManifest
            {
                Format = 1,
                Id = "x",
                Name = "x",
                Cols = 1,
                Rows = 1,
                Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Icon = new DeckIcon { Kind = "image", Value = id } } } } } },
            };
            using (var s = zip.CreateEntry("preset.json").Open())
            {
                JsonSerializer.Serialize(s, manifest, Nexus.Service.Serialization.AppJsonContext.Default.DeckPackageManifest);
            }
            using var a = zip.CreateEntry($"assets/{id}.mp4").Open();
            a.Write(OnePxGif);
        }
        var result = DeckPresetPackage.Read(new ZipPackageSource(new MemoryStream(ms.ToArray())));
        Assert.False(result.Ok);
    }

    [Fact]
    public void Nexus2Tile_ImportsItsCustomImageIntoTheStore()
    {
        var gifPath = Path.Combine(_dir, "tile.gif");
        File.WriteAllBytes(gifPath, OnePxGif);
        var mp4Path = Path.Combine(_dir, "tile.mp4");
        File.WriteAllBytes(mp4Path, Mp4());
        var json = $$"""
            { "hideDock": false, "dock": [
              { "type": "macros", "position": 0, "macro": { "type": "website", "url": "https://example.com/" }, "front": { "type": "image", "path": {{JsonSerializer.Serialize(gifPath)}} } },
              { "type": "macros", "position": 1, "macro": { "type": "website", "url": "https://example.com/" }, "front": { "type": "image", "path": {{JsonSerializer.Serialize(mp4Path)}} } },
              { "type": "macros", "position": 2, "macro": { "type": "website", "url": "https://example.com/" }, "front": { "type": "image", "path": {{JsonSerializer.Serialize(Path.Combine(_dir, "missing.png"))}} } },
              { "type": "macros", "position": 3, "macro": { "type": "website", "url": "https://example.com/" }, "front": { "type": "default" } }
            ] }
            """;
        using var doc = JsonDocument.Parse(json);

        var dock = Nexus2Y70Translator.TranslateDock(doc.RootElement, File.Exists, p => Nexus2Y70Translator.ImportTileImage(_images, p));

        var slots = dock!.Config.Pages[0].Slots;
        Assert.Equal("image", slots[0].Icon!.Kind);
        Assert.Equal("image/gif", _images.TryLoad(slots[0].Icon!.Value)!.Value.ContentType);
        Assert.Equal("video/mp4", _images.TryLoad(slots[1].Icon!.Value)!.Value.ContentType);
        Assert.Null(slots[2].Icon);
        Assert.Null(slots[3].Icon);
    }

    [Fact]
    public void Nexus2Tile_OversizedImageIsSkipped()
    {
        var path = Path.Combine(_dir, "big.mp4");
        File.WriteAllBytes(path, Mp4(DeckImageStore.MaxAnimatedBytes + 1));
        Assert.Null(Nexus2Y70Translator.ImportTileImage(_images, path));
    }

    [Fact]
    public void Renderer_GifShowsItsFirstFrame()
    {
        var model = StreamDeckModels.ByProductId(0x0080)!;
        var id = _images.Store(OnePxGif)!;
        var renderer = new DeckKeyRenderer(_images, new NullShortcutsProvider(), new NullProcessIconProvider());
        var slot = new DeckSlot { Icon = new DeckIcon { Kind = "image", Value = id } };

        var bytes = renderer.Render(slot, false, model, 0);

        Assert.NotNull(bytes);
        using var image = TestImages.Decode(bytes!);
        Assert.Equal(model.KeyPixelSize, image.Width);
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".webm")]
    public void Renderer_VideoWithoutAPosterRendersWithoutThrowing(string ext)
    {
        var model = StreamDeckModels.ByProductId(0x0080)!;
        var id = _images.Store(ext == ".mp4" ? Mp4() : WebM())!;
        var renderer = new DeckKeyRenderer(_images, new NullShortcutsProvider(), new NullProcessIconProvider());
        var slot = new DeckSlot { Icon = new DeckIcon { Kind = "image", Value = id }, Label = "Clip" };

        var bytes = renderer.Render(slot, false, model, 0);
        using var glyph = renderer.RenderIconGlyph(slot.Icon, "app-window", 32);

        Assert.NotNull(bytes);
    }
}
