using System.IO.Compression;
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
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Integration;

/// <summary>Host whose AppDataStore is rooted in a temp dir (never the machine's real data) and wired exactly like production.</summary>
public sealed class ProfileAppDataFactory : NexusAppFactory
{
    public readonly string DataRoot = Path.Combine(Path.GetTempPath(), "nexus-profile-appdata-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var root = DataRoot;
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<AppDataStore>();
            services.AddSingleton(sp =>
            {
                var profiles = sp.GetRequiredService<ProfileManager>();
                return AppDataStoreWiring.Wire(new AppDataStore(() => root, () => profiles.ActiveProfileId), profiles, sp.GetRequiredService<MultiplexHub>());
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(DataRoot, recursive: true); } catch { }
        }
    }
}

/// <summary>Profile lifecycle, file export/import/inspect and the reset signal against per-profile app data, over the real request pipeline.</summary>
public sealed class ProfileAppDataRoutesIntegrationTests : IDisposable
{
    private const string App = "com.test.app";
    private readonly ProfileAppDataFactory _factory = new();
    private readonly ProfileManager _profiles;
    private readonly AppDataStore _appData;
    private readonly List<string> _resetFrames = new();
    private readonly IDisposable _resetSub;

    public ProfileAppDataRoutesIntegrationTests()
    {
        _profiles = _factory.Services.GetRequiredService<ProfileManager>();
        _profiles.Initialize();
        _appData = _factory.Services.GetRequiredService<AppDataStore>();
        var hub = _factory.Services.GetRequiredService<MultiplexHub>();
        hub.OnBroadcastForTest += (topic, payload) =>
        {
            if (topic == AppDataTopics.ResetTopic)
            {
                lock (_resetFrames) { _resetFrames.Add(Encoding.UTF8.GetString(payload.Span)); }
            }
        };
        _resetSub = hub.AddTestSubscription(AppDataTopics.ResetTopic);
    }

    public void Dispose()
    {
        _resetSub.Dispose();
        _factory.Dispose();
    }

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static JsonElement J(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private int ResetCount() { lock (_resetFrames) { return _resetFrames.Count; } }

    private static async Task<string> ExportAsync(HttpClient client, string id)
    {
        var res = await client.GetAsync($"/profiles/{id}/export");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType!.MediaType);
        return await res.Content.ReadAsStringAsync();
    }

    private static ByteArrayContent Bytes(byte[] bytes) => new(bytes);

    private static StringContent Json(string raw) => new(raw, Encoding.UTF8, "application/json");

