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
}
