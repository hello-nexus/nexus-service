using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// /profiles/create, /profiles/{id}/rename, and /profiles/import over the
/// real request pipeline: a name collision with a DIFFERENT profile must
/// reach the client as 409 with msg "profile_name_taken" (nexus-web maps
/// this exact string to a translated inline error), not a generic 400. Own
/// <see cref="NexusAppFactory"/> per test (not a shared IClassFixture) since
/// profile names are state within one factory's ProfileManager and would leak
/// across test methods sharing that factory otherwise.
/// </summary>
[Collection("NexusHost")]
public sealed class ProfileRoutesIntegrationTests : IDisposable
{
    private readonly NexusAppFactory _factory;

    public ProfileRoutesIntegrationTests()
    {
        _factory = new NexusAppFactory();
        // AppBootstrap.InitializeProfiles is skipped for the test host (Program.cs
        // gates it behind !testHost), so the Default profile is never seeded.
        _factory.Services.GetRequiredService<ProfileManager>().Initialize();
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        var token = _factory.Services.GetRequiredService<TokenService>().Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<string> DefaultProfileIdAsync(HttpClient client)
    {
        var res = await client.GetAsync("/profiles");
        var list = await res.Content.ReadFromJsonAsync<JsonElement>();
        return list.GetProperty("profiles").EnumerateArray()
            .First(p => p.GetProperty("name").GetString() == "Default")
            .GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Create_with_a_colliding_name_returns_409_profile_name_taken()
    {
        var client = AuthedClient();
        var first = await client.PostAsJsonAsync("/profiles/create", new { name = "Gaming" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var res = await client.PostAsJsonAsync("/profiles/create", new { name = "  GAMING  " });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("profile_name_taken", body.GetProperty("msg").GetString());
    }

    [Fact]
    public async Task Rename_with_a_colliding_name_returns_409_profile_name_taken()
    {
        var client = AuthedClient();
        await client.PostAsJsonAsync("/profiles/create", new { name = "Gaming" });
        var defaultId = await DefaultProfileIdAsync(client);

        var res = await client.PostAsJsonAsync($"/profiles/{defaultId}/rename", new { name = "gaming" });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("profile_name_taken", body.GetProperty("msg").GetString());
    }

    [Fact]
    public async Task Rename_to_its_own_current_name_succeeds()
    {
        var client = AuthedClient();
        var defaultId = await DefaultProfileIdAsync(client);

        var res = await client.PostAsJsonAsync($"/profiles/{defaultId}/rename", new { name = "DEFAULT" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DEFAULT", body.GetProperty("profile").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Import_with_a_colliding_name_returns_409_profile_name_taken()
    {
        var client = AuthedClient();
        await client.PostAsJsonAsync("/profiles/create", new { name = "Gaming" });
        var defaultId = await DefaultProfileIdAsync(client);

        var exportRes = await client.GetAsync($"/profiles/{defaultId}/export");
        using var exportDoc = JsonDocument.Parse(await exportRes.Content.ReadAsStringAsync());
        var settingsJson = exportDoc.RootElement.GetProperty("settings").GetRawText();
        var importBody = $$"""{"name":"Gaming","settings":{{settingsJson}}}""";

        var res = await client.PostAsync("/profiles/import",
            new StringContent(importBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("profile_name_taken", body.GetProperty("msg").GetString());
    }

    [Fact]
    public async Task Import_with_a_distinct_name_succeeds()
    {
        var client = AuthedClient();
        var defaultId = await DefaultProfileIdAsync(client);

        var exportRes = await client.GetAsync($"/profiles/{defaultId}/export");
        using var exportDoc = JsonDocument.Parse(await exportRes.Content.ReadAsStringAsync());
        var settingsJson = exportDoc.RootElement.GetProperty("settings").GetRawText();
        var importBody = $$"""{"name":"Imported Copy","settings":{{settingsJson}}}""";

        var res = await client.PostAsync("/profiles/import",
            new StringContent(importBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Imported Copy", body.GetProperty("profile").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Preferences_diagnostics_patch_persists_and_round_trips_through_GET()
    {
        var client = AuthedClient();

        var patch = new
        {
            diagnostics = new
            {
                thresholds = new { cpuC = 80, gpuC = 82, storageC = 65, ramC = 55 },
                warningLingerMinutes = 15,
                notifications = new
                {
                    enabled = true,
                    highTemp = true,
                    storageHealth = false,
                    cooling = true,
                    memoryTest = false,
                    systemDevices = false,
                    gpuThrottle = true,
                    cooldownMinutes = 30,
                },
                components = new { cpu = true, gpu = false, storage = true, ram = true, cooling = false, system = true },
            },
        };

        var postRes = await client.PostAsJsonAsync("/preferences", patch);
        Assert.Equal(HttpStatusCode.OK, postRes.StatusCode);

        var getRes = await client.GetAsync("/preferences");
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        var body = await getRes.Content.ReadFromJsonAsync<JsonElement>();
        var diagnostics = body.GetProperty("diagnostics");

        var thresholds = diagnostics.GetProperty("thresholds");
        Assert.Equal(80, thresholds.GetProperty("cpuC").GetDouble());
        Assert.Equal(82, thresholds.GetProperty("gpuC").GetDouble());
        Assert.Equal(65, thresholds.GetProperty("storageC").GetDouble());
        Assert.Equal(55, thresholds.GetProperty("ramC").GetDouble());

        Assert.Equal(15, diagnostics.GetProperty("warningLingerMinutes").GetInt32());

        var notifications = diagnostics.GetProperty("notifications");
        Assert.True(notifications.GetProperty("enabled").GetBoolean());
        Assert.True(notifications.GetProperty("highTemp").GetBoolean());
        Assert.False(notifications.GetProperty("storageHealth").GetBoolean());
        Assert.True(notifications.GetProperty("cooling").GetBoolean());
        Assert.False(notifications.GetProperty("memoryTest").GetBoolean());
        Assert.False(notifications.GetProperty("systemDevices").GetBoolean());
        Assert.True(notifications.GetProperty("gpuThrottle").GetBoolean());
        Assert.Equal(30, notifications.GetProperty("cooldownMinutes").GetInt32());

        var components = diagnostics.GetProperty("components");
        Assert.True(components.GetProperty("cpu").GetBoolean());
        Assert.False(components.GetProperty("gpu").GetBoolean());
        Assert.True(components.GetProperty("storage").GetBoolean());
        Assert.True(components.GetProperty("ram").GetBoolean());
        Assert.False(components.GetProperty("cooling").GetBoolean());
        Assert.True(components.GetProperty("system").GetBoolean());
    }

    [Fact]
    public async Task Preferences_GET_defaults_diagnostics_to_the_documented_defaults()
    {
        var client = AuthedClient();

        var res = await client.GetAsync("/preferences");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var diagnostics = body.GetProperty("diagnostics");

        var thresholds = diagnostics.GetProperty("thresholds");
        Assert.Equal(90, thresholds.GetProperty("cpuC").GetDouble());
        Assert.Equal(85, thresholds.GetProperty("gpuC").GetDouble());
        Assert.Equal(70, thresholds.GetProperty("storageC").GetDouble());
        Assert.Equal(60, thresholds.GetProperty("ramC").GetDouble());
        Assert.Equal(0, diagnostics.GetProperty("warningLingerMinutes").GetInt32());

        // Master switch off by default; every category on, so enabling
        // notifications alerts for all of them without extra setup.
        var notifications = diagnostics.GetProperty("notifications");
        Assert.False(notifications.GetProperty("enabled").GetBoolean());
        Assert.True(notifications.GetProperty("highTemp").GetBoolean());
        Assert.True(notifications.GetProperty("storageHealth").GetBoolean());
        Assert.True(notifications.GetProperty("cooling").GetBoolean());
        Assert.True(notifications.GetProperty("memoryTest").GetBoolean());
        Assert.True(notifications.GetProperty("systemDevices").GetBoolean());
        Assert.True(notifications.GetProperty("gpuThrottle").GetBoolean());
        Assert.Equal(60, notifications.GetProperty("cooldownMinutes").GetInt32());

        var components = diagnostics.GetProperty("components");
        Assert.True(components.GetProperty("cpu").GetBoolean());
        Assert.True(components.GetProperty("gpu").GetBoolean());
        Assert.True(components.GetProperty("storage").GetBoolean());
        Assert.True(components.GetProperty("ram").GetBoolean());
        Assert.True(components.GetProperty("cooling").GetBoolean());
        Assert.True(components.GetProperty("system").GetBoolean());
    }

    [Fact]
    public async Task Import_with_replace_true_overwrites_the_colliding_profile_in_place()
    {
        var client = AuthedClient();
        var createRes = await client.PostAsJsonAsync("/profiles/create", new { name = "Gaming" });
        var createBody = await createRes.Content.ReadFromJsonAsync<JsonElement>();
        var gamingId = createBody.GetProperty("profile").GetProperty("id").GetString();
        var defaultId = await DefaultProfileIdAsync(client);

        var countBefore = (await (await client.GetAsync("/profiles")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("profiles").GetArrayLength();

        var exportRes = await client.GetAsync($"/profiles/{defaultId}/export");
        using var exportDoc = JsonDocument.Parse(await exportRes.Content.ReadAsStringAsync());
        var settingsJson = exportDoc.RootElement.GetProperty("settings").GetRawText();
        var importBody = $$"""{"name":"gaming","settings":{{settingsJson}}}""";

        var res = await client.PostAsync("/profiles/import?replace=true",
            new StringContent(importBody, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(gamingId, body.GetProperty("profile").GetProperty("id").GetString());
        Assert.Equal("gaming", body.GetProperty("profile").GetProperty("name").GetString());

        var countAfter = (await (await client.GetAsync("/profiles")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("profiles").GetArrayLength();
        Assert.Equal(countBefore, countAfter);
    }

    [Fact]
    public async Task Sharing_Counts_ReflectActiveProfilesLightingAndDevicePresets()
    {
        var client = AuthedClient();
        var store = _factory.Services.GetRequiredService<IConfigStore>();
        store.Update(s =>
        {
            s.Lighting.LayoutPresets = new List<LayoutPreset>
            {
                new() { Id = "l1", Name = "One" },
                new() { Id = "l2", Name = "Two" },
            };
            s.StreamDeck.Decks["SN-1"] = new PhysicalDeckSettings
            {
                Presets = new List<DeckPreset> { new() { Id = "d1", Name = "Deck One" } },
            };
            s.StreamDeck.Decks["SN-2"] = new PhysicalDeckSettings
            {
                Presets = new List<DeckPreset>
                {
                    new() { Id = "d2", Name = "Deck Two" },
                    new() { Id = "d3", Name = "Deck Three" },
                },
            };
        });

        var res = await client.GetAsync("/profiles/sharing");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var counts = body.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("lighting").GetInt32());
        Assert.Equal(3, counts.GetProperty("device").GetInt32());
        Assert.Equal(0, counts.GetProperty("cooling").GetInt32());
        Assert.Equal(0, counts.GetProperty("theme").GetInt32());
        Assert.Equal(0, counts.GetProperty("dashboard").GetInt32());
    }
}
