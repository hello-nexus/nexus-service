#if DEV_TOOLS
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Store;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Integration;

public sealed class AppTelemetryRoutesFactory : NexusAppFactory
{
    public const string OnApp = "com.test.tele-on";
    public const string OffApp = "com.test.tele-off";
    public const string BurstApp = "com.test.tele-burst";

    public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(6000);

    private readonly string _fixtureRoot = Path.Combine(Path.GetTempPath(), "nexus-tele-fixture-" + Guid.NewGuid().ToString("N"));

    public AppTelemetryRoutesFactory()
    {
        WriteApp(OnApp, telemetry: true, withStore: true);
        WriteApp(OffApp, telemetry: false, withStore: false);
        WriteApp(BurstApp, telemetry: true, withStore: false);
    }

    private void WriteApp(string id, bool telemetry, bool withStore)
    {
        var bundle = Path.Combine(_fixtureRoot, id);
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "manifest.json"), $$"""
        {
          "schema": "nexus.app/1", "id": "{{id}}", "name": "Tele {{id}}", "version": "4.5.6", "runtime": "sdk",
          "surfaces": ["dashboard"], "sizes": ["2x2"], "default_size": "2x2",
          "capabilities": { "telemetry": {{(telemetry ? "true" : "false")}} }
        }
        """);
        File.WriteAllText(Path.Combine(bundle, "widget.mjs"), "export const mount = () => {};");
        if (!withStore) return;
        Directory.CreateDirectory(Path.Combine(bundle, "store"));
        File.WriteAllBytes(Path.Combine(bundle, "store", "icon.png"), new byte[] { 9, 9 });
        File.WriteAllText(Path.Combine(bundle, "store", "x.svg"), "<svg/>");
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<AppRegistry>();
            services.AddSingleton(new AppRegistry(() => new List<AppInstallPaths.Root>
            {
                new(_fixtureRoot, AppInstallPaths.Source.User),
            }));
            services.RemoveAll<Nexus.Service.Telemetry.AppTelemetryRateLimiter>();
            services.AddSingleton(new Nexus.Service.Telemetry.AppTelemetryRateLimiter(60, () => Now));
            services.RemoveAll<StoreCatalogProxy>();
            services.AddSingleton(new StoreCatalogProxy(new HttpClient(new NotFoundHandler())));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_fixtureRoot, recursive: true); } catch { }
        }
    }
}

public sealed class AppTelemetryRoutesIntegrationTests : IClassFixture<AppTelemetryRoutesFactory>
{
    private readonly AppTelemetryRoutesFactory _factory;

