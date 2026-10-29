using System.Text.Json;
using Nexus.Service.Lifecycle;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests;

public class ReopenRouteTests
{
    [Theory]
    [InlineData("/system/lighting")]
    [InlineData("/")]
    [InlineData("/system/lighting?tab=effects")]
    [InlineData("/system/devices/np-50_a.b~c")]
    public void Accepts_in_app_paths_with_at_most_one_query(string route)
    {
        Assert.Equal(route, ReopenRoute.Validate(route));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("system/lighting")]
    [InlineData("http://evil.example/system/lighting")]
    [InlineData("https://localhost/x")]
    [InlineData("//evil.example/x")]
    [InlineData("/system//lighting")]
    [InlineData("/system\\lighting")]
    [InlineData("/\\evil.example")]
    [InlineData("/system/../settings")]
    [InlineData("/system/lighting\n")]
    [InlineData("/system/\u0001lighting")]
    [InlineData("/system/ lighting")]
    [InlineData("/system/lighting?a=1?b=2")]
    [InlineData("/system/lighting?a=1&b=2")]
    [InlineData("/system/%2e%2e/x")]
    [InlineData("javascript:alert(1)")]
    public void Rejects_everything_else(string? route)
    {
        Assert.Null(ReopenRoute.Validate(route));
    }

    [Fact]
    public void Rejects_a_route_over_the_length_bound()
    {
        Assert.NotNull(ReopenRoute.Validate("/" + new string('a', ReopenRoute.MaxLength - 1)));
        Assert.Null(ReopenRoute.Validate("/" + new string('a', ReopenRoute.MaxLength)));
    }

    [Fact]
    public void No_body_asks_for_nothing_and_is_accepted()
    {
        Assert.True(ReopenRoute.TryResolveRequest(null, out var route));
        Assert.Null(route);
    }

    [Fact]
    public void A_bad_requested_route_is_refused_and_a_good_one_is_passed_through()
    {
        Assert.False(ReopenRoute.TryResolveRequest("https://x", out _));
        Assert.False(ReopenRoute.TryResolveRequest("", out _));
        Assert.True(ReopenRoute.TryResolveRequest("/system/lighting", out var route));
        Assert.Equal("/system/lighting", route);
    }

    [Fact]
    public void The_request_body_binds_the_documented_property()
    {
        var withRoute = JsonSerializer.Deserialize("{\"reopenRoute\":\"/system/lighting\"}", AppJsonContext.Default.RestartBody);
        var empty = JsonSerializer.Deserialize("{}", AppJsonContext.Default.RestartBody);

        Assert.Equal("/system/lighting", withRoute!.ReopenRoute);
        Assert.Null(empty!.ReopenRoute);
    }

    [Fact]
    public void A_fresh_stored_route_is_read_and_a_stale_or_invalid_one_is_not()
    {
        var written = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal("/system/lighting", ReopenRoute.Parse("/system/lighting\n", written, written + TimeSpan.FromSeconds(30)));
        Assert.Equal("/system/lighting", ReopenRoute.Parse("/system/lighting", written, written + ReopenRoute.MaxAge));
        Assert.Null(ReopenRoute.Parse("/system/lighting", written, written + ReopenRoute.MaxAge + TimeSpan.FromSeconds(1)));
        Assert.Null(ReopenRoute.Parse("", written, written));
        Assert.Null(ReopenRoute.Parse("https://evil.example", written, written));
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), "nexus-reopen-" + Guid.NewGuid().ToString("N"), "reopen-route");

    [Fact]
    public void The_route_file_round_trips_once_and_is_deleted_on_consume()
    {
        var path = TempFile();
        try
        {
            Assert.Null(ReopenRoute.ConsumeFile(path));

            ReopenRoute.WriteFile("/system/lighting", path);
            Assert.True(File.Exists(path));
            Assert.Equal("/system/lighting", ReopenRoute.ConsumeFile(path));

            Assert.False(File.Exists(path));
            Assert.Null(ReopenRoute.ConsumeFile(path));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_stale_route_file_is_deleted_and_ignored()
    {
        var path = TempFile();
        try
        {
            ReopenRoute.WriteFile("/system/lighting", path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - ReopenRoute.MaxAge - TimeSpan.FromMinutes(1));

            Assert.Null(ReopenRoute.ConsumeFile(path));
            Assert.False(File.Exists(path));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { }
        }
    }
}
