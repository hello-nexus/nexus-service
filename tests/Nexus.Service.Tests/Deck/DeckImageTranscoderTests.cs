using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Deck;
using Nexus.Service.Media;
using SkiaSharp;
using Xunit;

namespace Nexus.Service.Tests.Deck;

/// <summary>Crop and shrink of animated deck key icons through the real ffmpeg.</summary>
public sealed class DeckImageTranscoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-deck-transcode-" + Guid.NewGuid().ToString("N")[..8]);

    public DeckImageTranscoderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Counts GIF frames by their Graphic Control Extension blocks; this Skia build has no codec frame-count entry point.</summary>
    internal static int GifFrames(byte[] gif)
    {
        var count = 0;
        for (var i = 0; i + 2 < gif.Length; i++)
        {
            if (gif[i] == 0x21 && gif[i + 1] == 0xF9 && gif[i + 2] == 0x04)
            {
                count++;
            }
        }
        return count;
    }

    private async Task<string> Clip(string name, string size, string source = "testsrc")
    {
        var path = Path.Combine(_dir, name);
        await MediaImporter.RunFfmpeg("-y", "-f", "lavfi", "-i", $"{source}=s={size}:d=1:r=10", path);
        return path;
    }

    [FfmpegFact]
    public async Task Gif_CropIsAppliedSquaredAndStaysAnimated()
    {
        var src = await Clip("a.gif", "640x360");

        var (bytes, error) = await DeckImageTranscoder.RunAsync(src, DeckImageTranscoder.Kind.Gif, "0,0,0.5625,1", CancellationToken.None);

        Assert.Null(error);
        using var codec = SKCodec.Create(SKData.CreateCopy(bytes!));
        Assert.Equal((256, 256), (codec.Info.Width, codec.Info.Height));
        Assert.True(GifFrames(bytes!) > 1);
    }

    [FfmpegFact]
    public async Task Gif_IsNeverUpscaled()
    {
        var src = await Clip("small.gif", "64x48");

        var (bytes, _) = await DeckImageTranscoder.RunAsync(src, DeckImageTranscoder.Kind.Gif, null, CancellationToken.None);

        using var codec = SKCodec.Create(SKData.CreateCopy(bytes!));
        Assert.Equal((64, 48), (codec.Info.Width, codec.Info.Height));
    }

    [FfmpegFact]
    public async Task Video_BecomesAnMp4ThatDecodesAtTheCroppedSquare()
    {
        var src = await Clip("v.mp4", "640x360");

        var (bytes, error) = await DeckImageTranscoder.RunAsync(src, DeckImageTranscoder.Kind.Video, "0,0,0.5625,1", CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(".mp4", DeckImageStore.SniffExtension(bytes!));
        var store = new DeckImageStore(Path.Combine(_dir, "store"));
        var id = store.Store(bytes!)!;
        await store.EnsurePosterAsync(id);
        using var poster = SKBitmap.Decode(store.TryLoadStill(id)!);
        Assert.Equal((256, 256), (poster.Width, poster.Height));
    }

    [FfmpegFact]
    public async Task Still_BecomesACroppedPng()
    {
        var src = Path.Combine(_dir, "s.png");
        await MediaImporter.RunFfmpeg("-y", "-f", "lavfi", "-i", "testsrc=s=640x360:d=1:r=1", "-frames:v", "1", src);

        var (bytes, _) = await DeckImageTranscoder.RunAsync(src, DeckImageTranscoder.Kind.Still, "0,0,0.5625,1", CancellationToken.None);

        using var bitmap = SKBitmap.Decode(bytes!);
        Assert.Equal((256, 256), (bitmap.Width, bitmap.Height));
    }

    [FfmpegFact]
    public async Task OutputOverTheCapIsRejected()
    {
        var src = Path.Combine(_dir, "noise.gif");
        await MediaImporter.RunFfmpeg("-y", "-f", "lavfi", "-i", "nullsrc=s=256x256:d=1:r=20,geq=random(1)*255:128:128", src);

        var (bytes, error) = await DeckImageTranscoder.RunAsync(src, DeckImageTranscoder.Kind.Gif, null, CancellationToken.None, maxOutputBytes: 50 * 1024);

        Assert.Null(bytes);
        Assert.Equal("Result too large", error);
    }

    [Fact]
    public async Task UnreadableInputReturnsAnErrorInsteadOfThrowing()
    {
        var src = Path.Combine(_dir, "junk.gif");
        await File.WriteAllBytesAsync(src, [1, 2, 3]);

        var (bytes, error) = await DeckImageTranscoder.RunAsync(src, DeckImageTranscoder.Kind.Gif, null, CancellationToken.None);

        Assert.Null(bytes);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(".GIF", DeckImageTranscoder.Kind.Gif)]
    [InlineData(".webm", DeckImageTranscoder.Kind.Video)]
    [InlineData(".png", DeckImageTranscoder.Kind.Still)]
    public void KindForExtension_MapsStagedExtensions(string ext, DeckImageTranscoder.Kind kind) =>
        Assert.Equal(kind, DeckImageTranscoder.KindForExtension(ext));

    [Fact]
    public void KindForExtension_RejectsUnknown() => Assert.Null(DeckImageTranscoder.KindForExtension(".txt"));
}
