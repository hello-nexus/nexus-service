using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// GET/PUT /apps-api/data/{appId}/{key} over the real request pipeline:
/// capability gating (capabilities.appData), CAS conflict, and the
/// validation 400s. Own AppRegistry fixture with two apps - one that
/// declares appData, one that does not - plus an AppDataStore isolated to a
/// temp directory (never the developer's real machine data directory).
/// </summary>
public sealed class AppDataRoutesFactory : NexusAppFactory
{
    public const string AppId = "com.test.appdata";
    public const string NoCapAppId = "com.test.nodata";

    private readonly string _fixtureRoot;
    public readonly string DataRoot;

    /// <summary>The profile the isolated store reports as active; tests flip it to simulate a profile switch.</summary>
    public volatile string ActiveProfile = "p1";

    public AppDataRoutesFactory()
    {
        _fixtureRoot = Path.Combine(Path.GetTempPath(), "nexus-appdata-fixture-" + Guid.NewGuid().ToString("N"));
        DataRoot = Path.Combine(Path.GetTempPath(), "nexus-appdata-store-" + Guid.NewGuid().ToString("N"));
        WriteApp(AppId, appData: true);
        WriteApp(NoCapAppId, appData: false);
    }

    private void WriteApp(string id, bool appData)
    {
        var bundle = Path.Combine(_fixtureRoot, id);
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "manifest.json"), $$"""
        {
          "schema": "nexus.app/1",
          "id": "{{id}}",
          "name": "App Data Fixture",
          "version": "1.0.0",
          "runtime": "sdk",
          "surfaces": ["dashboard"],
          "sizes": ["2x2"],
          "default_size": "2x2",
          "capabilities": { "appData": {{(appData ? "true" : "false")}} }
        }
        """);
        File.WriteAllText(Path.Combine(bundle, "widget.mjs"), "export const mount = () => {};");
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

            services.RemoveAll<AppDataStore>();
            var dataRoot = DataRoot;
            services.AddSingleton(sp =>
            {
                var store = new AppDataStore(() => dataRoot, () => ActiveProfile);
                var hub = sp.GetRequiredService<Nexus.Service.Sockets.MultiplexHub>();
                store.DocumentChanged += (profileId, appId, key) =>
                {
                    var (revision, updatedAt, data) = store.Get(profileId, appId, key);
                    Nexus.Service.Sockets.AppDataTopics.Broadcast(hub, appId, key,
                        new Nexus.Service.Models.Widgets.AppDataDocumentDto { ProfileId = profileId, Revision = revision, UpdatedAt = updatedAt, Data = data });
                };
                return store;
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_fixtureRoot, recursive: true); } catch { }
            try { Directory.Delete(DataRoot, recursive: true); } catch { }
        }
    }
}

public sealed class AppDataRoutesIntegrationTests : IClassFixture<AppDataRoutesFactory>
{
    private readonly AppDataRoutesFactory _factory;

