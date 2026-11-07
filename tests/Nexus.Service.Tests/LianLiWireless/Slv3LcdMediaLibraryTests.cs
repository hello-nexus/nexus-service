using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Platform;

namespace Nexus.Service.Tests.LianLiWireless;

public class Slv3LcdMediaLibraryTests
{
    [Fact]
    public async Task ImportAsync_of_a_still_image_produces_one_400x400_frame()
    {
        if (FfmpegResolver.Path is null) return;

        using var fixture = new LibraryFixture();
        var pngPath = await fixture.GenerateStillAsync();

        var result = await fixture.Library.ImportAsync(pngPath, "photo.png");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("image", result.Item!.Kind);
        Assert.Equal(1, result.Item.Frames);
        Assert.Empty(result.Item.Delays);

        var frames = fixture.Library.LoadFrames(result.Item.Id);
        Assert.NotNull(frames);
        Assert.Single(frames.Frames);
        Assert.True(frames.Frames[0].JpegBytes.Length <= Slv3LcdImage.MaxJpegBytes);
    }

    [Fact]
    public async Task ImportAsync_of_a_gif_produces_multiple_frames_with_parsed_delays()
    {
        if (FfmpegResolver.Path is null) return;

        using var fixture = new LibraryFixture();
        var gifPath = await fixture.GenerateGifAsync();

        var result = await fixture.Library.ImportAsync(gifPath, "anim.gif");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("gif", result.Item!.Kind);
        Assert.True(result.Item.Frames > 1, $"expected >1 frame, got {result.Item.Frames}");

        var frames = fixture.Library.LoadFrames(result.Item.Id);
        Assert.NotNull(frames);
        Assert.Equal(result.Item.Frames, frames.Frames.Length);
        // One delay per frame, the GIF's own 10 fps.
        Assert.Equal(result.Item.Frames, result.Item.Delays.Length);
        Assert.All(result.Item.Delays, d => Assert.Equal(100, d));
    }

    [Fact]
    public async Task ImportAsync_of_a_video_produces_frames_with_uniform_delay()
    {
        if (FfmpegResolver.Path is null) return;

        using var fixture = new LibraryFixture();
        var mp4Path = await fixture.GenerateVideoAsync();

        var result = await fixture.Library.ImportAsync(mp4Path, "clip.mp4");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("video", result.Item!.Kind);
        Assert.True(result.Item.Frames > 1, $"expected >1 frame, got {result.Item.Frames}");
        Assert.Equal(result.Item.Frames, result.Item.Delays.Length);
        // The source's own 10 fps, not a fixed import rate.
        Assert.All(result.Item.Delays, d => Assert.Equal(100, d));
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(120, 60)]
    public async Task ImportAsync_keeps_a_videos_frame_rate_up_to_60(int sourceFps, int keptFps)
    {
        if (FfmpegResolver.Path is null) return;

        using var fixture = new LibraryFixture();
        var mp4Path = await fixture.GenerateVideoAsync(sourceFps);

        var result = await fixture.Library.ImportAsync(mp4Path, "clip.mp4");

        Assert.True(result.Ok, result.Error);
        Assert.Equal(keptFps, result.Item!.Frames);
        Assert.Equal(1000, result.Item.Delays.Sum());
    }

    [Theory]
    [InlineData("  Stream #0:0(und): Video: h264 (High) (avc1 / 0x31637661), yuv420p, 1920x1080, 4987 kb/s, 29.97 fps, 29.97 tbr, 30k tbn", 30)]
    [InlineData("  Stream #0:0: Video: vp9 (Profile 0), yuv420p(tv), 400x400, SAR 1:1 DAR 1:1, 60 fps, 60 tbr, 1k tbn (default)", 60)]
    [InlineData("  Stream #0:1: Audio: aac (LC), 48000 Hz, stereo, fltp, 128 kb/s", null)]
    public void SourceFps_reads_the_video_streams_rate(string line, int? fps)
    {
        Assert.Equal(fps, Slv3LcdMediaLibrary.SourceFps("Input #0, mov,mp4\n" + line + "\n"));
    }

    [Fact]
    public void UniformDelays_keep_the_clip_length_at_60_fps()
    {
        var delays = Slv3LcdMediaLibrary.UniformDelays(60, 60);
        Assert.Equal(1000, delays.Sum());
        Assert.All(delays, d => Assert.InRange(d, 16, 17));
    }

    [Fact]
    public async Task ImportAsync_rejects_an_unsupported_extension()
    {
        if (FfmpegResolver.Path is null) return;

        using var fixture = new LibraryFixture();
        var path = Path.Combine(Path.GetTempPath(), $"slv3-lcd-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "not media");
        try
        {
            var result = await fixture.Library.ImportAsync(path, "notes.txt");
            Assert.False(result.Ok);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task ListItems_and_DeleteItem_round_trip()
    {
        if (FfmpegResolver.Path is null) return;

        using var fixture = new LibraryFixture();
        var pngPath = await fixture.GenerateStillAsync();
        var result = await fixture.Library.ImportAsync(pngPath, "photo.png");
        Assert.True(result.Ok, result.Error);

        var items = fixture.Library.ListItems();
        Assert.Contains(items, i => i.Id == result.Item!.Id);

        Assert.True(fixture.Library.DeleteItem(result.Item!.Id));
        Assert.DoesNotContain(fixture.Library.ListItems(), i => i.Id == result.Item.Id);
        Assert.Null(fixture.Library.LoadFrames(result.Item.Id));
    }

    [Fact]
    public void LoadFrames_returns_null_for_an_unknown_id()
    {
        using var fixture = new LibraryFixture();
        Assert.Null(fixture.Library.LoadFrames("nonexistent"));
    }

    private sealed class LibraryFixture : IDisposable
    {
        private readonly string _root;
        public Slv3LcdMediaLibrary Library { get; }

        public LibraryFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), $"slv3-lcd-lib-{Guid.NewGuid():N}");
            Library = new Slv3LcdMediaLibrary(_root);
        }

        public async Task<string> GenerateStillAsync()
        {
            var path = Path.Combine(_root, "source.png");
            await RunFfmpegAsync("-y", "-f", "lavfi", "-i", "color=red:size=800x600", "-frames:v", "1", path);
            return path;
        }

        public async Task<string> GenerateGifAsync()
        {
            var path = Path.Combine(_root, "source.gif");
            await RunFfmpegAsync(
                "-y", "-f", "lavfi", "-i", "testsrc=size=200x200:rate=10:duration=1",
                path);
            return path;
        }

        public async Task<string> GenerateVideoAsync(int fps = 10)
        {
            var path = Path.Combine(_root, "source.mp4");
            await RunFfmpegAsync(
                "-y", "-f", "lavfi", "-i", $"testsrc=size=320x240:rate={fps}:duration=1",
                "-pix_fmt", "yuv420p", path);
            return path;
        }

        private static async Task RunFfmpegAsync(params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegResolver.Path!,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg failed to start");
            await proc.WaitForExitAsync();
            Assert.Equal(0, proc.ExitCode);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
