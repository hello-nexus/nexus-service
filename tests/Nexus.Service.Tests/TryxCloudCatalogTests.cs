using Nexus.Service.Peripherals.Tryx.Panorama;
using Xunit;

namespace Nexus.Service.Tests;

public class TryxCloudCatalogTests
{
    [Theory]
    [InlineData("https://cdn.example/a/b.mp4?sig=1", "download_86.mp4.h264_2240x1080")]
    [InlineData("https://cdn.example/a/b.AVI", "download_86.mp4.h264_2240x1080")]
    [InlineData("https://cdn.example/a/b.gif?x=.mp4", "download_86.gif.h264_2240x1080")]
    [InlineData("https://cdn.example/a/b.jpeg", "download_86.jpg.h264_2240x1080")]
    [InlineData("https://cdn.example/a/b", "download_86.mp4.h264_2240x1080")]
    public void DownloadFileName_follows_kanalis_type_from_the_url(string url, string expected)
    {
        Assert.Equal(expected, TryxCloudCatalog.DownloadFileName(86, url));
    }
}
