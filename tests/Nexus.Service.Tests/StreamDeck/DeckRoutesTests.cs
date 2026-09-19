using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Deck;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Integration;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// Host-wide deck presets/instances contract (src/Routes/DeckRoutes.cs, plan
/// deck-modes.md CONTRACT ADDENDUM). Every route here is LocalhostOnly.
/// </summary>
public sealed class DeckRoutesTests : IClassFixture<DeckRoutesHostFactory>
{
    private readonly DeckRoutesHostFactory _host;

    public DeckRoutesTests(DeckRoutesHostFactory host)
    {
        _host = host;
        _host.ResetSettings();
    }

    private (SharedHost factory, HttpClient client) Boot()
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _host.Services.GetRequiredService<TokenService>().Token);
        return (new SharedHost(_host.Services), client);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    // ───────────────────────── presets ─────────────────────────

    [Fact]
    public async Task GetPresets_Empty_ReturnsAnEmptyList()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/deck/presets");
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal(0, doc.RootElement.GetProperty("presets").GetArrayLength());
        }
    }

    [Fact]
    public async Task CreatePreset_WithDeck_PersistsAndReturnsTheFullShape()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/deck/presets", Json(
                """{"name":"Discord","cols":5,"rows":3,"deck":{"pages":[{"slots":[{"label":"Mute"}]}]}}"""));
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var preset = doc.RootElement.GetProperty("preset");
            Assert.Equal("Discord", preset.GetProperty("name").GetString());
            Assert.Equal(5, preset.GetProperty("cols").GetInt32());
            Assert.Equal(3, preset.GetProperty("rows").GetInt32());
            Assert.Equal(1, preset.GetProperty("pageCount").GetInt32());
            Assert.Equal("Mute", preset.GetProperty("deck").GetProperty("pages")[0].GetProperty("slots")[0].GetProperty("label").GetString());

            var id = preset.GetProperty("id").GetString();
            var store = factory.Services.GetRequiredService<IConfigStore>();
            Assert.Contains(store.Load().StreamDeck.Presets, p => p.Id == id);
        }
    }

    [Fact]
    public async Task CreatePreset_WithNoOptions_CreatesAnEmptyPreset()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/deck/presets", Json("""{"name":"Empty","cols":2,"rows":2}"""));
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal(0, doc.RootElement.GetProperty("preset").GetProperty("deck").GetProperty("pages").GetArrayLength());
        }
    }

    [Fact]
    public async Task CreatePreset_WithTemplateId_Returns501()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/deck/presets", Json("""{"name":"Template","cols":5,"rows":3,"templateId":"discord"}"""));
            Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
        }
    }

    [Fact]
    public async Task CreatePreset_WithMoreThanOneOption_Returns400()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/deck/presets", Json(
                """{"name":"Bad","cols":2,"rows":2,"deck":{"pages":[{"slots":[]}]},"templateId":"discord"}"""));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task CreatePreset_CopyOfPresetId_DeepCopiesTheSourceDeck()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var created = await client.PostAsync("/deck/presets", Json(
                """{"name":"Source","cols":2,"rows":2,"deck":{"pages":[{"slots":[{"label":"A"}]}]}}"""));
            using var createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var sourceId = createdDoc.RootElement.GetProperty("preset").GetProperty("id").GetString();

            var copied = await client.PostAsync("/deck/presets", Json($$"""{"name":"Copy","cols":2,"rows":2,"copyOfPresetId":"{{sourceId}}"}"""));
            Assert.True(copied.IsSuccessStatusCode);
            using var copyDoc = JsonDocument.Parse(await copied.Content.ReadAsStringAsync());
            Assert.Equal("A", copyDoc.RootElement.GetProperty("preset").GetProperty("deck").GetProperty("pages")[0].GetProperty("slots")[0].GetProperty("label").GetString());

            // Independent copy: editing the source afterward must not affect it.
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.StreamDeck.Presets.Find(p => p.Id == sourceId)!.Deck.Pages[0].Slots[0].Label = "Changed");
            var copyId = copyDoc.RootElement.GetProperty("preset").GetProperty("id").GetString();
            var copyPreset = store.Load().StreamDeck.Presets.Find(p => p.Id == copyId)!;
            Assert.Equal("A", copyPreset.Deck.Pages[0].Slots[0].Label);
        }
    }

    [Fact]
    public async Task CreatePreset_CopyOfUnknownPresetId_Returns404()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/deck/presets", Json("""{"name":"Copy","cols":2,"rows":2,"copyOfPresetId":"missing"}"""));
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }

    [Theory]
    [InlineData("Gaming")]
    [InlineData("gaming")]
    [InlineData("  GAMING  ")]
    public async Task CreatePreset_NameCollidesCaseInsensitively_Returns409(string collidingName)
    {
        var (factory, client) = Boot();
        using (factory)
        {
            await client.PostAsync("/deck/presets", Json("""{"name":"Gaming","cols":2,"rows":2}"""));
            var res = await client.PostAsync("/deck/presets", Json($$"""{"name":"{{collidingName}}","cols":2,"rows":2}"""));
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        }
    }

    [Fact]
    public async Task CreatePreset_EmptyName_Returns400()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/deck/presets", Json("""{"name":"   ","cols":2,"rows":2}"""));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task CreatePreset_AtCap_Returns400()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s =>
            {
                for (var i = 0; i < 50; i++)
                {
                    s.StreamDeck.Presets.Add(new DeckPreset { Id = $"p-{i}", Name = $"Preset {i}", Cols = 2, Rows = 2 });
                }
            });

            var res = await client.PostAsync("/deck/presets", Json("""{"name":"OneMore","cols":2,"rows":2}"""));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task GetPreset_UnknownId_Returns404()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/deck/presets/missing");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }

    [Fact]
    public async Task UpdatePreset_Name_RenamesOnly()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.StreamDeck.Presets.Add(new DeckPreset { Id = "p1", Name = "Old", Cols = 2, Rows = 2 }));

            var res = await client.PutAsync("/deck/presets/p1", Json("""{"name":"New"}"""));
            Assert.True(res.IsSuccessStatusCode);
            Assert.Equal("New", store.Load().StreamDeck.Presets.Find(p => p.Id == "p1")!.Name);
        }
    }

    [Fact]
    public async Task UpdatePreset_Deck_ReplacesTheConfigTree()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.StreamDeck.Presets.Add(new DeckPreset { Id = "p1", Name = "P", Cols = 2, Rows = 2 }));

            var res = await client.PutAsync("/deck/presets/p1", Json("""{"deck":{"pages":[{"slots":[{"label":"Edited"}]}]}}"""));
            Assert.True(res.IsSuccessStatusCode);
            Assert.Equal("Edited", store.Load().StreamDeck.Presets.Find(p => p.Id == "p1")!.Deck.Pages[0].Slots[0].Label);
        }
    }

    [Fact]
    public async Task UpdatePreset_UnknownId_Returns404()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PutAsync("/deck/presets/missing", Json("""{"name":"X"}"""));
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }

    [Fact]
    public async Task UpdatePreset_NameCollidesWithASibling_Returns409()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s =>
            {
                s.StreamDeck.Presets.Add(new DeckPreset { Id = "p1", Name = "One", Cols = 2, Rows = 2 });
                s.StreamDeck.Presets.Add(new DeckPreset { Id = "p2", Name = "Two", Cols = 2, Rows = 2 });
            });

            var res = await client.PutAsync("/deck/presets/p2", Json("""{"name":"One"}"""));
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        }
    }

    [Fact]
    public async Task DeletePreset_ReassignsInstancesToTheFirstRemainingPreset()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s =>
            {
                s.StreamDeck.Presets.Add(new DeckPreset { Id = "p1", Name = "One", Cols = 2, Rows = 2 });
                s.StreamDeck.Presets.Add(new DeckPreset { Id = "p2", Name = "Two", Cols = 2, Rows = 2 });
                s.StreamDeck.Instances["widget:w1"] = new DeckInstance { Mode = "fixed", ActivePresetId = "p1" };
            });

            var res = await client.DeleteAsync("/deck/presets/p1");
            Assert.True(res.IsSuccessStatusCode);

            var settings = store.Load().StreamDeck;
            Assert.DoesNotContain(settings.Presets, p => p.Id == "p1");
            Assert.Equal("p2", settings.Instances["widget:w1"].ActivePresetId);
        }
    }

    [Fact]
    public async Task DeletePreset_LastPresetInUse_Returns409()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s =>
            {
                s.StreamDeck.Presets.Add(new DeckPreset { Id = "p1", Name = "Only", Cols = 2, Rows = 2 });
                s.StreamDeck.Instances["widget:w1"] = new DeckInstance { Mode = "fixed", ActivePresetId = "p1" };
            });

            var res = await client.DeleteAsync("/deck/presets/p1");
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Contains(store.Load().StreamDeck.Presets, p => p.Id == "p1");
        }
    }

    [Fact]
    public async Task DeletePreset_NotInUse_RemovesItEvenIfItIsTheLastOne()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.StreamDeck.Presets.Add(new DeckPreset { Id = "p1", Name = "Only", Cols = 2, Rows = 2 }));

            var res = await client.DeleteAsync("/deck/presets/p1");
            Assert.True(res.IsSuccessStatusCode);
            Assert.Empty(store.Load().StreamDeck.Presets);
        }
    }

    // ───────────────────────── instances ─────────────────────────

    [Fact]
    public async Task GetInstances_ReturnsEveryPersistedRow()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.StreamDeck.Instances["widget:w1"] = new DeckInstance { Mode = "fixed", ActivePresetId = "p1" });

            var res = await client.GetAsync("/deck/instances");
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal("p1", doc.RootElement.GetProperty("instances").GetProperty("widget:w1").GetProperty("activePresetId").GetString());
        }
    }

    [Fact]
    public async Task GetInstance_UnknownWidgetInstance_LazilyCreatesAnEmptyPreset()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/deck/instances/widget:fresh");
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var presetId = doc.RootElement.GetProperty("instance").GetProperty("activePresetId").GetString();
            Assert.NotNull(presetId);

            var store = factory.Services.GetRequiredService<IConfigStore>();
            Assert.Contains(store.Load().StreamDeck.Presets, p => p.Id == presetId);
            Assert.Equal("fixed", store.Load().StreamDeck.Instances["widget:fresh"].Mode);
        }
    }

    [Fact]
    public async Task GetInstance_UnknownStreamdeckInstance_Returns404()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/deck/instances/streamdeck:NEVER-SEEN");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }

    [Fact]
    public async Task GetInstance_InvalidId_Returns400()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/deck/instances/not-a-valid-prefix");
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task PutInstance_ChangesModeAndActivePreset()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.StreamDeck.Presets.Add(new DeckPreset { Id = "p1", Name = "One", Cols = 2, Rows = 2 }));

            var res = await client.PutAsync("/deck/instances/widget:w1", Json("""{"mode":"recentApps","activePresetId":"p1"}"""));
            Assert.True(res.IsSuccessStatusCode);

            var instance = store.Load().StreamDeck.Instances["widget:w1"];
            Assert.Equal("recentApps", instance.Mode);
            Assert.Equal("p1", instance.ActivePresetId);
        }
    }

    [Fact]
    public async Task PutInstance_InvalidMode_Returns400()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PutAsync("/deck/instances/widget:w1", Json("""{"mode":"sideways"}"""));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task PutInstance_UnknownActivePresetId_Returns404()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PutAsync("/deck/instances/widget:w1", Json("""{"activePresetId":"missing"}"""));
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }
}