    public AppDataRoutesIntegrationTests(AppDataRoutesFactory factory) => _factory = factory;

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Get_on_an_absent_document_returns_revision_zero()
    {
        var client = AuthedClient();
        var res = await client.GetAsync($"/apps-api/data/{AppDataRoutesFactory.AppId}/absent-key-1");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task Put_then_get_round_trips_the_document()
    {
        var client = AuthedClient();
        var put = await client.PutAsJsonAsync($"/apps-api/data/{AppDataRoutesFactory.AppId}/roundtrip",
            new { baseRevision = 0, data = new { fish = 3 } });

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var putBody = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, putBody.GetProperty("revision").GetInt32());

        var get = await client.GetAsync($"/apps-api/data/{AppDataRoutesFactory.AppId}/roundtrip");
        var getBody = await get.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, getBody.GetProperty("revision").GetInt32());
        Assert.Equal(3, getBody.GetProperty("data").GetProperty("fish").GetInt32());
    }

    [Fact]
    public async Task Put_with_a_stale_baseRevision_returns_409_with_the_current_document()
    {
        var client = AuthedClient();
        await client.PutAsJsonAsync($"/apps-api/data/{AppDataRoutesFactory.AppId}/conflict-key", new { baseRevision = 0, data = 1 });

        var res = await client.PutAsJsonAsync($"/apps-api/data/{AppDataRoutesFactory.AppId}/conflict-key", new { baseRevision = 0, data = 2 });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("revision").GetInt32());
        Assert.Equal(1, body.GetProperty("data").GetInt32());
    }

    [Fact]
    public async Task An_app_without_capabilities_appData_is_refused_with_403()
    {
        var client = AuthedClient();
        var res = await client.GetAsync($"/apps-api/data/{AppDataRoutesFactory.NoCapAppId}/save");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task An_uninstalled_app_id_is_refused_with_404()
    {
        var client = AuthedClient();
        var res = await client.GetAsync("/apps-api/data/com.test.not-installed/save");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task An_invalid_key_is_refused_with_400()
    {
        var client = AuthedClient();
        var res = await client.GetAsync($"/apps-api/data/{AppDataRoutesFactory.AppId}/BadKey");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task An_invalid_app_id_is_refused_with_400()
    {
        var client = AuthedClient();
        var res = await client.GetAsync("/apps-api/data/NotValid/save");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Put_without_a_data_field_is_refused_with_400_not_500()
    {
        var client = AuthedClient();
        var res = await client.PutAsJsonAsync($"/apps-api/data/{AppDataRoutesFactory.AppId}/missing-data", new { baseRevision = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_successful_put_broadcasts_the_new_document_over_the_multiplex_hub()
    {
        var client = AuthedClient();
        var hub = _factory.Services.GetRequiredService<Nexus.Service.Sockets.MultiplexHub>();
        var topic = Nexus.Service.Sockets.AppDataTopics.TopicFor(AppDataRoutesFactory.AppId, "broadcast-key");
        var captured = new List<byte[]>();
        void OnBroadcast(string t, ReadOnlyMemory<byte> payload) { if (t == topic) captured.Add(payload.ToArray()); }
        hub.OnBroadcastForTest += OnBroadcast;
        using var sub = hub.AddTestSubscription(topic);

        try
        {
            var res = await client.PutAsJsonAsync($"/apps-api/data/{AppDataRoutesFactory.AppId}/broadcast-key", new { baseRevision = 0, data = 42 });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            var frame = Assert.Single(captured);
            var json = Encoding.UTF8.GetString(frame);
            Assert.Contains("\"revision\":1", json);
            Assert.Contains("\"data\":42", json);
        }
        finally
        {
            hub.OnBroadcastForTest -= OnBroadcast;
        }
    }

    [Fact]
    public async Task Get_reports_the_active_profile_and_each_profile_has_its_own_document()
    {
        var client = AuthedClient();
        var url = $"/apps-api/data/{AppDataRoutesFactory.AppId}/per-profile";
        try
        {
            _factory.ActiveProfile = "pa";
            await client.PutAsJsonAsync(url, new { baseRevision = 0, data = "a" });
            var a = await (await client.GetAsync(url)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("pa", a.GetProperty("profileId").GetString());
            Assert.Equal("a", a.GetProperty("data").GetString());

            _factory.ActiveProfile = "pb";
            var b = await (await client.GetAsync(url)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("pb", b.GetProperty("profileId").GetString());
            Assert.Equal(0, b.GetProperty("revision").GetInt32());
        }
        finally
        {
            _factory.ActiveProfile = "p1";
        }
    }

    [Fact]
    public async Task Put_with_the_active_profileId_succeeds()
    {
        var client = AuthedClient();
        var res = await client.PutAsJsonAsync($"/apps-api/data/{AppDataRoutesFactory.AppId}/with-profile",
            new { baseRevision = 0, profileId = "p1", data = 1 });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Put_with_a_profileId_that_is_no_longer_active_returns_409_profile_switched_and_writes_nothing()
    {
        var client = AuthedClient();
        var url = $"/apps-api/data/{AppDataRoutesFactory.AppId}/stale-profile";

        var res = await client.PutAsJsonAsync(url, new { baseRevision = 0, profileId = "old-profile", data = 1 });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("profile_switched", body.GetProperty("code").GetString());
        Assert.Equal("p1", body.GetProperty("profileId").GetString());

        var get = await (await client.GetAsync(url)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, get.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task Put_with_null_data_is_rejected_with_400()
    {
        var client = AuthedClient();
        var res = await client.PutAsync($"/apps-api/data/{AppDataRoutesFactory.AppId}/null-doc",
            new StringContent("""{"baseRevision":0,"data":null}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
