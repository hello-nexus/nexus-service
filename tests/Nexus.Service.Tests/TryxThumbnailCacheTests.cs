using System;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxThumbnailCacheTests
{
    [Fact]
    public void Label_round_trips_and_is_removed_with_the_media()
    {
        var name = $"label-{Guid.NewGuid():N}.mp4.h264_2240x1080";
        try
        {
            TryxThumbnailCache.WriteLabel(name, " sunset b150 ");
            Assert.Equal("sunset b150", TryxThumbnailCache.ReadLabel(name));

            TryxThumbnailCache.Delete(name);
            Assert.Null(TryxThumbnailCache.ReadLabel(name));
        }
        finally
        {
            TryxThumbnailCache.Delete(name);
        }
    }

    [Fact]
    public void Label_is_not_written_for_a_blank_name_or_an_unsafe_device_file()
    {
        var name = $"label-{Guid.NewGuid():N}.mp4.h264_2240x1080";
        TryxThumbnailCache.WriteLabel(name, "  ");
        Assert.Null(TryxThumbnailCache.ReadLabel(name));

        TryxThumbnailCache.WriteLabel("../escape", "x");
        Assert.Null(TryxThumbnailCache.ReadLabel("../escape"));
    }

    [Fact]
    public void RunCapture_kills_a_hung_process_at_the_timeout()
    {
        if (OperatingSystem.IsWindows()) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var ok = TryxThumbnailCache.RunCapture("/bin/sleep", ["30"], 300, out _, out _);

        Assert.False(ok);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void RunCapture_returns_the_output_of_a_process_that_exits()
    {
        if (OperatingSystem.IsWindows()) return;

        var ok = TryxThumbnailCache.RunCapture("/bin/echo", ["a b"], 5000, out var stdout, out _);

        Assert.True(ok);
        Assert.Equal("a b", stdout.Trim());
    }
}
