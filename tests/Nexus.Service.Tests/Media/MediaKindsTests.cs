using Nexus.Service.Media;
using Xunit;

namespace Nexus.Service.Tests.Media;

public sealed class MediaKindsTests
{
    [Theory]
    [InlineData("clip.mp4", "video")]
    [InlineData("clip.WEBM", "video")]
    [InlineData("clip.mov", "video")]
    [InlineData("loop.gif", "gif")]
    [InlineData("still.png", "image")]
    [InlineData("still.jpeg", "image")]
    [InlineData("noext", "image")]
    public void Kind_follows_the_container(string path, string expected)
    {
        Assert.Equal(expected, MediaKinds.FromPath(path));
    }

    [Theory]
    [InlineData("a.mp4", "video/mp4")]
    [InlineData("a.webm", "video/webm")]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.png", "image/png")]
    [InlineData("a.mkv", "application/octet-stream")]
    public void Content_type_follows_the_container(string path, string expected)
    {
        Assert.Equal(expected, MediaKinds.ContentTypeFor(path));
    }
}
