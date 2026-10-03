using System.Linq;
using Nexus.Service.Telemetry;
using Nexus.Service.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Telemetry;

public class AppLinkTelemetryTests
{
    [Fact]
    public void Keeps_host_and_utm_and_drops_the_rest_of_the_query()
    {
        var props = AppLinkTelemetry.Properties("com.ibuypower.control", "1.2.3",
            "https://www.ibuypower.com/login/account/register-warranty?product-key=ABCDE-12345&utm_source=nexus&utm_campaign=nexus_ibp_warranty&utm_content=extend")!
            .ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal("com.ibuypower.control", props["app_id"]);
        Assert.Equal("1.2.3", props["app_version"]);
        Assert.Equal("www.ibuypower.com", props["host"]);
        Assert.Equal("nexus", props["utm_source"]);
        Assert.Equal("nexus_ibp_warranty", props["utm_campaign"]);
        Assert.Equal("extend", props["utm_content"]);
        Assert.DoesNotContain(props.Values, v => v is string s && s.Contains("ABCDE"));
        Assert.DoesNotContain("utm_medium", props.Keys);
    }

    [Theory]
    [InlineData("nexus", true)]
    [InlineData("nexus_ibp_chimera", true)]
    [InlineData("header_chip", true)]
    [InlineData("ABCDE-12345", false)]
    [InlineData("a%20b", false)]
    [InlineData("a@b.com", false)]
    [InlineData("-lead", false)]
    public void Keeps_only_token_shaped_utm_values(string raw, bool kept)
    {
        var props = AppLinkTelemetry.Properties("a.b", "1.0.0", $"https://x.com/?utm_term={raw}")!
            .ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(kept, props.ContainsKey("utm_term"));
    }

    [Fact]
    public void Drops_overlong_utm_values()
    {
        var props = AppLinkTelemetry.Properties("a.b", "1.0.0", $"https://x.com/?utm_content={new string('z', 65)}")!;
        Assert.DoesNotContain(props, p => p.Key == "utm_content");
    }

    [Theory]
    [InlineData("https://192.168.1.5/x", "private")]
    [InlineData("https://[::1]/x", "private")]
    [InlineData("http://nas/x", "private")]
    [InlineData("http://printer.local/x", "private")]
    [InlineData("http://box.lan./x", "private")]
    [InlineData("http://a.home/x", "private")]
    [InlineData("http://a.internal/x", "private")]
    [InlineData("http://a.localdomain/x", "private")]
    [InlineData("https://www.ibuypower.com/x", "www.ibuypower.com")]
    public void Sends_private_for_lan_hosts(string url, string host)
    {
        var props = AppLinkTelemetry.Properties("a.b", "1.0.0", url)!.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(host, props["host"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("mailto:support@example.com")]
    public void Ignores_anything_but_a_web_link(string url)
    {
        Assert.Null(AppLinkTelemetry.Properties("a.b", "1.0.0", url));
    }

    private sealed class RecordingTelemetry : ITelemetry
    {
        public List<string> Events { get; } = new();
        public void Capture(string @event, params (string Key, object? Value)[] properties) => Events.Add(@event);
        public void Identify(params (string Key, object? Value)[] properties) { }
    }

    private static string Fixture(string id)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-linktele-" + Guid.NewGuid().ToString("N")[..8]);
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            $$"""{"schema":"nexus.app/1","id":"{{id}}","name":"x","version":"1.0.0","runtime":"sdk","surfaces":["dashboard"],"sizes":["2x2"]}""");
        File.WriteAllText(Path.Combine(dir, "widget.mjs"), "export const mount = () => {};");
        return root;
    }

    [Fact]
    public void Link_capture_is_throttled_per_app_and_ignores_unknown_apps()
    {
        var root = Fixture("com.test.link");
        try
        {
            var registry = new AppRegistry(() => new List<AppInstallPaths.Root> { new(root, AppInstallPaths.Source.User) });
            var telemetry = new RecordingTelemetry();
            var now = DateTimeOffset.FromUnixTimeSeconds(6000);
            var limiter = new AppTelemetryRateLimiter(30, () => now);

            for (var i = 0; i < 40; i++) AppLinkTelemetry.Capture(telemetry, registry, "com.test.link", "https://x.com/", limiter);
            Assert.Equal(30, telemetry.Events.Count);
            now = now.AddSeconds(60);
            AppLinkTelemetry.Capture(telemetry, registry, "com.test.link", "https://x.com/", limiter);
            Assert.Equal(31, telemetry.Events.Count);

            AppLinkTelemetry.Capture(telemetry, registry, "com.test.other", "https://x.com/", limiter);
            Assert.Equal(31, telemetry.Events.Count);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void Page_opened_capture_is_throttled_per_app()
    {
        var telemetry = new RecordingTelemetry();
        var limiter = new AppTelemetryRateLimiter(30, () => DateTimeOffset.FromUnixTimeSeconds(6000));
        for (var i = 0; i < 40; i++) AppPageTelemetry.CaptureOpened(telemetry, "a.b.c", "1.0.0", limiter);
        AppPageTelemetry.CaptureOpened(telemetry, "x.y.z", "1.0.0", limiter);
        Assert.Equal(31, telemetry.Events.Count);
    }

    [Fact]
    public void Rate_limiter_is_per_app_and_resets_next_minute()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(6000);
        var limiter = new AppTelemetryRateLimiter(60, () => now);
        for (var i = 0; i < 60; i++) Assert.True(limiter.TryAcquire("a.b.c"));
        Assert.False(limiter.TryAcquire("a.b.c"));
        Assert.True(limiter.TryAcquire("x.y.z"));
        now = now.AddSeconds(60);
        Assert.True(limiter.TryAcquire("a.b.c"));
    }

    [Fact]
    public void Opted_out_suppresses_page_and_link_events()
    {
        var root = Fixture("com.test.optout");
        try
        {
            var store = new InMemoryConfigStore();
            store.Update(x => x.Telemetry.CollectAnonymousData = false);
            var client = new TelemetryClient(store);
            var registry = new AppRegistry(() => new List<AppInstallPaths.Root> { new(root, AppInstallPaths.Source.User) });

            AppPageTelemetry.CaptureOpened(client, "com.test.optout", "1.0.0", new AppTelemetryRateLimiter(30));
            AppLinkTelemetry.Capture(client, registry, "com.test.optout", "https://x.com/", new AppTelemetryRateLimiter(30));
            Assert.Empty(client.DrainBatch(10));

            store.Update(x => x.Telemetry.CollectAnonymousData = true);
            AppPageTelemetry.CaptureOpened(client, "com.test.optout", "1.0.0", new AppTelemetryRateLimiter(30));
            Assert.Single(client.DrainBatch(10));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void Open_url_request_carries_the_app_id_from_the_host()
    {
        var body = System.Text.Json.JsonSerializer.Deserialize(
            """{"url":"https://x.com/","appId":"com.test.link"}""",
            Nexus.Service.Serialization.AppJsonContext.Default.OpenUrlRequest)!;
        Assert.Equal("com.test.link", body.AppId);
    }
}
