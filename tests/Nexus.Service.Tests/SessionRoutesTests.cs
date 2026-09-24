using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// The stored route is replayed into a reopening window, so anything that is
/// not an absolute same-origin path must be dropped rather than stored.
/// </summary>
public class SessionRoutesTests
{
    [Theory]
    [InlineData("/system/monitoring/cpu")]
    [InlineData("/")]
    [InlineData("/system/diagnostics/cooling")]
    public void Sanitize_KeepsAnAbsoluteSameOriginPath(string path)
    {
        Assert.Equal(path, SessionRoutes.Sanitize(path));
    }

    [Theory]
    [InlineData("//evil.example.com/x")]
    [InlineData("/\\evil.example.com/x")]
    [InlineData("https://evil.example.com/x")]
    [InlineData("system/monitoring")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Sanitize_DropsAnythingThatCouldLeaveTheOrigin(string? path)
    {
        Assert.Equal("", SessionRoutes.Sanitize(path));
    }

    [Fact]
    public void Sanitize_DropsControlCharactersAndOverlongValues()
    {
        Assert.Equal("", SessionRoutes.Sanitize("/system/\nmonitoring"));
        Assert.Equal("", SessionRoutes.Sanitize("/" + new string('a', 300)));
    }

    [Fact]
    public void Sanitize_TrimsSurroundingWhitespace()
    {
        Assert.Equal("/system/settings", SessionRoutes.Sanitize("  /system/settings  "));
    }

    [Fact]
    public void SanitizeKeys_KeepsOrderAndDropsDuplicatesBlanksAndUnprintables()
    {
        var keys = new[] { "monitoring", " app:com.test.app ", "monitoring", "", "  ", "bad\nkey", "two words", "lighting" };
        Assert.Equal(["monitoring", "app:com.test.app", "lighting"], SessionRoutes.SanitizeKeys(keys));
    }

    [Fact]
    public void SanitizeKeys_BoundsCountAndKeyLength()
    {
        var many = new string[40];
        for (var i = 0; i < many.Length; i++)
        {
            many[i] = "app:" + i;
        }
        var bounded = SessionRoutes.SanitizeKeys(many);
        Assert.Equal(16, bounded.Length);
        Assert.Equal("app:24", bounded[0]);
        Assert.Equal("app:39", bounded[^1]);
        Assert.Empty(SessionRoutes.SanitizeKeys(["app:" + new string('a', 200)]));
        Assert.Empty(SessionRoutes.SanitizeKeys(null));
    }
}
