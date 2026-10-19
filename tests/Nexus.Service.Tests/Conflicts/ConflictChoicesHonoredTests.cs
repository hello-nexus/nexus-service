using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Integration;

namespace Nexus.Service.Tests.Conflicts;

/// <summary>
/// The keep-running whitelist is edited one app at a time through
/// POST /conflicts/whitelist. A client holding an older copy of the list
/// (a window hydrated before another window kept an app, or a fresh window
/// before its first read) must not be able to erase those choices with a
/// whole-list write, because the startup sweep and launch window end every
/// detected app that is not on the list.
/// </summary>
public sealed class ConflictChoicesHonoredTests : IDisposable
{
    private readonly NexusAppFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.Services.GetRequiredService<TokenService>().Token);
        return client;
    }

    private static async Task<string[]> ExclusionsAsync(HttpClient client)
    {
        var prefs = await client.GetFromJsonAsync<JsonElement>("/preferences");
        return prefs.GetProperty("ui").GetProperty("conflictAutoKillExclusions")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    [Fact]
    public async Task Stale_whole_list_write_does_not_erase_a_kept_app()
    {
        var client = AuthedClient();
        _factory.Services.GetRequiredService<IConfigStore>().Update(s => s.Ui.ConflictAutoKillExclusions = new List<string>());

        var keep = await client.PostAsJsonAsync("/conflicts/whitelist", new { id = "armoury-crate", whitelisted = true });
        Assert.True(keep.IsSuccessStatusCode);
        Assert.Contains("armoury-crate", await ExclusionsAsync(client));

        var stale = await client.PostAsJsonAsync("/preferences", new { ui = new { conflictAutoKillExclusions = new[] { "icue" } } });
        Assert.True(stale.IsSuccessStatusCode);

        Assert.Contains("armoury-crate", await ExclusionsAsync(client));
    }

    [Fact]
    public async Task Master_switch_patch_leaves_the_whitelist_alone()
    {
        var client = AuthedClient();
        _factory.Services.GetRequiredService<IConfigStore>().Update(s => s.Ui.ConflictAutoKillExclusions = new List<string>());
        await client.PostAsJsonAsync("/conflicts/whitelist", new { id = "razer-synapse", whitelisted = true });

        await client.PostAsJsonAsync("/preferences", new { ui = new { autoKillConflictsAtStartup = true, endConflictsOnLaunch = true } });

        Assert.Equal(new[] { "razer-synapse" }, await ExclusionsAsync(client));
    }

    [Fact]
    public async Task Whitelist_route_removes_one_app_without_touching_the_rest()
    {
        var client = AuthedClient();
        _factory.Services.GetRequiredService<IConfigStore>().Update(s => s.Ui.ConflictAutoKillExclusions = new List<string>());
        await client.PostAsJsonAsync("/conflicts/whitelist", new { id = "armoury-crate", whitelisted = true });
        await client.PostAsJsonAsync("/conflicts/whitelist", new { id = "razer-synapse", whitelisted = true });

        await client.PostAsJsonAsync("/conflicts/whitelist", new { id = "armoury-crate", whitelisted = false });

        Assert.Equal(new[] { "razer-synapse" }, await ExclusionsAsync(client));
    }
}
