using Nexus.Service.Platform;
using Xunit;

namespace Nexus.Service.Tests;

public class OpenAppRouteTests
{
    [Theory]
    [InlineData("/system/monitoring")]
    [InlineData("/system/devices/abc-1_2")]
    [InlineData("/system/cooling?tab=fans")]
    public void Accepts_in_app_system_paths(string route)
        => Assert.Equal(route, OpenAppRoute.Normalize(route));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/system/")]
    [InlineData("/settings")]
    [InlineData("system/monitoring")]
    [InlineData("https://evil.example/system/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example/system/x")]
    [InlineData("/system//evil.example")]
    [InlineData("/system/../settings")]
    [InlineData("/system/a\\b")]
    [InlineData("/system/a\nb")]
    [InlineData("/system/a\0b")]
    [InlineData("/system/a b")]
    [InlineData("/system/a\"b")]
    [InlineData("/system/a&b")]
    [InlineData("/system/a%2e%2e")]
    [InlineData("/system/@evil.example")]
    [InlineData("/system/a:b")]
    public void Rejects_everything_else(string? route)
        => Assert.Null(OpenAppRoute.Normalize(route));

    [Fact]
    public void Rejects_an_overlong_route()
        => Assert.Null(OpenAppRoute.Normalize("/system/" + new string('a', 300)));

    [Fact]
    public void LocalUrl_joins_the_route_to_the_dashboard_origin_or_falls_back_to_the_root()
    {
        Assert.Equal("http://localhost:9400/system/monitoring", OpenAppRoute.LocalUrl(0, "/system/monitoring"));
        Assert.Equal("http://localhost:9500/", OpenAppRoute.LocalUrl(9500, null));
    }

    [Fact]
    public void Body_deserializes_with_the_source_generated_context()
    {
        var body = System.Text.Json.JsonSerializer.Deserialize("{\"route\":\"/system/x\"}", Nexus.Service.Serialization.AppJsonContext.Default.OpenAppBody);
        Assert.Equal("/system/x", body!.Route);
    }
}