    private static byte[] LegacyZip(string profileJson, string appId, string key, string docJson)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = zip.CreateEntry("profile.json").Open()) { s.Write(Encoding.UTF8.GetBytes(profileJson)); }
            using (var s = zip.CreateEntry($"app-data/{appId}/{key}.json").Open()) { s.Write(Encoding.UTF8.GetBytes(docJson)); }
        }
        return ms.ToArray();
    }

    // ── lifecycle ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_new_profile_starts_with_no_app_data_and_becomes_active()
    {
        var client = AuthedClient();
        var defaultId = _profiles.ActiveProfileId;
        _appData.Put(defaultId, App, "save", 0, J("1"));

        var res = await client.PostAsJsonAsync("/profiles/create", new { name = "Second" });
        var secondId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("profile").GetProperty("id").GetString()!;

        Assert.Equal(secondId, _appData.ActiveProfileId);
        Assert.Empty(_appData.ReadProfile(secondId));
        Assert.Single(_appData.ReadProfile(defaultId));
    }

    [Fact]
    public async Task Deleting_a_profile_deletes_its_app_data_only()
    {
        var client = AuthedClient();
        var defaultId = _profiles.ActiveProfileId;
        var secondId = _profiles.CreateProfile("Second").Id;
        _appData.Put(defaultId, App, "save", 0, J("1"));
        _appData.Put(secondId, App, "save", 0, J("2"));

        var res = await client.DeleteAsync($"/profiles/{secondId}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_factory.DataRoot, "profiles", secondId)));
        Assert.Single(_appData.ReadProfile(defaultId));
    }

    [Fact]
    public async Task Switching_profile_changes_which_document_the_active_store_reads_and_broadcasts_a_reset()
    {
        var client = AuthedClient();
        var defaultId = _profiles.ActiveProfileId;
        var secondId = _profiles.CreateProfile("Second").Id;
        _profiles.SwitchProfile(defaultId);
        _appData.Put(defaultId, App, "save", 0, J("1"));
        var before = ResetCount();

        var res = await client.PostAsync($"/profiles/{secondId}/switch", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(secondId, _appData.ActiveProfileId);
        Assert.Equal(0, _appData.Get(_appData.ActiveProfileId, App, "save").Revision);
        Assert.Equal(before + 1, ResetCount());
        var frame = JsonDocument.Parse(_resetFrames[^1]).RootElement.GetProperty("d");
        Assert.Equal(secondId, frame.GetProperty("profileId").GetString());
        Assert.False(string.IsNullOrEmpty(frame.GetProperty("resetId").GetString()));
    }

    [Fact]
    public async Task Deleting_the_active_profile_resets_apps_onto_the_new_active_profile()
    {
        var defaultId = _profiles.ActiveProfileId;
        var secondId = _profiles.CreateProfile("Second").Id;
        var before = ResetCount();

        var res = await AuthedClient().DeleteAsync($"/profiles/{secondId}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(defaultId, _appData.ActiveProfileId);
        Assert.Equal(before + 1, ResetCount());
    }

    // ── export ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_is_one_json_bundle_with_name_settings_and_the_profiles_app_data()
    {
        var client = AuthedClient();
        var id = _profiles.ActiveProfileId;
        _appData.Put(id, App, "save", 0, J("""{"fish":4}"""));
        var other = _profiles.CreateProfile("Other").Id;
        _appData.Put(other, "com.other.app", "save", 0, J("1"));
        _profiles.SwitchProfile(id);

        var json = await ExportAsync(client, id);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Default", doc.RootElement.GetProperty("name").GetString());
        Assert.True(doc.RootElement.TryGetProperty("settings", out _));
        var appData = doc.RootElement.GetProperty("appData");
        Assert.Equal(4, appData.GetProperty(App).GetProperty("save").GetProperty("fish").GetInt32());
        Assert.False(appData.TryGetProperty("com.other.app", out _));
        Assert.DoesNotContain("revision", appData.GetRawText());
    }

    [Fact]
    public async Task Export_ignores_the_legacy_format_query_and_never_returns_a_zip()
    {
        var client = AuthedClient();
        var res = await client.GetAsync($"/profiles/{_profiles.ActiveProfileId}/export?format=archive");

        Assert.Equal("application/json", res.Content.Headers.ContentType!.MediaType);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.False(bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B);
    }

    [Fact]
    public async Task Export_of_an_unknown_profile_is_404()
    {
        var res = await AuthedClient().GetAsync("/profiles/nope/export");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ── import ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Import_of_a_bundle_creates_a_new_profile_and_writes_its_app_data_to_that_profile()
    {
        var client = AuthedClient();
        var sourceId = _profiles.ActiveProfileId;
        _appData.Put(sourceId, App, "save", 0, J("""{"fish":4}"""));
        var bundle = (await ExportAsync(client, sourceId)).Replace("\"Default\"", "\"Copy\"");

        var res = await client.PostAsync("/profiles/import", Json(bundle));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var newId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("profile").GetProperty("id").GetString()!;
        Assert.NotEqual(sourceId, newId);
        Assert.Equal(4, _appData.ReadProfile(newId)[App]["save"].GetProperty("fish").GetInt32());
        Assert.Single(_appData.ReadProfile(sourceId));
    }

    [Fact]
    public async Task Import_with_appData_skip_writes_no_app_data()
    {
        var client = AuthedClient();
        var sourceId = _profiles.ActiveProfileId;
        _appData.Put(sourceId, App, "save", 0, J("1"));
        var bundle = (await ExportAsync(client, sourceId)).Replace("\"Default\"", "\"Copy\"");

        var res = await client.PostAsync("/profiles/import?appData=skip", Json(bundle));

        var newId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("profile").GetProperty("id").GetString()!;
        Assert.Empty(_appData.ReadProfile(newId));
    }

    [Fact]
    public async Task Import_on_a_name_conflict_is_409_and_writes_no_app_data()
    {
        var client = AuthedClient();
        var sourceId = _profiles.ActiveProfileId;
        _appData.Put(sourceId, App, "save", 0, J("1"));
        var bundle = await ExportAsync(client, sourceId);
        _appData.Delete(sourceId, App, "save");

        var res = await client.PostAsync("/profiles/import", Json(bundle));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Empty(_appData.ReadProfile(sourceId));
    }

    [Fact]
    public async Task Replace_import_replaces_only_the_apps_in_the_bundle_and_resets_when_the_target_is_active()
    {
        var client = AuthedClient();
        var id = _profiles.ActiveProfileId;
        _appData.Put(id, App, "save", 0, J("""{"fish":4}"""));
        _appData.Put(id, App, "extra", 0, J("1"));
        _appData.Put(id, "com.untouched.app", "save", 0, J("1"));
        var bundle = await ExportAsync(client, id);
        var withoutExtra = bundle.Replace("\"extra\"", "\"extra-gone\"");
        _appData.Put(id, App, "save", 1, J("""{"fish":99}"""));
        var before = ResetCount();

        var res = await client.PostAsync("/profiles/import?replace=true", Json(withoutExtra));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var after = _appData.ReadProfile(id);
        Assert.Equal(4, after[App]["save"].GetProperty("fish").GetInt32());
        Assert.False(after[App].ContainsKey("extra"));
        Assert.True(after[App].ContainsKey("extra-gone"));
        Assert.True(after.ContainsKey("com.untouched.app"));
        Assert.True(ResetCount() > before);
    }

    [Fact]
    public async Task Import_of_a_legacy_bare_json_without_app_data_succeeds_and_writes_none()
    {
        var client = AuthedClient();
        var settingsJson = JsonDocument.Parse(await ExportAsync(client, _profiles.ActiveProfileId)).RootElement.GetProperty("settings").GetRawText();

        var res = await client.PostAsync("/profiles/import", Json($$"""{"name":"Legacy","settings":{{settingsJson}}}"""));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var newId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("profile").GetProperty("id").GetString()!;
        Assert.Empty(_appData.ReadProfile(newId));
    }

    [Fact]
    public async Task Import_of_a_legacy_zip_is_sniffed_by_magic_bytes_and_applies_its_app_data()
    {
        var client = AuthedClient();
        var export = await ExportAsync(client, _profiles.ActiveProfileId);
        var profileJson = export.Replace("\"Default\"", "\"From Zip\"");
        var zip = LegacyZip(profileJson, App, "save", """{"revision":3,"updatedAt":"t","data":{"fish":7}}""");

        var res = await client.PostAsync("/profiles/import", Bytes(zip));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var newId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("profile").GetProperty("id").GetString()!;
        Assert.Equal(7, _appData.ReadProfile(newId)[App]["save"].GetProperty("fish").GetInt32());
    }

    [Fact]
    public async Task A_json_file_is_never_misread_as_a_zip_by_its_name_and_a_zip_with_a_json_content_type_still_reads()
    {
        var client = AuthedClient();
        var export = await ExportAsync(client, _profiles.ActiveProfileId);
        var zip = LegacyZip(export.Replace("\"Default\"", "\"Zip2\""), App, "save", """{"revision":1,"updatedAt":"t","data":1}""");
        var content = Bytes(zip);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var res = await client.PostAsync("/profiles/import", content);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Import_drops_invalid_app_ids_and_keys_from_a_file()
    {
        var client = AuthedClient();
        var settingsJson = JsonDocument.Parse(await ExportAsync(client, _profiles.ActiveProfileId)).RootElement.GetProperty("settings").GetRawText();
        var body = "{\"name\":\"Hostile\",\"settings\":" + settingsJson + ",\"appData\":{\"../evil\":{\"save\":1},\"com.test.app\":{\"../escape\":1,\"ok\":2}}}";

        var res = await client.PostAsync("/profiles/import", Json(body));

        var newId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("profile").GetProperty("id").GetString()!;
        var written = _appData.ReadProfile(newId);
        Assert.Equal(new[] { App }, written.Keys);
        Assert.Equal(new[] { "ok" }, written[App].Keys);
        Assert.False(Directory.Exists(Path.Combine(_factory.DataRoot, "evil")));
    }

    [Fact]
    public async Task Import_of_unreadable_bytes_is_400()
    {
        var res = await AuthedClient().PostAsync("/profiles/import", Json("not json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ── inspect ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Inspect_reports_name_app_ids_and_format_for_a_bundle_and_writes_nothing()
    {
        var client = AuthedClient();
        var id = _profiles.ActiveProfileId;
        _appData.Put(id, App, "save", 0, J("1"));
        _appData.Put(id, "com.alpha.app", "save", 0, J("1"));
        var bundle = (await ExportAsync(client, id)).Replace("\"Default\"", "\"Inspected\"");
        var profileCount = _profiles.GetManifest().Profiles.Count;

        var res = await client.PostAsync("/profiles/import/inspect", Json(bundle));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Inspected", body.GetProperty("name").GetString());
        Assert.Equal(new[] { "com.alpha.app", App }, body.GetProperty("appIds").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("json", body.GetProperty("format").GetString());
        Assert.Equal(profileCount, _profiles.GetManifest().Profiles.Count);
    }

    [Fact]
    public async Task Inspect_of_a_legacy_bare_json_reports_no_app_ids()
    {
        var client = AuthedClient();
        var settingsJson = JsonDocument.Parse(await ExportAsync(client, _profiles.ActiveProfileId)).RootElement.GetProperty("settings").GetRawText();

        var res = await client.PostAsync("/profiles/import/inspect", Json($$"""{"name":"Legacy","settings":{{settingsJson}}}"""));

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Legacy", body.GetProperty("name").GetString());
        Assert.Empty(body.GetProperty("appIds").EnumerateArray());
        Assert.Equal("json", body.GetProperty("format").GetString());
    }

    [Fact]
    public async Task Inspect_of_a_legacy_zip_reports_format_archive()
    {
        var client = AuthedClient();
        var export = await ExportAsync(client, _profiles.ActiveProfileId);
        var zip = LegacyZip(export.Replace("\"Default\"", "\"Zipped\""), App, "save", """{"revision":1,"updatedAt":"t","data":1}""");

        var res = await client.PostAsync("/profiles/import/inspect", Bytes(zip));

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("archive", body.GetProperty("format").GetString());
        Assert.Equal("Zipped", body.GetProperty("name").GetString());
        Assert.Equal(new[] { App }, body.GetProperty("appIds").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Inspect_of_unreadable_bytes_is_400()
    {
        var res = await AuthedClient().PostAsync("/profiles/import/inspect", Json("{ nope"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Export_with_an_id_that_is_not_a_valid_profile_id_is_404()
    {
        var res = await AuthedClient().GetAsync("/profiles/a.b/export");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Import_drops_null_documents_from_a_file()
    {
        var client = AuthedClient();
        var settingsJson = JsonDocument.Parse(await ExportAsync(client, _profiles.ActiveProfileId)).RootElement.GetProperty("settings").GetRawText();
        var body = "{\"name\":\"Nulls\",\"settings\":" + settingsJson + ",\"appData\":{\"com.test.app\":{\"n\":null,\"ok\":1}}}";

        var res = await client.PostAsync("/profiles/import", Json(body));

        var newId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("profile").GetProperty("id").GetString()!;
        Assert.Equal(new[] { "ok" }, _appData.ReadProfile(newId)[App].Keys);
    }
}
