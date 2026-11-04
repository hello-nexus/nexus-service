using System;
using System.Text.RegularExpressions;
using Nexus.Service.Platform;
using Xunit;

namespace Nexus.Service.Tests.Platform;

public class LogLineFormatTests
{
    private static readonly DateTime At = new(2026, 11, 4, 8, 58, 7, 331, DateTimeKind.Utc);

    [Fact]
    public void Line_UsesCanonicalShape_WithTagFromMessage()
    {
        var line = LogLineFormat.Line(At, LogLineFormat.Info, "helper", "[ddc] probed id=X capable=False");
        Assert.Equal("2026-11-04T08:58:07.331Z INF [ddc] probed id=X capable=False", line);
    }

    [Fact]
    public void Line_PrefixesDefaultTag_WhenMessageHasNone()
    {
        var line = LogLineFormat.Line(At, LogLineFormat.Warn, "helper", "WindowsUserHelper.Run entered");
        Assert.Equal("2026-11-04T08:58:07.331Z WRN [helper] WindowsUserHelper.Run entered", line);
    }

    [Fact]
    public void Line_PlacesPidAfterTag()
    {
        var line = LogLineFormat.Line(At, LogLineFormat.Error, "tray", "tray ready", pid: 42);
        Assert.Equal("2026-11-04T08:58:07.331Z ERR [tray] pid=42 tray ready", line);
    }

    [Fact]
    public void Line_ConvertsLocalTimeToUtc()
    {
        var local = new DateTime(2026, 11, 4, 8, 0, 0, DateTimeKind.Local);
        var line = LogLineFormat.Line(local, LogLineFormat.Info, "x", "m");
        Assert.StartsWith(local.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), line);
    }

    [Theory]
    [InlineData("[not a tag] body", "[helper] [not a tag] body")]
    [InlineData("[] body", "[helper] [] body")]
    [InlineData("[a[b] body", "[helper] [a[b] body")]
    public void Line_DoesNotTreatMalformedBracketsAsTag(string message, string expectedTail)
    {
        var line = LogLineFormat.Line(At, LogLineFormat.Info, "helper", message);
        Assert.EndsWith(" INF " + expectedTail, line);
    }

    [Fact]
    public void Line_FoldsMultiLineMessagesIntoOneLine()
    {
        var line = LogLineFormat.Line(At, LogLineFormat.Info, "gpu", "[gpu] compile failed\r\nline 1\n\nline 2\n");
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.EndsWith("[gpu] compile failed | line 1 | line 2", line);
    }

    [Fact]
    public void Line_EmptyMessage_HasNoTrailingSpace()
    {
        var line = LogLineFormat.Line(At, LogLineFormat.Info, "gpu", "");
        Assert.Equal("2026-11-04T08:58:07.331Z INF [gpu]", line);
    }

    [Fact]
    public void Line_MatchesServiceLogShape()
    {
        var line = LogLineFormat.Line(At, LogLineFormat.Info, "helper", "[ddc] x=1", 7);
        Assert.Matches(new Regex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z (INF|WRN|ERR) \[[a-z0-9-]+\]"), line);
    }

    [Theory]
    [InlineData("Microsoft.Hosting.Lifetime", "lifetime")]
    [InlineData("Nexus.Service.Discovery.MdnsAdvertiser", "mdns-advertiser")]
    [InlineData("Microsoft.AspNetCore.Server.Kestrel", "kestrel")]
    [InlineData("HTTPClient", "http-client")]
    [InlineData("", "log")]
    [InlineData("Ends.With.", "log")]
    public void Kebab_UsesLastSegmentInKebabCase(string category, string expected)
    {
        Assert.Equal(expected, LogLineFormat.Kebab(category));
    }
}
