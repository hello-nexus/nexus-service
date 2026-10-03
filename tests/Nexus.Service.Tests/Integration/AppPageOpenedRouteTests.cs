using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Telemetry;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Integration;

public sealed class AppPageOpenedFactory : NexusAppFactory
{
    public const string AppId = "com.test.pageopened";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-pageopened-" + Guid.NewGuid().ToString("N"));

    public List<(string Event, (string Key, object? Value)[] Props)> Captured { get; } = new();

    public AppPageOpenedFactory()
    {
        var dir = Path.Combine(_root, AppId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            $$"""{"schema":"nexus.app/1","id":"{{AppId}}","name":"x","version":"7.8.9","runtime":"sdk","surfaces":["dashboard"],"sizes":["2x2"]}""");
        File.WriteAllText(Path.Combine(dir, "widget.mjs"), "export const mount = () => {};");
    }

    private sealed class Recorder(List<(string Event, (string Key, object? Value)[] Props)> sink) : ITelemetry
    {
        public void Capture(string @event, params (string Key, object? Value)[] properties)
        {
            lock (sink) sink.Add((@event, properties));
        }

        public void Identify(params (string Key, object? Value)[] properties) { }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<AppRegistry>();
            services.AddSingleton(new AppRegistry(() => new List<AppInstallPaths.Root> { new(_root, AppInstallPaths.Source.User) }));
            services.RemoveAll<ITelemetry>();
            services.AddSingleton<ITelemetry>(new Recorder(Captured));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}

public sealed class AppPageOpenedRouteTests : IClassFixture<AppPageOpenedFactory>
{
    private readonly AppPageOpenedFactory _factory;

    public AppPageOpenedRouteTests(AppPageOpenedFactory factory) => _factory = factory;

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    [Fact]
    public async Task Page_opened_records_the_installed_version_and_404s_for_unknown_apps()
    {
        var client = Client();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/apps-api/page-opened/com.test.missing", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/apps-api/page-opened/{AppPageOpenedFactory.AppId}", null)).StatusCode);

        var e = _factory.Captured.First(c => c.Event == TelemetryEvents.AppPageOpened);
        var props = e.Props.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(AppPageOpenedFactory.AppId, props["app_id"]);
        Assert.Equal("7.8.9", props["app_version"]);
    }

    [Fact]
    public async Task Page_opened_still_answers_200_beyond_the_throttle()
    {
        var client = Client();
        for (var i = 0; i < 40; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/apps-api/page-opened/{AppPageOpenedFactory.AppId}", null)).StatusCode);
        }
        Assert.True(_factory.Captured.Count(c => c.Event == TelemetryEvents.AppPageOpened) <= 31);
    }
}
