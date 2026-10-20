using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Platform;
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

    private static readonly byte[] Mp4Head = [0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m'];

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ".png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ".jpg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'7', (byte)'a' }, ".gif")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, ".gif")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' }, ".mp4")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2' }, ".mp4")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c' }, null)]
    [InlineData(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f' }, null)]
    [InlineData(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'M', (byte)'4', (byte)'A', (byte)' ' }, null)]
    [InlineData(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'X', (byte)'A', (byte)'V', (byte)'C' }, ".mp4")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'M', (byte)'S', (byte)'N', (byte)'V' }, ".mp4")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'7' }, ".mp4")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'8' }, ".mp4")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'9' }, ".mp4")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'7', (byte)'1' }, ".mp4")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'f', (byte)'4', (byte)'v', (byte)' ' }, ".mp4")]
    [InlineData(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p' }, null)]
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
            ".mp4" => Padded(Mp4Head, length),
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
    public void Nexus2Tile_RecordsLocalImagePathsAndImportsThemOnDemand()
    {
        var gifPath = Path.Combine(_dir, "tile.gif");
        File.WriteAllBytes(gifPath, OnePxGif);
        var mp4Path = Path.Combine(_dir, "tile.mp4");
        File.WriteAllBytes(mp4Path, Mp4());
        string Tile(int position, string front) =>
            $$"""{ "type": "macros", "position": {{position}}, "macro": { "type": "website", "url": "https://example.com/" }, "front": {{front}} }""";
        string Image(string path) => $$"""{ "type": "image", "path": {{JsonSerializer.Serialize(path)}} }""";
        var json = "{ \"hideDock\": false, \"dock\": [" + string.Join(",", [
            Tile(0, Image(gifPath)),
            Tile(1, Image(mp4Path)),
            Tile(2, Image(Path.Combine(_dir, "missing.png"))),
            Tile(3, Image("\\\\server\\share\\a.png")),
            Tile(4, """{ "type": "default" }"""),
        ]) + "] }";
        using var doc = JsonDocument.Parse(json);

        var dock = Nexus2Y70Translator.TranslateDock(doc.RootElement, File.Exists)!;

        // Translation alone copies nothing: the store is still empty and the UNC path is never recorded.
        Assert.Empty(Directory.GetFiles(_images.RootDir));
        Assert.Equal([gifPath, mp4Path, Path.Combine(_dir, "missing.png")], dock.TileImages.ConvertAll(t => t.Path));

        var ids = dock.TileImages.ConvertAll(t => Nexus2Y70Translator.ImportTileImage(_images, t.Path));
        Assert.Equal("image/gif", _images.TryLoad(ids[0]!)!.Value.ContentType);
        Assert.Equal("video/mp4", _images.TryLoad(ids[1]!)!.Value.ContentType);
        Assert.Null(ids[2]);
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
        using var glyph = renderer.RenderIconGlyph(slot.Icon, "Sliders", 32);

        Assert.NotNull(bytes);
        // No poster means no image face: the glyph falls back to the lucide icon rather than failing.
        Assert.NotNull(glyph);
    }

    [Fact]
    public void Sweep_RemovesOnlyStaleTempFiles()
    {
        var root = Path.Combine(_dir, "sweep");
        Directory.CreateDirectory(root);
        var stale = Path.Combine(root, "a.tmp.png");
        var staleWrite = Path.Combine(root, "b.tmp");
        var fresh = Path.Combine(root, "c.tmp");
        foreach (var f in new[] { stale, staleWrite, fresh })
        {
            File.WriteAllBytes(f, [1]);
        }
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-3));
        File.SetLastWriteTimeUtc(staleWrite, DateTime.UtcNow.AddHours(-3));

        _ = new DeckImageStore(root);

        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(staleWrite));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task EnsurePoster_NeverThrowsForABrokenOrUnknownVideo()
    {
        var id = _images.Store(Mp4())!;
        await _images.EnsurePosterAsync(id);
        await _images.EnsurePosterAsync(new string('0', 64));
        await _images.EnsurePosterAsync("not-an-id");
        Assert.Null(_images.TryLoadStill(id));
    }

    [Fact]
    public async Task EnsurePoster_HonorsTheCallersCancellation()
    {
        var id = _images.Store(Mp4())!;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        if (FfmpegResolver.Path is null)
        {
            await _images.EnsurePosterAsync(id, cts.Token);
            return;
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _images.EnsurePosterAsync(id, cts.Token));
    }

    private static async Task<byte[]?> MakeRealMp4(string path, string size = "64x48")
    {
        await MediaImporter.RunFfmpeg("-y", "-f", "lavfi", "-i", $"color=c=red:s={size}:d=0.5:r=10", "-c:v", "libx264", "-pix_fmt", "yuv420p", path);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    [FfmpegFact]
    public async Task EnsurePoster_ExtractsAFirstFrameFromARealMp4()
    {
        var path = Path.Combine(_dir, "real.mp4");
        var mp4 = await MakeRealMp4(path);
        var id = _images.Store(mp4!)!;

        await _images.EnsurePosterAsync(id);

        var still = _images.TryLoadStill(id);
        Assert.NotNull(still);
        using var bitmap = SkiaSharp.SKBitmap.Decode(still!);
        Assert.Equal((64, 48), (bitmap.Width, bitmap.Height));
        Assert.Empty(Directory.GetFiles(_images.RootDir, "*.tmp*"));
    }

    [FfmpegFact]
    public async Task EnsurePoster_ConcurrentCallsShareOneExtraction()
    {
        var path = Path.Combine(_dir, "real2.mp4");
        var mp4 = await MakeRealMp4(path);
        var id = _images.Store(mp4!)!;

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => _images.EnsurePosterAsync(id)));

        Assert.NotNull(_images.TryLoadStill(id));
    }

    [FfmpegFact]
    public async Task PresetImport_ProducesAPosterForAVideoAsset()
    {
        var path = Path.Combine(_dir, "real3.mp4");
        var mp4 = await MakeRealMp4(path);
        var id = _images.Store(mp4!)!;
        var preset = new DeckPreset
        {
            Id = "p-v",
            Name = "V",
            Cols = 1,
            Rows = 1,
            Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Icon = new DeckIcon { Kind = "image", Value = id } } } } } },
        };
        var read = DeckPresetPackage.Read(new ZipPackageSource(new MemoryStream(DeckPresetPackage.Write(preset, _images))));
        var target = new DeckImageStore(Path.Combine(_dir, "import-target"));
        foreach (var (assetId, asset) in read.Assets)
        {
            target.StoreValidated(assetId, asset.Ext, asset.Bytes);
        }

        await target.EnsurePosterAsync(id);

        Assert.NotNull(target.TryLoadStill(id));
    }

    [FfmpegFact]
    public async Task EnsurePoster_BoundsALargeSourceToTheMaxEdge()
    {
        var mp4 = await MakeRealMp4(Path.Combine(_dir, "wide.mp4"), "640x360");
        var id = _images.Store(mp4!)!;

        await _images.EnsurePosterAsync(id);

        using var bitmap = SkiaSharp.SKBitmap.Decode(_images.TryLoadStill(id)!);
        Assert.Equal(256, Math.Max(bitmap.Width, bitmap.Height));
        Assert.Equal((256, 144), (bitmap.Width, bitmap.Height));
    }

    private (DeckImageStore Store, string Id, Func<int> Calls) FakeRunnerStore(Func<string[], Task<string>> run)
    {
        var calls = 0;
        var store = new DeckImageStore(Path.Combine(_dir, "fake-" + Guid.NewGuid().ToString("N")[..6]), (_, _, args) =>
        {
            calls++;
            return run(args);
        });
        return (store, store.Store(Mp4())!, () => calls);
    }

    [Fact]
    public async Task EnsurePoster_FfmpegRejectingTheFileIsRememberedAndNotRetried()
    {
        var (store, id, calls) = FakeRunnerStore(_ => throw new FfmpegExitException("ffmpeg exit 1: bad"));

        await store.EnsurePosterAsync(id);
        await store.EnsurePosterAsync(id);

        Assert.Equal(1, calls());
        Assert.False(store.IsPosterPending(id));
        Assert.Empty(Directory.GetFiles(store.RootDir, "*.poster.none"));
    }

    [Fact]
    public async Task EnsurePoster_CleanExitWithNoOutputIsRemembered()
    {
        var (store, id, _) = FakeRunnerStore(_ => Task.FromResult(""));

        await store.EnsurePosterAsync(id);

        Assert.False(store.IsPosterPending(id));
    }

    [Fact]
    public async Task EnsurePoster_TimeoutAndSpawnFailureLeaveNoMarkerSoARetryRuns()
    {
        var spawn = false;
        var (store, id, calls) = FakeRunnerStore(_ => spawn
            ? throw new System.ComponentModel.Win32Exception("access denied")
            : throw new InvalidOperationException("ffmpeg timed out after 15s"));

        await store.EnsurePosterAsync(id);
        spawn = true;
        await store.EnsurePosterAsync(id);

        Assert.Equal(2, calls());
        Assert.True(store.IsPosterPending(id));
    }

    [Fact]
    public async Task EnsurePoster_SuccessfulRunPublishesThePoster()
    {
        var (store, id, _) = FakeRunnerStore(args =>
        {
            File.WriteAllBytes(args[^1], OnePxPng);
            return Task.FromResult("");
        });

        await store.EnsurePosterAsync(id);

        Assert.Equal(OnePxPng, store.TryLoadStill(id));
        Assert.False(store.IsPosterPending(id));
    }

    private static byte[] GreenPng()
    {
        using var bmp = new SkiaSharp.SKBitmap(8, 8);
        bmp.Erase(SkiaSharp.SKColors.Lime);
        using var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public async Task Renderer_TriggersExtractionAndDoesNotCacheTheFallbackWhilePending()
    {
        var model = StreamDeckModels.ByProductId(0x0080)!;
        var release = new TaskCompletionSource();
        var ready = new TaskCompletionSource<string>();
        var (store, id, calls) = FakeRunnerStore(async args =>
        {
            await release.Task;
            File.WriteAllBytes(args[^1], GreenPng());
            return "";
        });
        var renderer = new DeckKeyRenderer(store, new NullShortcutsProvider(), new NullProcessIconProvider());
        renderer.PosterReady += readyId => ready.TrySetResult(readyId);
        var slot = new DeckSlot { Icon = new DeckIcon { Kind = "image", Value = id } };

        var before = renderer.Render(slot, false, model, 0);
        var duringExtraction = renderer.Render(slot, false, model, 0);
        release.SetResult();
        Assert.Equal(id, await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        var after = renderer.Render(slot, false, model, 0);

        Assert.Equal(1, calls());
        Assert.Equal(before, duringExtraction);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task EnsurePoster_ARestartRetriesWhatFfmpegRejectedBefore()
    {
        var root = Path.Combine(_dir, "restart");
        var first = new DeckImageStore(root, (_, _, _) => throw new FfmpegExitException("ffmpeg exit 1: bad"));
        var id = first.Store(Mp4())!;
        await first.EnsurePosterAsync(id);
        Assert.False(first.IsPosterPending(id));

        var second = new DeckImageStore(root, (_, _, args) =>
        {
            File.WriteAllBytes(args[^1], GreenPng());
            return Task.FromResult("");
        });
        Assert.True(second.IsPosterPending(id));
        await second.EnsurePosterAsync(id);

        Assert.NotNull(second.TryLoadStill(id));
    }

    [Fact]
    public void FfmpegPathCache_DoesNotReprobeAMissWithinTheWindow()
    {
        var probes = 0;
        long now = 0;
        var cache = new FfmpegPathCache(() => { probes++; return (null, false); }, () => now);

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(cache.Path);
        }
        Assert.Equal(1, probes);

        now += 6 * 60 * 1000;
        Assert.Null(cache.Path);
        Assert.Equal(2, probes);
    }

    [Fact]
    public void FfmpegPathCache_ProbesAgainWhenACandidateExistsButItsProbeFailed()
    {
        var probes = 0;
        var cache = new FfmpegPathCache(() => { probes++; return (null, true); }, () => 0);

        Assert.Null(cache.Path);
        Assert.Null(cache.Path);

        Assert.Equal(2, probes);
        Assert.True(cache.CandidateExists);
    }

    [Fact]
    public void FfmpegPathCache_KeepsAFoundPath()
    {
        var probes = 0;
        var cache = new FfmpegPathCache(() => { probes++; return ("/x/ffmpeg", true); }, () => 0);

        Assert.Equal("/x/ffmpeg", cache.Path);
        Assert.Equal("/x/ffmpeg", cache.Path);
        Assert.Equal(1, probes);
    }

    private static readonly byte[] OnePxPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
}
