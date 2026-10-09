using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Deck;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Routes;
using Nexus.Service.Tests.Integration;
using Xunit;
using static Nexus.Service.Tests.StreamDeck.DeckTestHelpers;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>Spy executor used by the route tests to assert test-press dispatched without constructing the real provider graph.</summary>
internal sealed class SpyDeckActionExecutor : IDeckActionExecutor
{
    public (DeckAction? Action, string Serial, int KeyIndex, string LatchKey)? LastCall;

    public Task ExecuteAsync(DeckAction? action, string serial, int keyIndex, string latchKey, CancellationToken ct)
    {
        LastCall = (action, serial, keyIndex, latchKey);
        return Task.CompletedTask;
    }

    public bool IsToggleOn(DeckToggleState? state, string latchKey) => false;

    public void OpenApp() { }
}

/// <summary>
/// Every /streamdeck/* route is LocalhostOnly, which the auth middleware
/// gates on HttpContext.Connection.RemoteIpAddress - unset for a plain
/// WebApplicationFactory HttpClient request, so it 404s before auth even
/// runs (see AuthMiddlewareIntegrationTests.cs's doc comment). A raw
/// TestServer.SendAsync with a manually assigned request body reliably 400s
/// with an empty response for this app's minimal-API POST/PUT routes for
/// reasons that didn't resolve under investigation; this IStartupFilter
/// stamps loopback onto every request instead, which lets the rest of the
/// test use the plain HttpClient body-sending path DeckActionRoutesTests.cs
/// already proves works.
/// </summary>
internal sealed class LoopbackConnectionFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (ctx, nextMw) =>
        {
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
            await nextMw(ctx);
        });
        next(app);
    };
}

public sealed class StreamDeckRoutesTests : IClassFixture<StreamDeckRouteHostFactory>
{
    private readonly StreamDeckRouteHostFactory _host;

    public StreamDeckRoutesTests(StreamDeckRouteHostFactory host)
    {
        _host = host;
        // One host for the class. Three pieces of state outlive a test and have
        // to go back to first-run: the settings store, the spy's last call, and
        // the worker's single simulated-deck slot (SimulatedKey), which several
        // tests attach to and others assert is absent.
        _host.ResetSettings();
        Executor.LastCall = null;
        var worker = _host.Services.GetRequiredService<StreamDeckConnectionWorker>();
        worker.ClearSimulatedModel();
        worker.ResetPerDeckStateForTests();
    }

    private SpyDeckActionExecutor Executor => _host.Executor;

