using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxKanaliEncodeTests
{
    [Theory]
    // Two Kanali uploads read back off the panel: 30 fps 2240x1080 clips at 2629 / 269 kb/s
    // were encoded at bitrate=6047 / 619 (x264 SEI).
    [InlineData(2240, 1080, 30, 2629, 6047)]
    [InlineData(2240, 1080, 30, 269, 619)]
    [InlineData(2240, 1080, 30, 0, 4000)]
    [InlineData(2240, 1080, 30, 9000, 12000)]
    [InlineData(2240, 1080, 60, 100, 500)]
    // 4K downscale clamps the pixel ratio at 0.35; a 120 fps source clamps the fps ratio at 0.75.
    [InlineData(3840, 2160, 60, 10000, 4025)]
    [InlineData(2240, 1080, 120, 4000, 3450)]
    public void BitrateKbps_follows_mediax(int w, int h, double fps, int kbps, int expected)
    {
        var src = new TryxKanaliEncode.SourceInfo(w, h, fps, kbps);

        Assert.Equal(expected, TryxKanaliEncode.BitrateKbps(src, 2240, 1080, 60));
    }

    [Fact]
    public void ParseSourceInfo_reads_the_video_stream()
    {
        const string stderr =
            "Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'a.mp4':\n" +
            "  Duration: 00:00:02.00, start: 0.000000, bitrate: 2633 kb/s\n" +
            "  Stream #0:0[0x1](und): Video: h264 (Main) (avc1 / 0x31637661), yuv420p(progressive), 2240x1080, 2628 kb/s, 30 fps, 30 tbr, 15360 tbn (default)\n";

        Assert.Equal(new TryxKanaliEncode.SourceInfo(2240, 1080, 30, 2628), TryxKanaliEncode.ParseSourceInfo(stderr));
    }

    [Fact]
    public void ParseSourceInfo_falls_back_to_the_container_bitrate()
    {
        const string stderr =
            "  Duration: 00:00:05.00, start: 0.000000, bitrate: 812 kb/s\n" +
            "  Stream #0:0: Video: vp9 (Profile 0), yuv420p(tv), 1280x720, SAR 1:1 DAR 16:9, 29.97 fps, 29.97 tbr, 1k tbn\n";

        Assert.Equal(new TryxKanaliEncode.SourceInfo(1280, 720, 29.97, 812), TryxKanaliEncode.ParseSourceInfo(stderr));
    }

    [Fact]
    public void ParseSourceInfo_marks_a_gif_source_as_an_image()
    {
        const string stderr =
            "  Duration: 00:00:03.00, start: 0.000000, bitrate: 5120 kb/s\n" +
            "  Stream #0:0: Video: gif, bgra, 498x280, 15 fps, 15 tbr, 100 tbn\n";

        Assert.True(TryxKanaliEncode.ParseSourceInfo(stderr).IsImage);
    }

    [Fact]
    public void X264Options_uses_crf_18_for_an_image_source_and_abr_otherwise()
    {
        var gif = new TryxKanaliEncode.SourceInfo(498, 280, 15, 5120, IsImage: true);
        var video = new TryxKanaliEncode.SourceInfo(2240, 1080, 30, 2629);

        Assert.Contains("-crf 18", TryxKanaliEncode.X264Options(gif, 2240, 1080, 60));
        Assert.DoesNotContain("-b:v", TryxKanaliEncode.X264Options(gif, 2240, 1080, 60));
        Assert.Contains("-b:v 6047k", TryxKanaliEncode.X264Options(video, 2240, 1080, 60));
    }
}
