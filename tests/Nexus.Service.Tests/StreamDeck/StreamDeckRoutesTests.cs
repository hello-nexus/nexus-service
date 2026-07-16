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

[Collection("NexusHost")]
public sealed class StreamDeckRoutesTests : IDisposable
{
    private readonly string _imageCacheDir = Path.Combine(Path.GetTempPath(), "nexus-streamdeck-route-cache-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SpyDeckActionExecutor _executor = new();

    public void Dispose()
    {
        try { Directory.Delete(_imageCacheDir, recursive: true); } catch { /* best effort */ }
    }

    private (WebApplicationFactory<Program> factory, HttpClient client) Boot()
    {
        var factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.AddTransient<IStartupFilter, LoopbackConnectionFilter>();
                s.RemoveAll<StreamDeckImageCache>();
                s.AddSingleton(new StreamDeckImageCache(_imageCacheDir));
                s.RemoveAll<IDeckActionExecutor>();
                s.AddSingleton<IDeckActionExecutor>(_executor);
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return (factory, client);
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
                Deck = new DeckConfig
                {
                    Pages =
                    {
                        new DeckPage(),
                        new DeckPage { Slots = { new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot() } } } } },
                    },
                },
            });
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
    public async Task GetConfig_WithNoPersistedDeck_ReturnsAnEmptyConfig()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.GetAsync("/streamdeck/decks/UNKNOWN-SERIAL/config");
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var pages = doc.RootElement.GetProperty("config").GetProperty("pages");
            Assert.Equal(1, pages.GetArrayLength());
            Assert.Empty(pages[0].GetProperty("slots").EnumerateArray());
        }
    }

    [Fact]
    public async Task PutConfig_ThenGetConfig_RoundTripsTheSlotTree()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var putBody = "{\"config\":{\"pages\":[{\"slots\":[{\"label\":\"Lock\",\"action\":{\"type\":\"power\",\"action\":\"lock\"}}]}]}}";
            var put = await client.PutAsync("/streamdeck/decks/SERIAL-1/config", Json(putBody));
            Assert.True(put.IsSuccessStatusCode);

            var get = await client.GetAsync("/streamdeck/decks/SERIAL-1/config");
            using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            var slots = doc.RootElement.GetProperty("config").GetProperty("pages")[0].GetProperty("slots");
            Assert.Equal(1, slots.GetArrayLength());
            Assert.Equal("Lock", slots[0].GetProperty("label").GetString());
            Assert.Equal("lock", slots[0].GetProperty("action").GetProperty("action").GetString());

            var store = factory.Services.GetRequiredService<IConfigStore>();
            Assert.Equal("Lock", store.Load().StreamDeck.Decks["SERIAL-1"].Deck.Pages[0].Slots[0].Label);
        }
    }

    [Fact]
    public async Task PutConfig_ThenGetConfig_RoundTripsTheTitleStyle()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var putBody = "{\"config\":{\"pages\":[{\"slots\":[{\"label\":\"Lock\",\"title\":{"
                + "\"show\":true,\"align\":\"top\",\"font\":\"mono\",\"size\":18,"
                + "\"bold\":true,\"italic\":false,\"underline\":true,\"color\":\"#ff0000\"},"
                + "\"action\":{\"type\":\"power\",\"action\":\"lock\"}}]}]}}";
            var put = await client.PutAsync("/streamdeck/decks/SERIAL-1/config", Json(putBody));
            Assert.True(put.IsSuccessStatusCode);

            var get = await client.GetAsync("/streamdeck/decks/SERIAL-1/config");
            using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            var slot = doc.RootElement.GetProperty("config").GetProperty("pages")[0].GetProperty("slots")[0];
            var title = slot.GetProperty("title");
            Assert.True(title.GetProperty("show").GetBoolean());
            Assert.Equal("top", title.GetProperty("align").GetString());
            Assert.Equal("mono", title.GetProperty("font").GetString());
            Assert.Equal(18, title.GetProperty("size").GetInt32());
            Assert.True(title.GetProperty("bold").GetBoolean());
            Assert.False(title.GetProperty("italic").GetBoolean());
            Assert.True(title.GetProperty("underline").GetBoolean());
            Assert.Equal("#ff0000", title.GetProperty("color").GetString());

            var store = factory.Services.GetRequiredService<IConfigStore>();
            var persisted = store.Load().StreamDeck.Decks["SERIAL-1"].Deck.Pages[0].Slots[0].Title;
            Assert.NotNull(persisted);
            Assert.Equal("top", persisted!.Align);
            Assert.Equal("#ff0000", persisted.Color);
        }
    }

    [Fact]
    public async Task PutConfig_SlotWithNoTitle_GetConfigOmitsTheTitleKey()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var putBody = "{\"config\":{\"pages\":[{\"slots\":[{\"label\":\"Lock\"}]}]}}";
            var put = await client.PutAsync("/streamdeck/decks/SERIAL-1/config", Json(putBody));
            Assert.True(put.IsSuccessStatusCode);

            var get = await client.GetAsync("/streamdeck/decks/SERIAL-1/config");
            using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            var slot = doc.RootElement.GetProperty("config").GetProperty("pages")[0].GetProperty("slots")[0];
            // The HTTP pipeline's merged JsonSerializerOptions write every
            // nullable DeckSlot field explicitly (icon/color/action/folder
            // included) rather than omitting it, so "title" is either absent
            // or JSON null here - never an empty title object, which would
            // read on the web side as an explicitly-customized style.
            Assert.True(!slot.TryGetProperty("title", out var titleEl) || titleEl.ValueKind == JsonValueKind.Null);

            var store = factory.Services.GetRequiredService<IConfigStore>();
            Assert.Null(store.Load().StreamDeck.Decks["SERIAL-1"].Deck.Pages[0].Slots[0].Title);
        }
    }

    [Fact]
    public async Task PutConfig_LegacySlotsShape_NormalizesToOnePage()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var putBody = "{\"config\":{\"slots\":[{\"label\":\"Lock\",\"action\":{\"type\":\"power\",\"action\":\"lock\"}}]}}";
            var put = await client.PutAsync("/streamdeck/decks/SERIAL-1/config", Json(putBody));
            Assert.True(put.IsSuccessStatusCode);

            var store = factory.Services.GetRequiredService<IConfigStore>();
            var pages = store.Load().StreamDeck.Decks["SERIAL-1"].Deck.Pages;
            Assert.Single(pages);
            Assert.Equal("Lock", pages[0].Slots[0].Label);
        }
    }

    [Fact]
    public async Task UpdateDeck_PersistsNameAndBrightness()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/streamdeck/decks/SERIAL-1", Json("{\"name\":\"Desk Deck\",\"brightness\":77}"));
            Assert.True(res.IsSuccessStatusCode);

            var store = factory.Services.GetRequiredService<IConfigStore>();
            var deck = store.Load().StreamDeck.Decks["SERIAL-1"];
            Assert.Equal("Desk Deck", deck.Name);
            Assert.Equal(77, deck.Brightness);
        }
    }

    [Fact]
    public async Task UpdateDeck_PersistsOrientationAndSleepAfterSeconds()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/streamdeck/decks/SERIAL-1", Json("{\"orientation\":180,\"sleepAfterSeconds\":120}"));
            Assert.True(res.IsSuccessStatusCode);

            var store = factory.Services.GetRequiredService<IConfigStore>();
            var deck = store.Load().StreamDeck.Decks["SERIAL-1"];
            Assert.Equal(180, deck.Orientation);
            Assert.Equal(120, deck.SleepAfterSeconds);
        }
    }

    [Theory]
    [InlineData(100, 90)]
    [InlineData(-90, 270)]
    [InlineData(400, 0)]
    [InlineData(359, 0)]
    [InlineData(45, 90)]
    [InlineData(135, 180)]
    [InlineData(225, 270)]
    [InlineData(315, 0)]
    public async Task UpdateDeck_ClampsOrientationToNearestCanonicalValue(int input, int expected)
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/streamdeck/decks/SERIAL-1", Json($"{{\"orientation\":{input}}}"));
            Assert.True(res.IsSuccessStatusCode);

            var store = factory.Services.GetRequiredService<IConfigStore>();
            Assert.Equal(expected, store.Load().StreamDeck.Decks["SERIAL-1"].Orientation);
        }
    }

    [Fact]
    public async Task UpdateDeck_ClampsSleepAfterSecondsToNonNegative()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var res = await client.PostAsync("/streamdeck/decks/SERIAL-1", Json("{\"sleepAfterSeconds\":-5}"));
            Assert.True(res.IsSuccessStatusCode);

            var store = factory.Services.GetRequiredService<IConfigStore>();
            Assert.Equal(0, store.Load().StreamDeck.Decks["SERIAL-1"].SleepAfterSeconds);
        }
    }

    [Fact]
    public async Task UploadImage_CachesToDiskAndReturnsTheHash()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var bytes = new byte[] { 0x42, 0x4d, 1, 2, 3, 4, 5 };
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var res = await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", content);
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var hash = doc.RootElement.GetProperty("hash").GetString();
            Assert.False(string.IsNullOrEmpty(hash));
            Assert.Equal(StreamDeckImageCache.Hash(bytes), hash);

            var onDisk = Path.Combine(_imageCacheDir, "SERIAL-1", hash + ".bin");
            Assert.True(File.Exists(onDisk));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(onDisk));

            var store = factory.Services.GetRequiredService<IConfigStore>();
            Assert.Equal(hash, store.Load().StreamDeck.Decks["SERIAL-1"].ImageRefs["0/0"]);
        }
    }

    [Fact]
    public async Task UploadImage_BackSlotPath_CachesUnderTheReservedKey()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var bytes = new byte[] { 0x42, 0x4d, 9, 9, 9 };
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var res = await client.PutAsync("/streamdeck/decks/SERIAL-1/images/back/0", content);
            Assert.True(res.IsSuccessStatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var hash = doc.RootElement.GetProperty("hash").GetString();
            Assert.False(string.IsNullOrEmpty(hash));

            var store = factory.Services.GetRequiredService<IConfigStore>();
            Assert.Equal(hash, store.Load().StreamDeck.Decks["SERIAL-1"].ImageRefs["back/0"]);
        }
    }

    [Fact]
    public async Task UploadImage_InvalidSerialCharset_Returns400()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var bytes = new byte[] { 0x42, 0x4d, 1, 2, 3 };
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var path = $"/streamdeck/decks/{Uri.EscapeDataString("bad serial")}/images/0/0";
            var res = await client.PutAsync(path, content);

            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var onDisk = Path.Combine(_imageCacheDir, "bad serial");
            Assert.False(Directory.Exists(onDisk));
        }
    }

    [Theory]
    [InlineData("abc", "0")]
    [InlineData("0", "2")]
    [InlineData("back", "1")]
    public async Task UploadImage_InvalidSlotPathOrState_Returns400(string slotPath, string state)
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var bytes = new byte[] { 0x42, 0x4d, 1, 2, 3 };
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var res = await client.PutAsync($"/streamdeck/decks/SERIAL-1/images/{slotPath}/{state}", content);

            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task UploadImage_ReplacingASlot_EvictsTheOrphanedHash()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var first = new byte[] { 0x42, 0x4d, 1, 1, 1 };
            var firstContent = new ByteArrayContent(first);
            firstContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var firstRes = await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", firstContent);
            using var firstDoc = JsonDocument.Parse(await firstRes.Content.ReadAsStringAsync());
            var firstHash = firstDoc.RootElement.GetProperty("hash").GetString()!;
            var firstPath = Path.Combine(_imageCacheDir, "SERIAL-1", firstHash + ".bin");
            Assert.True(File.Exists(firstPath));

            var second = new byte[] { 0x42, 0x4d, 2, 2, 2 };
            var secondContent = new ByteArrayContent(second);
            secondContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var secondRes = await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", secondContent);
            using var secondDoc = JsonDocument.Parse(await secondRes.Content.ReadAsStringAsync());
            var secondHash = secondDoc.RootElement.GetProperty("hash").GetString()!;

            Assert.NotEqual(firstHash, secondHash);
            Assert.False(File.Exists(firstPath));
            Assert.True(File.Exists(Path.Combine(_imageCacheDir, "SERIAL-1", secondHash + ".bin")));
        }
    }

    [Fact]
    public async Task UploadImage_ReplacingASlot_KeepsTheHashIfStillReferencedByAnotherSlot()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var shared = new byte[] { 0x42, 0x4d, 3, 3, 3 };
            var sharedHash = StreamDeckImageCache.Hash(shared);

            foreach (var refKey in new[] { "0.0/0", "0.1/0" })
            {
                var content = new ByteArrayContent(shared);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                await client.PutAsync($"/streamdeck/decks/SERIAL-1/images/{refKey}", content);
            }

            var replacement = new byte[] { 0x42, 0x4d, 4, 4, 4 };
            var replacementContent = new ByteArrayContent(replacement);
            replacementContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0.0/0", replacementContent);

            // slot 0.1/0 still references sharedHash, so it must survive.
            Assert.True(File.Exists(Path.Combine(_imageCacheDir, "SERIAL-1", sharedHash + ".bin")));
        }
    }

    [Fact]
    public async Task UploadImage_WrongSizeForAKnownModel_Fails()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var mini = StreamDeckModels.ByProductId(0x0063)!;
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings { ProductId = mini.ProductId });

            var wrongSize = new byte[] { 0x42, 0x4d, 1, 2, 3 };
            var content = new ByteArrayContent(wrongSize);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var res = await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", content);

            var text = await res.Content.ReadAsStringAsync();
            Assert.Contains("\"error\":true", text);
        }
    }

    [Fact]
    public async Task UploadImage_ExactModelSizeForAKnownModel_Succeeds()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var mini = StreamDeckModels.ByProductId(0x0063)!;
            var store = factory.Services.GetRequiredService<IConfigStore>();
            store.Update(s => s.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings { ProductId = mini.ProductId });

            var exactSize = new byte[54 + mini.KeyPixelSize * mini.KeyPixelSize * 3];
            var content = new ByteArrayContent(exactSize);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var res = await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0/0", content);

            Assert.True(res.IsSuccessStatusCode);
            var text = await res.Content.ReadAsStringAsync();
            Assert.DoesNotContain("\"error\":true", text);
        }
    }

    /// <summary>Two-page config, each page's root slot 0 an action, so a v2 image upload can target either page's key 0 independently.</summary>
    private static string TwoPageActionConfigBody() =>
        "{\"config\":{\"pages\":["
        + "{\"slots\":[{\"action\":{\"type\":\"power\",\"action\":\"lock\"}}]},"
        + "{\"slots\":[{\"action\":{\"type\":\"power\",\"action\":\"lock\"}}]}]}}";

    [Fact]
    public async Task UploadImage_TargetingTheCurrentlyVisiblePage_RepaintsTheLiveKey()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();
            var mini = StreamDeckModels.ByProductId(0x0063)!;
            Assert.True(worker.SetSimulatedModel(mini.ProductId));
            var serial = worker.Surfaces[StreamDeckConnectionWorker.SimulatedKey].Serial;
            var simulated = (SimulatedStreamDeckSurface)worker.Surfaces[StreamDeckConnectionWorker.SimulatedKey];
            await client.PutAsync($"/streamdeck/decks/{serial}/config", Json(TwoPageActionConfigBody()));

            var exactSize = new byte[54 + mini.KeyPixelSize * mini.KeyPixelSize * 3];
            var content = new ByteArrayContent(exactSize);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var res = await client.PutAsync($"/streamdeck/decks/{serial}/images/0.0/0", content);
            Assert.True(res.IsSuccessStatusCode);

            Assert.Equal(exactSize, simulated.PeekKeyImage(0));
        }
    }

    [Fact]
    public async Task UploadImage_TargetingANonVisiblePage_SkipsTheRepaint()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();
            var mini = StreamDeckModels.ByProductId(0x0063)!;
            Assert.True(worker.SetSimulatedModel(mini.ProductId));
            var serial = worker.Surfaces[StreamDeckConnectionWorker.SimulatedKey].Serial;
            var simulated = (SimulatedStreamDeckSurface)worker.Surfaces[StreamDeckConnectionWorker.SimulatedKey];
            await client.PutAsync($"/streamdeck/decks/{serial}/config", Json(TwoPageActionConfigBody()));

            var pageZeroImage = new byte[54 + mini.KeyPixelSize * mini.KeyPixelSize * 3];
            pageZeroImage[54] = 1;
            var pageZeroContent = new ByteArrayContent(pageZeroImage);
            pageZeroContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            await client.PutAsync($"/streamdeck/decks/{serial}/images/0.0/0", pageZeroContent);
            var callsAfterVisibleUpload = simulated.SetKeyImageCallCount;

            // The deck is still showing page 0 - a v2 upload targeting page
            // 1's own key 0 must not trigger a repaint of the live view.
            var pageOneImage = new byte[54 + mini.KeyPixelSize * mini.KeyPixelSize * 3];
            pageOneImage[54] = 2;
            var pageOneContent = new ByteArrayContent(pageOneImage);
            pageOneContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var res = await client.PutAsync($"/streamdeck/decks/{serial}/images/1.0/0", pageOneContent);
            Assert.True(res.IsSuccessStatusCode);

            Assert.Equal(callsAfterVisibleUpload, simulated.SetKeyImageCallCount);
            Assert.Equal(pageZeroImage, simulated.PeekKeyImage(0));
        }
    }

    [Fact]
    public async Task UploadImage_BackSlotPath_RepaintsOnlyWhileTheDeckIsInAFolder()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();
            var mini = StreamDeckModels.ByProductId(0x0063)!;
            Assert.True(worker.SetSimulatedModel(mini.ProductId));
            var serial = worker.Surfaces[StreamDeckConnectionWorker.SimulatedKey].Serial;
            var simulated = (SimulatedStreamDeckSurface)worker.Surfaces[StreamDeckConnectionWorker.SimulatedKey];
            var folderConfig = "{\"config\":{\"pages\":[{\"slots\":[{\"folder\":{\"slots\":[{}]}}]}]}}";
            await client.PutAsync($"/streamdeck/decks/{serial}/config", Json(folderConfig));

            var backImage = new byte[54 + mini.KeyPixelSize * mini.KeyPixelSize * 3];
            var atRootContent = new ByteArrayContent(backImage);
            atRootContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var atRoot = await client.PutAsync($"/streamdeck/decks/{serial}/images/back/0", atRootContent);
            Assert.True(atRoot.IsSuccessStatusCode);
            Assert.Null(simulated.PeekKeyImage(0));

            var navRes = await client.PostAsync($"/streamdeck/decks/{serial}/nav", Json("{\"page\":0,\"folderPath\":[0]}"));
            Assert.True(navRes.IsSuccessStatusCode);

            var inFolderContent = new ByteArrayContent(backImage);
            inFolderContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var inFolder = await client.PutAsync($"/streamdeck/decks/{serial}/images/back/0", inFolderContent);
            Assert.True(inFolder.IsSuccessStatusCode);

            Assert.Equal(backImage, simulated.PeekKeyImage(0));
        }
    }

    [Fact]
    public async Task UploadImage_ReplacingASlot_EvictsAHashEvenIfOnlyALegacyV1KeyStillPointsAtIt()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var shared = new byte[] { 9, 8, 7 };
            var sharedHash = StreamDeckImageCache.Hash(shared);
            var store = factory.Services.GetRequiredService<IConfigStore>();
            var cache = factory.Services.GetRequiredService<StreamDeckImageCache>();
            cache.Store("SERIAL-1", sharedHash, shared);
            // Seeds a legacy pre-v2 "0/0" entry alongside the v2 "0.0/0"
            // entry this test is about to replace - both point at sharedHash.
            store.Update(s => s.StreamDeck.Decks["SERIAL-1"] = new PhysicalDeckSettings
            {
                ImageRefs = { ["0/0"] = sharedHash, ["0.0/0"] = sharedHash },
            });
            var blobPath = Path.Combine(_imageCacheDir, "SERIAL-1", sharedHash + ".bin");

            var replacement = new byte[] { 1, 2, 3 };
            var content = new ByteArrayContent(replacement);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            await client.PutAsync("/streamdeck/decks/SERIAL-1/images/0.0/0", content);

            // The legacy "0/0" key still nominally points at sharedHash, but
            // it is not counted as a reference, so the blob is evicted even
            // though that stale dict entry was never cleaned up (no migration).
            Assert.False(File.Exists(blobPath));
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
                Deck = new DeckConfig { Pages = { new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "power", PowerAction = "lock" } } } } } },
            });

            var res = await client.PostAsync("/streamdeck/decks/SERIAL-1/test-press/0", Json("{}"));
            Assert.True(res.IsSuccessStatusCode);

            // The dispatch is fire-and-forget (Task.Run), same as a real key
            // press - await the test seam before asserting the spy was called.
            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();
            if (worker.LastDispatchTask is not null)
            {
                await worker.LastDispatchTask;
            }

            Assert.NotNull(_executor.LastCall);
            Assert.Equal("SERIAL-1", _executor.LastCall!.Value.Serial);
            Assert.Equal("lock", _executor.LastCall.Value.Action!.PowerAction);
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
            Assert.Null(_executor.LastCall);
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
                Deck = new DeckConfig
                {
                    Pages =
                    {
                        new DeckPage { Slots = { new DeckSlot { Action = new DeckAction { Type = "page", Op = "next" } } } },
                        new DeckPage(),
                    },
                },
            });
            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();
            Assert.Equal(0, worker.GetCurrentPage("SERIAL-1"));

            var res = await client.PostAsync("/streamdeck/decks/SERIAL-1/test-press/0", Json("{}"));
            Assert.True(res.IsSuccessStatusCode);

            // A "page" slot is intercepted before the executor, exactly like
            // a real key press - the executor must never see it.
            Assert.Equal(1, worker.GetCurrentPage("SERIAL-1"));
            Assert.Null(_executor.LastCall);
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
                Deck = new DeckConfig
                {
                    Pages = { new DeckPage { Slots = { new DeckSlot { Folder = new DeckFolder { Slots = { new DeckSlot() } } } } } },
                },
            });
            var worker = factory.Services.GetRequiredService<StreamDeckConnectionWorker>();

            var res = await client.PostAsync("/streamdeck/decks/SERIAL-1/test-press/0", Json("{}"));
            Assert.True(res.IsSuccessStatusCode);

            Assert.Equal(new[] { 0 }, worker.GetFolderPath("SERIAL-1"));
            Assert.Null(_executor.LastCall);
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
            var anon = factory.CreateClient();
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