    public AppTelemetryRoutesIntegrationTests(AppTelemetryRoutesFactory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static string Url(string appId) => $"/apps-api/telemetry/{appId}";

    private async Task<JsonElement> Recent()
    {
        var res = await AuthedClient().GetAsync("/apps-api/telemetry/recent");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Event_for_an_unknown_app_is_404_and_for_an_app_without_the_capability_is_403()
    {
        var client = AuthedClient();
        var body = new { @event = "tab_viewed" };

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(Url("com.test.missing"), body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(Url("..%2Fx"), body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(Url(AppTelemetryRoutesFactory.OffApp), body)).StatusCode);
    }

    [Theory]
    [InlineData("""{"event":"Bad Event"}""")]
    [InlineData("""{"event":"ok","surface":"nope"}""")]
    [InlineData("""{"event":"ok","properties":{"k":"free text here"}}""")]
    public async Task Invalid_bodies_are_400(string json)
    {
        var res = await AuthedClient().PostAsync(Url(AppTelemetryRoutesFactory.OnApp),
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Event_is_recorded_with_the_installed_version_and_flat_props()
    {
        var res = await AuthedClient().PostAsJsonAsync(Url(AppTelemetryRoutesFactory.OnApp),
            new { @event = "route_probe", surface = "page", properties = new { tab = "orders", n = 2 } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var events = (await Recent()).GetProperty("events").EnumerateArray()
            .Where(e => e.GetProperty("properties").TryGetProperty("event", out var n) && n.GetString() == "route_probe").ToList();
        var p = Assert.Single(events).GetProperty("properties");
        Assert.Equal("app_event", events[0].GetProperty("event").GetString());
        Assert.Equal(AppTelemetryRoutesFactory.OnApp, p.GetProperty("app_id").GetString());
        Assert.Equal("4.5.6", p.GetProperty("app_version").GetString());
        Assert.Equal("page", p.GetProperty("surface").GetString());
        Assert.True(p.GetProperty("dev_tools").GetBoolean());
        Assert.Equal("orders", p.GetProperty("p_tab").GetString());
        Assert.Equal(2, p.GetProperty("p_n").GetDouble());
    }

    [Fact]
    public async Task Page_opened_and_closed_carry_the_version_and_clamped_duration()
    {
        var client = AuthedClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/apps-api/page-opened/{AppTelemetryRoutesFactory.OffApp}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            $"/apps-api/page-closed/{AppTelemetryRoutesFactory.OffApp}", new { durationMs = 999999999999d })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(
            "/apps-api/page-closed/com.test.missing", new { durationMs = 5 })).StatusCode);

        var events = (await Recent()).GetProperty("events").EnumerateArray().ToList();
        var closed = events.First(e => e.GetProperty("event").GetString() == "app_page_closed").GetProperty("properties");
        Assert.Equal(86400, closed.GetProperty("duration_s").GetDouble());
        Assert.Equal("4.5.6", closed.GetProperty("app_version").GetString());
        var opened = events.First(e => e.GetProperty("event").GetString() == "app_page_opened").GetProperty("properties");
        Assert.Equal("4.5.6", opened.GetProperty("app_version").GetString());
    }

    [Fact]
    public async Task Recent_reports_a_known_posthog_status()
    {
        var status = (await Recent()).GetProperty("posthog").GetString();
        Assert.Contains(status, new[] { "on", "off", "opted_out" });
    }

    [Fact]
    public async Task Sixty_first_event_in_a_minute_is_429()
    {
        var client = AuthedClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 61; i++)
        {
            statuses.Add((await client.PostAsJsonAsync(Url(AppTelemetryRoutesFactory.BurstApp), new { @event = "burst" })).StatusCode);
        }
        Assert.Equal(60, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[60]);
    }

    [Fact]
    public async Task Local_preview_fills_in_when_the_cloud_has_no_listing()
    {
        var client = AuthedClient();

        var detail = await client.GetAsync($"/apps-api/store/apps/{AppTelemetryRoutesFactory.OnApp}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var d = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(d.GetProperty("localPreview").GetBoolean());
        Assert.Equal("4.5.6", d.GetProperty("latest").GetProperty("version").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/apps-api/store/apps/com.test.missing")).StatusCode);
    }

    [Fact]
    public async Task Local_media_serves_images_with_nosniff_and_refuses_traversal()
    {
        var client = AuthedClient();
        var png = await client.GetAsync($"/apps-api/store/local-media/{AppTelemetryRoutesFactory.OnApp}/icon.png");
        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        Assert.Equal("image/png", png.Content.Headers.ContentType?.MediaType);
        Assert.Contains("nosniff", png.Headers.GetValues("X-Content-Type-Options"));

        var svg = await client.GetAsync($"/apps-api/store/local-media/{AppTelemetryRoutesFactory.OnApp}/x.svg");
        Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", svg.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains("sandbox", svg.Headers.GetValues("Content-Security-Policy").Single());

        foreach (var path in new[] { "../manifest.json", "..%2Fmanifest.json", "%2e%2e/manifest.json", "missing.png" })
        {
            var res = await client.GetAsync($"/apps-api/store/local-media/{AppTelemetryRoutesFactory.OnApp}/{path}");
            Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/apps-api/store/local-media/com.test.missing/icon.png")).StatusCode);
    }
}
#endif
