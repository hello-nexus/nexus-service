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
