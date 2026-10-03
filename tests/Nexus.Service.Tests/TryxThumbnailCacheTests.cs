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
}