    private (SharedHost factory, HttpClient client) Boot()
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _host.Services.GetRequiredService<TokenService>().Token);
        return (new SharedHost(_host.Services), client);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task GetDecks_ListsAPersistedDeckWithNoLiveSurfaceAsDisconnected()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            var mini = StreamDeckModels.ByProductId(0x0063)!;
            store.Update(s => s.StreamDeck.Decks["OFFLINE-SERIAL"] = new PhysicalDeckSettings
            {
                Name = "Desk Deck",
                ProductId = mini.ProductId,
            });

            var res = await client.GetAsync("/streamdeck/decks");
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var decks = doc.RootElement.GetProperty("decks");
            var entry = decks.EnumerateArray().Single(d => d.GetProperty("serial").GetString() == "OFFLINE-SERIAL");

            Assert.False(entry.GetProperty("connected").GetBoolean());
            Assert.Equal("Desk Deck", entry.GetProperty("name").GetString());
            Assert.Equal(mini.Name, entry.GetProperty("model").GetString());
            Assert.Equal(mini.Rows, entry.GetProperty("rows").GetInt32());
            Assert.Equal(mini.Columns, entry.GetProperty("cols").GetInt32());
            Assert.Equal(mini.KeyCount, entry.GetProperty("keyCount").GetInt32());
            Assert.Equal(mini.Transform, entry.GetProperty("transform").GetString());
            Assert.Equal(0, entry.GetProperty("orientation").GetInt32());
            Assert.Equal(0, entry.GetProperty("sleepAfterSeconds").GetInt32());
            Assert.True(entry.GetProperty("sleepWhenLocked").GetBoolean());
            Assert.True(!entry.TryGetProperty("warning", out var warningEl) || warningEl.ValueKind == JsonValueKind.Null);
            Assert.True(!entry.TryGetProperty("conflictAppId", out var conflictEl) || conflictEl.ValueKind == JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task GetDecks_ReflectsASimulatedModelSetOnTheWorker()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();
            var xl = StreamDeckModels.ByProductId(0x006c)!; // not the DI-default Mini - proves the model is parametric
            Assert.True(worker.SetSimulatedModel(xl.ProductId));

            var res = await client.GetAsync("/streamdeck/decks");
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var decks = doc.RootElement.GetProperty("decks");
            var entry = decks.EnumerateArray().Single(d => d.GetProperty("model").GetString() == "XL");

            Assert.True(entry.GetProperty("connected").GetBoolean());
            Assert.Equal(xl.Rows, entry.GetProperty("rows").GetInt32());
            Assert.Equal(xl.Columns, entry.GetProperty("cols").GetInt32());
            Assert.Equal(xl.KeyCount, entry.GetProperty("keyCount").GetInt32());
            Assert.Equal(xl.KeyPixelSize, entry.GetProperty("keyPixels").GetInt32());
            Assert.Equal("jpeg", entry.GetProperty("format").GetString());
            Assert.Equal(xl.Transform, entry.GetProperty("transform").GetString());
        }
    }

    /// <summary>The dev-tools model picker's dropdown is populated from this route; it must list every catalog model so any deck size can be simulated.</summary>
    [Fact]
    public async Task GetDevModels_ListsEveryCatalogModel()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/streamdeck/dev/models");
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var models = doc.RootElement.GetProperty("models");
            Assert.Equal(StreamDeckModels.All.Count, models.GetArrayLength());

            var mini = StreamDeckModels.ByProductId(0x0063)!;
            var entry = models.EnumerateArray().Single(m => m.GetProperty("productId").GetInt32() == mini.ProductId);
            Assert.Equal(mini.Name, entry.GetProperty("name").GetString());
            Assert.Equal(mini.Rows, entry.GetProperty("rows").GetInt32());
            Assert.Equal(mini.Columns, entry.GetProperty("cols").GetInt32());
            Assert.Equal(mini.KeyCount, entry.GetProperty("keyCount").GetInt32());
        }
    }

    [Fact]
    public async Task PostSimulate_KnownModel_ConnectsADeck_ThenDeleteClearsIt()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var mini = StreamDeckModels.ByProductId(0x0063)!;
            var post = await client.PostAsync("/streamdeck/dev/simulate", Json($"{{\"productId\":{mini.ProductId}}}"));
            Assert.True(post.IsSuccessStatusCode);
            using (var doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
            {
                Assert.True(doc.RootElement.GetProperty("connected").GetBoolean());
                Assert.Equal(mini.Name, doc.RootElement.GetProperty("model").GetString());
            }

            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();
            Assert.True(worker.Surfaces.ContainsKey(StreamDeckConnectionWorker.SimulatedKey));

            var del = await client.DeleteAsync("/streamdeck/dev/simulate");
            Assert.True(del.IsSuccessStatusCode);
            Assert.False(worker.Surfaces.ContainsKey(StreamDeckConnectionWorker.SimulatedKey));
        }
    }

    [Fact]
    public async Task PostSimulate_UnknownModel_Returns400()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/streamdeck/dev/simulate", Json("{\"productId\":57005}")); // 0xDEAD
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task PostSimPress_PokesTheSimulatedSurface_ButNoOpsWithoutOne()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var noSim = await client.PostAsync("/streamdeck/dev/sim-press", Json("{\"keyIndex\":0,\"pressed\":true}"));
            Assert.True(noSim.IsSuccessStatusCode);
            using (var doc = JsonDocument.Parse(await noSim.Content.ReadAsStringAsync()))
            {
                Assert.True(doc.RootElement.GetProperty("error").GetBoolean());
            }

            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();
            var mini = StreamDeckModels.ByProductId(0x0063)!;
            Assert.True(worker.SetSimulatedModel(mini.ProductId));

            var pressed = await client.PostAsync("/streamdeck/dev/sim-press", Json("{\"keyIndex\":0,\"pressed\":true}"));
            Assert.True(pressed.IsSuccessStatusCode);
            using (var doc = JsonDocument.Parse(await pressed.Content.ReadAsStringAsync()))
            {
                Assert.False(doc.RootElement.GetProperty("error").GetBoolean());
            }
        }
    }

    /// <summary>A simulated deck is ephemeral: disconnecting must purge its persisted `sim-` record so it does not linger as an offline deck (which kept the web sim toggle stuck "on").</summary>
    [Fact]
    public async Task DeleteSimulate_PurgesThePersistedSimRecord()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var mini = StreamDeckModels.ByProductId(0x0063)!;
            Assert.True((await client.PostAsync("/streamdeck/dev/simulate", Json($"{{\"productId\":{mini.ProductId}}}"))).IsSuccessStatusCode);
            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();
            var serial = worker.Surfaces[StreamDeckConnectionWorker.SimulatedKey].Serial;

            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.StreamDeck.Decks[serial] = new PhysicalDeckSettings { ProductId = mini.ProductId, Name = "Sim" });
            Assert.True(store.Load().StreamDeck.Decks.ContainsKey(serial));

            Assert.True((await client.DeleteAsync("/streamdeck/dev/simulate")).IsSuccessStatusCode);

            Assert.False(store.Load().StreamDeck.Decks.ContainsKey(serial));
            var res = await client.GetAsync("/streamdeck/decks");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.DoesNotContain(doc.RootElement.GetProperty("decks").EnumerateArray(),
                d => d.GetProperty("serial").GetString() == serial);
        }
    }

    [Fact]
    public async Task GetDecks_UnknownPersistedProductId_IsSkippedRatherThanBroken()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.StreamDeck.Decks["CORRUPT-SERIAL"] = new PhysicalDeckSettings { ProductId = 0xDEAD });

            var res = await client.GetAsync("/streamdeck/decks");
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var decks = doc.RootElement.GetProperty("decks");
            Assert.DoesNotContain(decks.EnumerateArray(), d => d.GetProperty("serial").GetString() == "CORRUPT-SERIAL");
        }
    }

    /// <summary>The web deck page opens at the worker's actual tracked view (not always page 0/root), so a connected deck's summary must carry it.</summary>
    [Fact]
    public async Task GetDecks_ConnectedDeck_ReportsTheWorkersTrackedCurrentPageAndFolderPath()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();
            var mini = StreamDeckModels.ByProductId(0x0063)!;
            Assert.True(worker.SetSimulatedModel(mini.ProductId));
            var serial = worker.Surfaces[StreamDeckConnectionWorker.SimulatedKey].Serial;

            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.StreamDeck.Decks[serial] = new PhysicalDeckSettings
            {
                ProductId = mini.ProductId,
                LegacyDeck = new DeckConfig
                {
                    Pages =
                    {
                        new DeckPage(),
                        new DeckPage { Slots = { new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot() } } } } },
                    },
                },
            });
            store.Update(s => ActivateLegacyDeck(s, serial));
            Assert.True(worker.SetNav(serial, 1, new[] { 0 }));

            var res = await client.GetAsync("/streamdeck/decks");
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var entry = doc.RootElement.GetProperty("decks").EnumerateArray().Single(d => d.GetProperty("serial").GetString() == serial);

            Assert.Equal(1, entry.GetProperty("currentPage").GetInt32());
            var folderPath = entry.GetProperty("folderPath").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            Assert.Equal(new[] { 0 }, folderPath);
        }
    }

    /// <summary>A persisted deck with no live surface has no tracked worker state, so it reports the same default (root, page 0) OnSurfaceConnected would seed on reconnect.</summary>
    [Fact]
    public async Task GetDecks_DisconnectedDeck_ReportsDefaultPageAndEmptyFolderPath()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            var mini = StreamDeckModels.ByProductId(0x0063)!;
            store.Update(s => s.StreamDeck.Decks["OFFLINE-SERIAL"] = new PhysicalDeckSettings { ProductId = mini.ProductId });

            var res = await client.GetAsync("/streamdeck/decks");
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var entry = doc.RootElement.GetProperty("decks").EnumerateArray().Single(d => d.GetProperty("serial").GetString() == "OFFLINE-SERIAL");

            Assert.Equal(0, entry.GetProperty("currentPage").GetInt32());
            Assert.Empty(entry.GetProperty("folderPath").EnumerateArray());
        }
    }

    [Fact]
    public async Task TestPress_DispatchesTheResolvedSlotActionToTheExecutor()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings
            {
                LegacyDeck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "power", PowerAction = "lock" } } } } } },
            });
            store.Update(s => ActivateLegacyDeck(s, "SERIAL-1"));

            var res = await client.PostAsync("/streamdeck/decks/SERIAL-1/test-press/0", Json("{}"));
            Assert.True(res.IsSuccessStatusCode);

            // The dispatch is fire-and-forget (Task.Run), same as a real key
            // press - await the test seam before asserting the spy was called.
            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();
            if (worker.LastDispatchTask is not null)
            {
                await worker.LastDispatchTask;
            }

            Assert.NotNull(Executor.LastCall);
            Assert.Equal("SERIAL-1", Executor.LastCall!.Value.Serial);
            Assert.Equal("lock", Executor.LastCall.Value.Action!.PowerAction);
        }
    }

    [Fact]
    public async Task TestPress_UnknownDeck_Fails()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/streamdeck/decks/NEVER-SEEN/test-press/0", Json("{}"));
            var text = await res.Content.ReadAsStringAsync();
            Assert.Contains("\"error\":true", text);
            Assert.Null(Executor.LastCall);
        }
    }

    [Fact]
    public async Task TestPress_OnPageAction_ChangesCurrentPageLikeARealPress()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings
            {
                LegacyDeck = new DeckConfig
                {
                    Pages =
                    {
                        new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "page", Op = "next" } } } },
                        new DeckPage(),
                    },
                },
            });
            store.Update(s => ActivateLegacyDeck(s, "SERIAL-1"));
            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();
            Assert.Equal(0, worker.GetCurrentPage("SERIAL-1"));

            var res = await client.PostAsync("/streamdeck/decks/SERIAL-1/test-press/0", Json("{}"));
            Assert.True(res.IsSuccessStatusCode);

            // A "page" slot is intercepted before the executor, exactly like
            // a real key press - the executor must never see it.
            Assert.Equal(1, worker.GetCurrentPage("SERIAL-1"));
            Assert.Null(Executor.LastCall);
        }
    }

    [Fact]
    public async Task TestPress_OnFolderSlot_PushesFolderNavLikeARealPress()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings
            {
                LegacyDeck = new DeckConfig
                {
                    Pages = { new DeckPage { Slots = { new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot() } } } } } },
                },
            });
            store.Update(s => ActivateLegacyDeck(s, "SERIAL-1"));
            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();

            var res = await client.PostAsync("/streamdeck/decks/SERIAL-1/test-press/0", Json("{}"));
            Assert.True(res.IsSuccessStatusCode);

            Assert.Equal(new[] { 0 }, worker.GetFolderPath("SERIAL-1"));
            Assert.Null(Executor.LastCall);
        }
    }

    [Fact]
    public async Task GetDecks_FromLan_404s()
    {
        // Deliberately does NOT use Boot()'s loopback-forcing filter - this
        // is the one test that needs a real non-loopback RemoteIpAddress, so
        // it uses the plain factory + Server.SendAsync (a bodyless GET, which
        // AuthMiddlewareIntegrationTests.cs already proves works with SendAsync).
        var factory = new NexusAppFactory();
        var token = factory.Services.GetRequiredService<TokenService>().Token;
        var ctx = await factory.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/streamdeck/decks";
            c.Request.Headers.Authorization = "Bearer " + token;
            c.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
        });
        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task GetDecks_OnLoopbackWithoutToken_401s()
    {
        var (factory, _) = Boot();
        using (factory)
        {
            var anon = _host.CreateClient();
            var res = await anon.GetAsync("/streamdeck/decks");
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
    }

    private async Task<JsonElement> Simulate(HttpClient client, int productId)
    {
        var post = await client.PostAsync("/streamdeck/dev/simulate", Json($"{{\"productId\":{productId}}}"));
        Assert.True(post.IsSuccessStatusCode);
        using var doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Simulated_Plus_SummaryCarriesTheDialAndScreenFields()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var summary = await Simulate(client, 0x0084);

            Assert.Equal(4, summary.GetProperty("encoders").GetInt32());
            Assert.Equal("below", summary.GetProperty("dialPlacement").GetString());
            var screen = summary.GetProperty("screen");
            Assert.Equal(800, screen.GetProperty("width").GetInt32());
            Assert.Equal(100, screen.GetProperty("height").GetInt32());
            Assert.Equal("touchStrip", screen.GetProperty("kind").GetString());
            Assert.Equal(0, summary.GetProperty("touchKeys").GetInt32());
            Assert.Equal(120, summary.GetProperty("keyWidth").GetInt32());
            Assert.Equal(120, summary.GetProperty("keyHeight").GetInt32());
            Assert.Equal("none", summary.GetProperty("transform").GetString());
            Assert.False(summary.TryGetProperty("infoScreen", out _));
        }
    }

    [Theory]
    [InlineData(0x00c6, "rot90Ccw", "below", 6, 0, 0)]
    [InlineData(0x00aa, "none", "sides", 2, 24, 0)]
    [InlineData(0x2b18, "none", "above", 2, 4, 0)]
    [InlineData(0x009a, "flipBoth", null, 0, 0, 2)]
    public async Task Simulated_ExpandedModels_SummaryMatchesTheCapabilityRow(
        int productId, string transform, string? placement, int encoders, int ringLeds, int touchKeys)
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var summary = await Simulate(client, productId);

            Assert.Equal(transform, summary.GetProperty("transform").GetString());
            Assert.Equal(encoders, summary.GetProperty("encoders").GetInt32());
            Assert.Equal(ringLeds, summary.GetProperty("encoderRingLeds").GetInt32());
            Assert.Equal(touchKeys, summary.GetProperty("touchKeys").GetInt32());
            var placementEl = summary.GetProperty("dialPlacement");
            Assert.Equal(placement, placementEl.ValueKind == JsonValueKind.Null ? null : placementEl.GetString());
        }
    }

    [Fact]
    public async Task Simulated_Studio_ReportsNoScreenAsNull_AndWideKeys()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var summary = await Simulate(client, 0x00aa);

            Assert.Equal(JsonValueKind.Null, summary.GetProperty("screen").ValueKind);
            Assert.Equal(144, summary.GetProperty("keyWidth").GetInt32());
            Assert.Equal(112, summary.GetProperty("keyHeight").GetInt32());
            Assert.Equal(112, summary.GetProperty("keyPixels").GetInt32());
        }
    }

    [Fact]
    public async Task ButtonOnlyDeck_SummaryHasZeroEncodersAndNullPlacement()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var summary = await Simulate(client, 0x0063);

            Assert.Equal(0, summary.GetProperty("encoders").GetInt32());
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("dialPlacement").ValueKind);
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("screen").ValueKind);
        }
    }

    [Fact]
    public async Task Neo_InfoScreenSettingDefaultsToClock_AndPostUpdatesIt()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var summary = await Simulate(client, 0x009a);
            var serial = summary.GetProperty("serial").GetString()!;
            Assert.Equal("clock", summary.GetProperty("infoScreen").GetString());
            Assert.Equal("infoScreen", summary.GetProperty("screen").GetProperty("kind").GetString());

            var set = await client.PostAsync($"/streamdeck/decks/{serial}", Json("{\"infoScreen\":\"page\"}"));
            Assert.True(set.IsSuccessStatusCode);
            var store = factory.Services.GetRequiredService<IConfigStore>();
            Assert.Equal("page", store.Load().StreamDeck.Decks[serial].InfoScreen);

            await client.PostAsync($"/streamdeck/decks/{serial}", Json("{\"infoScreen\":\"bogus\"}"));
            Assert.Equal("page", store.Load().StreamDeck.Decks[serial].InfoScreen);

            var list = await client.GetStringAsync("/streamdeck/decks");
            using var doc = JsonDocument.Parse(list);
            var entry = doc.RootElement.GetProperty("decks").EnumerateArray().Single(d => d.GetProperty("serial").GetString() == serial);
            Assert.Equal("page", entry.GetProperty("infoScreen").GetString());
        }
    }

    [Fact]
    public async Task DevModels_CarryEncodersScreenAndTouchKeys()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            using var doc = JsonDocument.Parse(await client.GetStringAsync("/streamdeck/dev/models"));
            var models = doc.RootElement.GetProperty("models").EnumerateArray().ToList();

            var plus = models.Single(m => m.GetProperty("productId").GetInt32() == 0x0084);
            Assert.Equal(4, plus.GetProperty("encoders").GetInt32());
            Assert.Equal("touchStrip", plus.GetProperty("screen").GetProperty("kind").GetString());
            var neo = models.Single(m => m.GetProperty("productId").GetInt32() == 0x009a);
            Assert.Equal(2, neo.GetProperty("touchKeys").GetInt32());
            var mini = models.Single(m => m.GetProperty("productId").GetInt32() == 0x0063);
            Assert.Equal(JsonValueKind.Null, mini.GetProperty("screen").ValueKind);
        }
    }

    [Fact]
    public async Task SimInput_DrivesTheHardwareInputPath_ForDialsAndTouch()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var summary = await Simulate(client, 0x0084);
            var serial = summary.GetProperty("serial").GetString()!;
            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s =>
            {
                var preset = s.StreamDeck.Presets.First(p => p.Id == s.StreamDeck.Instances[DeckInstanceResolver.PhysicalInstanceId(serial)].ActivePresetId);
                preset.Deck.Pages.Add(new DeckPage());
                preset.Deck.Pages.Add(new DeckPage());
            });

            var swipe = await client.PostAsync("/streamdeck/dev/sim-input",
                Json($"{{\"serial\":\"{serial}\",\"kind\":\"swipe\",\"x\":600,\"y\":50,\"x2\":500,\"y2\":50}}"));
            Assert.True(swipe.IsSuccessStatusCode);
            Assert.Equal(1, worker.GetCurrentPage(serial));

            var longTouch = await client.PostAsync("/streamdeck/dev/sim-input",
                Json($"{{\"serial\":\"{serial}\",\"kind\":\"longTouch\",\"x\":50,\"y\":50}}"));
            Assert.True(longTouch.IsSuccessStatusCode);
            using var pending = JsonDocument.Parse(await client.GetStringAsync("/streamdeck/pending-edit"));
            var edit = pending.RootElement.GetProperty("edit");
            Assert.Equal(0, edit.GetProperty("dialIndex").GetInt32());
            Assert.Equal(serial, edit.GetProperty("serial").GetString());
            Assert.Equal(1, edit.GetProperty("page").GetInt32());

            foreach (var body in new[]
            {
                $"{{\"serial\":\"{serial}\",\"kind\":\"rotate\",\"index\":2,\"ticks\":3}}",
                $"{{\"serial\":\"{serial}\",\"kind\":\"dialDown\",\"index\":1}}",
                $"{{\"serial\":\"{serial}\",\"kind\":\"dialUp\",\"index\":1}}",
                $"{{\"serial\":\"{serial}\",\"kind\":\"tap\",\"x\":300,\"y\":40}}",
            })
            {
                var res = await client.PostAsync("/streamdeck/dev/sim-input", Json(body));
                Assert.True(res.IsSuccessStatusCode);
                Assert.Contains("\"error\":false", (await res.Content.ReadAsStringAsync()).Replace(" ", ""));
            }
        }
    }

    [Fact]
    public async Task SimInput_TouchKey_PagesTheNeo()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var summary = await Simulate(client, 0x009a);
            var serial = summary.GetProperty("serial").GetString()!;
            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();
            factory.Services.GetRequiredService<IConfigStore>().Update(s =>
            {
                var preset = s.StreamDeck.Presets.First(p => p.Id == s.StreamDeck.Instances[DeckInstanceResolver.PhysicalInstanceId(serial)].ActivePresetId);
                preset.Deck.Pages.Add(new DeckPage());
            });

            foreach (var pressed in new[] { "true", "false" })
            {
                var res = await client.PostAsync("/streamdeck/dev/sim-input",
                    Json($"{{\"serial\":\"{serial}\",\"kind\":\"touchKey\",\"index\":1,\"pressed\":{pressed}}}"));
                Assert.Contains("\"error\":false", (await res.Content.ReadAsStringAsync()).Replace(" ", ""));
            }
            Assert.Equal(1, worker.GetCurrentPage(serial));

            var bad = await client.PostAsync("/streamdeck/dev/sim-input",
                Json($"{{\"serial\":\"{serial}\",\"kind\":\"touchKey\",\"index\":2,\"pressed\":true}}"));
            Assert.Contains("\"error\":true", (await bad.Content.ReadAsStringAsync()).Replace(" ", ""));
        }
    }

    [Theory]
    [InlineData(0x0063, "{\"kind\":\"rotate\",\"index\":0,\"ticks\":1}")]
    [InlineData(0x0084, "{\"kind\":\"rotate\",\"index\":4,\"ticks\":1}")]
    [InlineData(0x0084, "{\"kind\":\"rotate\",\"index\":0}")]
    [InlineData(0x0084, "{\"kind\":\"tap\",\"x\":10}")]
    [InlineData(0x0084, "{\"kind\":\"swipe\",\"x\":10,\"y\":10}")]
    [InlineData(0x0084, "{\"kind\":\"nonsense\"}")]
    public async Task SimInput_RejectsInputTheModelCannotProduce(int productId, string partialBody)
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var summary = await Simulate(client, productId);
            var serial = summary.GetProperty("serial").GetString()!;
            var body = "{\"serial\":\"" + serial + "\"," + partialBody[1..];

            var res = await client.PostAsync("/streamdeck/dev/sim-input", Json(body));

            Assert.Contains("\"error\":true", (await res.Content.ReadAsStringAsync()).Replace(" ", ""));
        }
    }

    [Fact]
    public async Task SimInput_WithoutASimulatedDeck_Fails()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/streamdeck/dev/sim-input", Json("{\"serial\":\"nope\",\"kind\":\"dialDown\",\"index\":0}"));

            Assert.Contains("\"error\":true", (await res.Content.ReadAsStringAsync()).Replace(" ", ""));
        }
    }

    [Fact]
    public void ResolveConflictAppId_NoWarning_ReturnsNull()
    {
        Assert.Null(StreamDeckRoutes.ResolveConflictAppId(null));
    }

    [Fact]
    public void ResolveConflictAppId_WithWarning_ReturnsTheElgatoCatalogId()
    {
        Assert.Equal(StreamDeckHandler.ElgatoConflictAppId, StreamDeckRoutes.ResolveConflictAppId("elgato-software-running"));
    }
}
