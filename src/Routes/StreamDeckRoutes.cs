using System.Buffers.Binary;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Auth;
using Nexus.Service.Deck;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Models;
using Nexus.Service.Models.Peripherals.StreamDeck;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Peripherals.StreamDeck.ElgatoImport;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

/// <summary>
/// Stream Deck REST contract (plan streamdeck-support.md §5.5). Every route
/// is LocalhostOnly - a physical deck is a desktop configuration surface, not
/// something a paired phone panel touches. test-pattern and the dev/* routes
/// (sim-press, simulate, models) are additive bench/simulator tooling, not
/// part of the desktop contract the web editor drives.
/// </summary>
public static class StreamDeckRoutes
{
    /// <summary>Key images are at most 96x96; a few hundred KB covers any BMP/JPEG encode with margin.</summary>
    private const long MaxImageBytes = 300_000;

    public static void MapStreamDeckEndpoints(this WebApplication app)
    {
        app.MapGet("/streamdeck/decks", (
            StreamDeckConnectionWorker worker, IConfigStore store, StreamDeckHandler handler, IUsbEnumerator usb) =>
        {
            var settings = store.Load().StreamDeck;
            var warning = handler.GetWarning(usb.Enumerate());
            var conflictAppId = ResolveConflictAppId(warning);
            var response = new GetStreamDecksResponse();
            var seenSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, surface) in worker.Surfaces)
            {
                seenSerials.Add(surface.Serial);
                settings.Decks.TryGetValue(surface.Serial, out var deck);
                response.Decks.Add(BuildSummary(worker, surface, deck, warning, conflictAppId));
            }

            // Persisted decks with no live surface (unplugged, or never seen
            // this run) still list so their name/config stay reachable.
            foreach (var (serial, deck) in settings.Decks)
            {
                if (seenSerials.Contains(serial))
                {
                    continue;
                }
                var model = StreamDeckModels.ByProductId(deck.ProductId);
                if (model is null)
                {
                    continue;
                }
                response.Decks.Add(new StreamDeckSummaryDto
                {
                    Serial = serial,
                    Model = model.Name,
                    Name = string.IsNullOrEmpty(deck.Name) ? model.Name : deck.Name,
                    Connected = false,
                    Verified = model.Verified,
                    Rows = model.Rows,
                    Columns = model.Columns,
                    KeyCount = model.KeyCount,
                    KeyPixels = model.KeyPixelSize,
                    Format = FormatName(model.ImageFormat),
                    Transform = model.Transform,
                    Brightness = deck.Brightness,
                    Orientation = deck.Orientation,
                    SleepAfterSeconds = deck.SleepAfterSeconds,
                    FirmwareVersion = "",
                    Warning = null,
                    ConflictAppId = null,
                    CurrentPage = worker.GetCurrentPage(serial),
                    FolderPath = worker.GetFolderPath(serial).ToList(),
                });
            }
            return response;
        }).LocalhostOnly();

        app.MapPost("/streamdeck/decks/{serial}", (
            string serial, UpdateStreamDeckBody body, StreamDeckConnectionWorker worker, IConfigStore store, MultiplexHub hub) =>
        {
            store.Update(s =>
            {
                if (!s.StreamDeck.Decks.TryGetValue(serial, out var deck))
                {
                    deck = new PhysicalDeckSettings();
                    s.StreamDeck.Decks[serial] = deck;
                }
                if (body.Name is not null)
                {
                    deck.Name = body.Name;
                }
                if (body.Brightness is not null)
                {
                    deck.Brightness = Math.Clamp(body.Brightness.Value, 0, 100);
                }
                if (body.Orientation is not null)
                {
                    deck.Orientation = ClampOrientation(body.Orientation.Value);
                }
                if (body.SleepAfterSeconds is not null)
                {
                    deck.SleepAfterSeconds = Math.Max(0, body.SleepAfterSeconds.Value);
                }
            });
            // Skipped while the deck is asleep (sleep-after already blanked
            // it): the new value already persisted above and applies the
            // moment the next key press wakes it.
            if (body.Brightness is not null && !worker.IsAsleep(serial))
            {
                worker.FindBySerial(serial)?.SetBrightness(Math.Clamp(body.Brightness.Value, 0, 100));
            }
            PanelTopics.BroadcastStreamDeck(hub, new StreamDeckChangedFrame { Kind = "decks", Serial = serial });
            return ApiResponse.Ok();
        }).LocalhostOnly();

        app.MapGet("/streamdeck/decks/{serial}/config", (string serial, IConfigStore store) =>
        {
            var settings = store.Load().StreamDeck;
            var config = settings.Decks.TryGetValue(serial, out var deck) ? deck.Deck : new DeckConfig { Pages = { new DeckPage() } };
            return new StreamDeckConfigEnvelope { Config = config };
        }).LocalhostOnly();

        app.MapPut("/streamdeck/decks/{serial}/config", (
            string serial, StreamDeckConfigEnvelope body, StreamDeckConnectionWorker worker, IConfigStore store, MultiplexHub hub) =>
        {
            var config = body.Config;
            store.Update(s =>
            {
                if (!s.StreamDeck.Decks.TryGetValue(serial, out var deck))
                {
                    deck = new PhysicalDeckSettings();
                    s.StreamDeck.Decks[serial] = deck;
                }
                deck.Deck = config;
            });
            worker.RefreshView(serial);
            PanelTopics.BroadcastStreamDeck(hub, new StreamDeckChangedFrame { Kind = "config", Serial = serial });
            return new StreamDeckConfigEnvelope { Config = config };
        }).LocalhostOnly();

        // Mirror the desktop editor's navigation (page + folder) onto the deck.
        app.MapPost("/streamdeck/decks/{serial}/nav", (
            string serial, StreamDeckNavBody body, StreamDeckConnectionWorker worker) =>
        {
            return worker.SetNav(serial, body.Page, body.FolderPath ?? new List<int>())
                ? ApiResponse.Ok()
                : ApiResponse.Fail("deck not found");
        }).LocalhostOnly();

        // A blank-key hold-to-edit that fired while the app was closed lands
        // here on the freshly-opened dashboard, which navigates to the deck's
        // editor and selects the held key. Not cleared on read (multiple
        // dashboard readers must all see it); the intent ages out on its own
        // after PendingEditTtl, and the client dedupes on Token.
        app.MapGet("/streamdeck/pending-edit", (StreamDeckConnectionWorker worker) =>
        {
            var response = new StreamDeckPendingEditResponse();
            if (worker.TryGetPendingEdit(out var edit))
            {
                response.Edit = new StreamDeckPendingEditDto
                {
                    Serial = edit.Serial,
                    Page = edit.Page,
                    FolderPath = edit.FolderPath.ToList(),
                    KeyIndex = edit.SlotIndex,
                    Token = edit.Token,
                };
            }
            return Results.Json(response, AppJsonContext.Default.StreamDeckPendingEditResponse);
        }).LocalhostOnly();

        app.MapPut("/streamdeck/decks/{serial}/images/{slotPath}/{state}", async (
            string serial, string slotPath, string state, HttpRequest req,
            StreamDeckImageCache cache, IConfigStore store, StreamDeckConnectionWorker worker, CancellationToken ct) =>
        {
            if (!StreamDeckImageCache.IsValidSerial(serial))
            {
                return Results.Json(ApiResponse.Fail("invalid serial"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            if (!IsValidImageSlotAndState(slotPath, state))
            {
                return Results.Json(ApiResponse.Fail("invalid slot path or state"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }

            var (bytes, tooLarge) = await ReadBoundedAsync(req.Body, MaxImageBytes, ct).ConfigureAwait(false);
            if (tooLarge)
            {
                return Results.Json(ApiResponse.Fail("image too large"), AppJsonContext.Default.ApiResponse);
            }
            if (bytes is null || bytes.Length == 0)
            {
                return Results.Json(ApiResponse.Fail("empty upload"), AppJsonContext.Default.ApiResponse);
            }

            var model = worker.FindBySerial(serial)?.Model;
            if (model is null && store.Load().StreamDeck.Decks.TryGetValue(serial, out var persistedDeck))
            {
                model = StreamDeckModels.ByProductId(persistedDeck.ProductId);
            }
            if (model is not null && !model.IsValidWireImageLength(bytes.Length))
            {
                return Results.Json(ApiResponse.Fail("image size does not match this deck's key format"), AppJsonContext.Default.ApiResponse);
            }

            var hash = StreamDeckImageCache.Hash(bytes);
            try
            {
                cache.Store(serial, hash, bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ServiceLog.Warn($"[streamdeck] image cache write failed for {serial}: {ex.Message}");
                return Results.Json(ApiResponse.Fail("image cache write failed"), AppJsonContext.Default.ApiResponse);
            }
            string? evictHash = null;
            var refKey = $"{slotPath}/{state}";
            store.Update(s =>
            {
                if (!s.StreamDeck.Decks.TryGetValue(serial, out var deck))
                {
                    deck = new PhysicalDeckSettings();
                    s.StreamDeck.Decks[serial] = deck;
                }
                if (deck.ImageRefs.TryGetValue(refKey, out var previousHash) &&
                    previousHash != hash &&
                    !IsHashReferenced(deck, previousHash, refKey))
                {
                    evictHash = previousHash;
                }
                deck.ImageRefs[refKey] = hash;
            });
            if (evictHash is not null)
            {
                cache.Evict(serial, evictHash);
            }
            if (IsUploadedKeyVisible(serial, slotPath, worker))
            {
                worker.RefreshView(serial);
            }
            return Results.Json(new StreamDeckImageUploadResponse { Hash = hash }, AppJsonContext.Default.StreamDeckImageUploadResponse);
        }).LocalhostOnly();

        app.MapPost("/streamdeck/decks/{serial}/test-press/{slotPath}", (
            string serial, string slotPath, StreamDeckConnectionWorker worker, IConfigStore store) =>
        {
            var indices = DeckConfigNavigation.ParseSlotPath(slotPath);
            if (indices is null)
            {
                return ApiResponse.Fail("invalid slot path");
            }
            var settings = store.Load().StreamDeck;
            if (!settings.Decks.TryGetValue(serial, out var deck))
            {
                return ApiResponse.Fail("deck not found");
            }

            // Routes through the same nav-or-dispatch decision a real key
            // press makes (HandleSlotAction), so testing a page or folder
            // slot navigates the tracked deck state exactly like pressing
            // the physical key would, not just the leaf-action executor.
            return worker.SimulatePress(serial, indices, deck.Deck)
                ? ApiResponse.Ok()
                : ApiResponse.Fail("slot has no action");
        }).LocalhostOnly();

        app.MapPost("/streamdeck/decks/{serial}/test-pattern", (string serial, StreamDeckConnectionWorker worker) =>
        {
            var surface = worker.FindBySerial(serial);
            if (surface is null)
            {
                return ApiResponse.Fail("deck not found");
            }
            if (surface.Model.ImageFormat != StreamDeckImageFormat.Bmp)
            {
                return ApiResponse.Fail("test pattern only supported for gen1 BMP models");
            }

            for (var i = 0; i < surface.Model.KeyCount; i++)
            {
                var (r, g, b) = TestPatternColors[i % TestPatternColors.Length];
                var bmp = BuildSolidBmp(surface.Model.KeyPixelSize, surface.Model.KeyPixelSize, r, g, b);
                if (!surface.SetKeyImage(i, bmp))
                {
                    return ApiResponse.Fail($"key {i} image push failed");
                }
            }
            return ApiResponse.Ok();
        }).LocalhostOnly();

        app.MapGet("/streamdeck/decks/{serial}/presets", (
            string serial, IConfigStore store) =>
        {
            var settings = store.Load().StreamDeck;
            settings.Decks.TryGetValue(serial, out var deck);
            return Results.Json(
                new GetDeckPresetsResponse
                {
                    Presets = deck?.Presets.ConvertAll(ToPresetDto) ?? new List<DeckPresetDto>(),
                    ActiveId = deck?.ActivePresetId,
                },
                AppJsonContext.Default.GetDeckPresetsResponse);
        }).LocalhostOnly();

        app.MapPost("/streamdeck/decks/{serial}/presets", (
            string serial, CreateDeckPresetBody body, IConfigStore store) =>
        {
            bool capped = false;
            bool nameTaken = false;
            DeckPreset? created = null;
            store.Update(s =>
            {
                if (!s.StreamDeck.Decks.TryGetValue(serial, out var deck))
                {
                    deck = new PhysicalDeckSettings();
                    s.StreamDeck.Decks[serial] = deck;
                }
                if (deck.Presets.Count >= 10)
                {
                    capped = true;
                    return;
                }
                var trimmedName = string.IsNullOrEmpty(body.Name) ? "" : body.Name.Trim();
                if (trimmedName.Length > 0
                    && deck.Presets.Any(p => string.Equals(p.Name, trimmedName, StringComparison.OrdinalIgnoreCase)))
                {
                    nameTaken = true;
                    return;
                }
                var id = Guid.NewGuid().ToString("n");
                created = body.Config is not null
                    ? new DeckPreset
                    {
                        Id = id,
                        Name = trimmedName,
                        Deck = DeepCopyDeckConfig(body.Config),
                        ImageRefs = new Dictionary<string, string>(),
                    }
                    : new DeckPreset
                    {
                        Id = id,
                        Name = trimmedName,
                        Deck = DeepCopyDeckConfig(deck.Deck),
                        ImageRefs = new Dictionary<string, string>(deck.ImageRefs),
                    };
                deck.Presets.Add(created);
                deck.ActivePresetId = id;
            });
            if (capped)
            {
                return Results.Json(
                    ApiResponse.Fail("Deck preset cap of 10 reached"),
                    AppJsonContext.Default.ApiResponse,
                    statusCode: 400);
            }
            if (nameTaken)
            {
                return Results.Json(
                    ApiResponse.Fail("preset_name_taken"),
                    AppJsonContext.Default.ApiResponse,
                    statusCode: 409);
            }
            return Results.Json(
                new CreateDeckPresetResponse { Preset = ToPresetDto(created!), ActiveId = created!.Id },
                AppJsonContext.Default.CreateDeckPresetResponse);
        }).LocalhostOnly();

        app.MapPut("/streamdeck/decks/{serial}/presets/active", (
            string serial, SetActiveDeckPresetBody body, IConfigStore store) =>
        {
            store.Update(s =>
            {
                if (!s.StreamDeck.Decks.TryGetValue(serial, out var deck))
                {
                    deck = new PhysicalDeckSettings();
                    s.StreamDeck.Decks[serial] = deck;
                }
                deck.ActivePresetId = body.Id;
            });
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        }).LocalhostOnly();

        app.MapPut("/streamdeck/decks/{serial}/presets/{id}", (
            string serial, string id, UpdateDeckPresetBody body, IConfigStore store, StreamDeckImageCache cache) =>
        {
            var settings = store.Load().StreamDeck;
            if (!settings.Decks.TryGetValue(serial, out var existingDeck) || existingDeck.Presets.Find(p => p.Id == id) is null)
            {
                return Results.Json(
                    ApiResponse.Fail("Deck preset not found"),
                    AppJsonContext.Default.ApiResponse,
                    statusCode: 404);
            }

            bool nameTaken = false;
            List<string>? evictHashes = null;
            store.Update(s =>
            {
                if (!s.StreamDeck.Decks.TryGetValue(serial, out var deck))
                {
                    return;
                }
                var p = deck.Presets.Find(x => x.Id == id);
                if (p is null)
                {
                    return;
                }
                if (!string.IsNullOrEmpty(body.Name))
                {
                    var trimmedName = body.Name.Trim();
                    if (trimmedName.Length > 0
                        && deck.Presets.Any(x => x.Id != id && string.Equals(x.Name, trimmedName, StringComparison.OrdinalIgnoreCase)))
                    {
                        nameTaken = true;
                        return;
                    }
                    p.Name = trimmedName;
                }
                if (body.SaveCurrent)
                {
                    var previousHashes = p.ImageRefs.Values.Distinct().ToList();
                    p.Deck = DeepCopyDeckConfig(deck.Deck);
                    p.ImageRefs = new Dictionary<string, string>(deck.ImageRefs);
                    evictHashes = previousHashes.Where(h => !IsHashReferenced(deck, h, null)).ToList();
                }
            });
            if (nameTaken)
            {
                return Results.Json(
                    ApiResponse.Fail("preset_name_taken"),
                    AppJsonContext.Default.ApiResponse,
                    statusCode: 409);
            }
            if (evictHashes is not null)
            {
                foreach (var h in evictHashes)
                {
                    cache.Evict(serial, h);
                }
            }
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        }).LocalhostOnly();

        app.MapDelete("/streamdeck/decks/{serial}/presets/{id}", (
            string serial, string id, IConfigStore store, StreamDeckConnectionWorker worker, MultiplexHub hub, StreamDeckImageCache cache) =>
        {
            string? activeId = null;
            var promoted = false;
            List<string>? evictHashes = null;
            store.Update(s =>
            {
                if (!s.StreamDeck.Decks.TryGetValue(serial, out var deck))
                {
                    return;
                }
                var removed = deck.Presets.Find(p => p.Id == id);
                deck.Presets.RemoveAll(p => p.Id == id);
                if (deck.ActivePresetId == id)
                {
                    // Deleting the active preset promotes the first remaining one
                    // and applies its layout to the live deck, rather than leaving
                    // nothing selected. Falls to null only when none remain.
                    var next = deck.Presets.Count > 0 ? deck.Presets[0] : null;
                    if (next is not null)
                    {
                        deck.Deck = DeepCopyDeckConfig(next.Deck);
                        deck.ImageRefs = new Dictionary<string, string>(next.ImageRefs);
                        deck.ActivePresetId = next.Id;
                        promoted = true;
                    }
                    else
                    {
                        deck.ActivePresetId = null;
                    }
                }
                activeId = deck.ActivePresetId;
                if (removed is not null)
                {
                    evictHashes = removed.ImageRefs.Values
                        .Distinct()
                        .Where(h => !IsHashReferenced(deck, h, null))
                        .ToList();
                }
            });
            if (evictHashes is not null)
            {
                foreach (var h in evictHashes)
                {
                    cache.Evict(serial, h);
                }
            }
            if (promoted)
            {
                worker.SetNav(serial, 0, System.Array.Empty<int>());
                PanelTopics.BroadcastStreamDeck(hub, new StreamDeckChangedFrame { Kind = "config", Serial = serial });
            }
            return Results.Json(
                new DeleteDeckPresetResponse { ActiveId = activeId },
                AppJsonContext.Default.DeleteDeckPresetResponse);
        }).LocalhostOnly();

        app.MapPost("/streamdeck/decks/{serial}/presets/{id}/activate", (
            string serial, string id, IConfigStore store, StreamDeckConnectionWorker worker, MultiplexHub hub, StreamDeckImageCache cache) =>
        {
            var settings = store.Load().StreamDeck;
            if (!settings.Decks.TryGetValue(serial, out var existingDeck))
            {
                return Results.Json(
                    ApiResponse.Fail("Deck preset not found"),
                    AppJsonContext.Default.ApiResponse,
                    statusCode: 404);
            }
            var preset = existingDeck.Presets.Find(p => p.Id == id);
            if (preset is null)
            {
                return Results.Json(
                    ApiResponse.Fail("Deck preset not found"),
                    AppJsonContext.Default.ApiResponse,
                    statusCode: 404);
            }

            var config = DeepCopyDeckConfig(preset.Deck);
            var imageRefs = new Dictionary<string, string>(preset.ImageRefs);
            List<string>? evictHashes = null;
            store.Update(s =>
            {
                if (!s.StreamDeck.Decks.TryGetValue(serial, out var deck))
                {
                    return;
                }
                var previousHashes = deck.ImageRefs.Values.Distinct().ToList();
                deck.Deck = config;
                deck.ImageRefs = imageRefs;
                deck.ActivePresetId = id;
                evictHashes = previousHashes.Where(h => !IsHashReferenced(deck, h, null)).ToList();
            });
            if (evictHashes is not null)
            {
                foreach (var h in evictHashes)
                {
                    cache.Evict(serial, h);
                }
            }
            // Switching preset opens the new layout on page 1, not wherever the
            // previous preset was left (a folder or a later page); SetNav resets
            // page + folder path to the top and pushes the fresh view.
            worker.SetNav(serial, 0, System.Array.Empty<int>());
            PanelTopics.BroadcastStreamDeck(hub, new StreamDeckChangedFrame { Kind = "config", Serial = serial });
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        }).LocalhostOnly();

        // Elgato Stream Deck profile import: read-only against the local
        // Elgato software's own store. Never persists anything - the caller
        // decides whether to save the returned config as a preset via
        // POST /streamdeck/decks/{serial}/presets with its Config field.
        app.MapGet("/streamdeck/elgato/profiles", (ElgatoProfileLocator locator) =>
        {
            var (status, root) = locator.Resolve();
            var response = new ElgatoProfilesResponse { Status = ElgatoStatusName(status) };
            if (status == ElgatoStoreStatus.Ok && root is not null)
            {
                foreach (var profile in ElgatoProfileReader.ReadProfiles(root))
                {
                    var (_, _, label) = ElgatoModelCatalog.Resolve(profile.Model, profile.MaxColSeen, profile.MaxRowSeen);
                    response.Profiles.Add(new ElgatoProfileSummaryDto
                    {
                        Id = profile.Id,
                        Name = profile.Name,
                        Model = profile.Model,
                        ModelLabel = label,
                        PageCount = profile.TopPageIds.Count,
                        KeyCount = ElgatoProfileReader.CountKeys(profile),
                    });
                }
            }
            return Results.Json(response, AppJsonContext.Default.ElgatoProfilesResponse);
        }).LocalhostOnly();

        app.MapPost("/streamdeck/elgato/profiles/{id}/import", (
            string id, ElgatoProfileLocator locator, ElgatoProfileTranslator translator) =>
        {
            var (status, root) = locator.Resolve();
            if (status != ElgatoStoreStatus.Ok || root is null)
            {
                return Results.NotFound();
            }
            var profile = ElgatoProfileReader.ReadProfiles(root).Find(p => p.Id == id);
            if (profile is null)
            {
                return Results.NotFound();
            }
            var (config, report) = translator.Translate(profile);
            return Results.Json(
                new ImportElgatoProfileResponse { Config = config, Report = report },
                AppJsonContext.Default.ImportElgatoProfileResponse);
        }).LocalhostOnly();

        // Stream Deck simulator (in-memory fake deck, no HID hardware). Served
        // in every build, not compile-gated: the web enables the simulator UI
        // whenever its dev-tools flag is on, which includes `vite dev`
        // (import.meta.env.DEV) - a state with no compile-time service
        // counterpart to gate on. LocalhostOnly, and a release web bundle
        // strips the simulator row, so these have no reachable UI there;
        // nothing constructs a SimulatedStreamDeckSurface until a route is
        // called.
        app.MapPost("/streamdeck/dev/sim-press", (StreamDeckSimPressBody body, StreamDeckConnectionWorker worker) =>
        {
            if (worker.Surfaces.TryGetValue(StreamDeckConnectionWorker.SimulatedKey, out var sim)
                && sim is SimulatedStreamDeckSurface simulated)
            {
                simulated.Poke(body.KeyIndex, body.Pressed);
                return ApiResponse.Ok();
            }
            return ApiResponse.Fail("simulator not available");
        }).LocalhostOnly();

        app.MapGet("/streamdeck/dev/models", () =>
        {
            var response = new StreamDeckDevModelsResponse();
            foreach (var model in StreamDeckModels.All)
            {
                response.Models.Add(new StreamDeckDevModelDto
                {
                    ProductId = model.ProductId,
                    Name = model.Name,
                    Rows = model.Rows,
                    Columns = model.Columns,
                    KeyCount = model.KeyCount,
                });
            }
            return response;
        }).LocalhostOnly();

        app.MapPost("/streamdeck/dev/simulate", (
            StreamDeckSimulateBody body, StreamDeckConnectionWorker worker, IConfigStore store) =>
        {
            if (!worker.SetSimulatedModel(body.ProductId))
            {
                return Results.Json(ApiResponse.Fail("unknown model"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            var surface = worker.Surfaces[StreamDeckConnectionWorker.SimulatedKey];
            store.Load().StreamDeck.Decks.TryGetValue(surface.Serial, out var deck);
            return Results.Json(
                BuildSummary(worker, surface, deck, warning: null, conflictAppId: null),
                AppJsonContext.Default.StreamDeckSummaryDto);
        }).LocalhostOnly();

        app.MapDelete("/streamdeck/dev/simulate", (
            StreamDeckConnectionWorker worker, IConfigStore store, StreamDeckImageCache cache) =>
        {
            worker.ClearSimulatedModel();
            // A simulated deck is ephemeral, but interacting with it (config /
            // image PUTs) persists a `sim-<pid>` record. Left behind, GET
            // /streamdeck/decks re-lists it as an offline deck, so the sim
            // never fully disconnects. Purge every sim- record and evict its
            // images on disconnect.
            var evict = new List<(string Serial, string Hash)>();
            store.Update(s =>
            {
                foreach (var serial in s.StreamDeck.Decks.Keys.Where(k => k.StartsWith("sim-", StringComparison.Ordinal)).ToList())
                {
                    var deck = s.StreamDeck.Decks[serial];
                    var hashes = deck.ImageRefs.Values
                        .Concat(deck.Presets.SelectMany(p => p.ImageRefs.Values))
                        .Distinct();
                    foreach (var hash in hashes)
                    {
                        evict.Add((serial, hash));
                    }
                    s.StreamDeck.Decks.Remove(serial);
                }
            });
            foreach (var (serial, hash) in evict)
            {
                cache.Evict(serial, hash);
            }
            return ApiResponse.Ok();
        }).LocalhostOnly();
    }

    /// <summary>Shared DTO builder for GET /streamdeck/decks and the dev-tools simulate route.</summary>
    private static StreamDeckSummaryDto BuildSummary(
        StreamDeckConnectionWorker worker, IStreamDeckSurface surface, PhysicalDeckSettings? deck, string? warning, string? conflictAppId) => new()
    {
        Serial = surface.Serial,
        Model = surface.Model.Name,
        Name = string.IsNullOrEmpty(deck?.Name) ? surface.Model.Name : deck!.Name,
        Connected = surface.IsConnected,
        Verified = surface.Model.Verified,
        Rows = surface.Model.Rows,
        Columns = surface.Model.Columns,
        KeyCount = surface.Model.KeyCount,
        KeyPixels = surface.Model.KeyPixelSize,
        Format = FormatName(surface.Model.ImageFormat),
        Transform = surface.Model.Transform,
        Brightness = deck?.Brightness ?? PhysicalDeckSettings.DefaultBrightness,
        Orientation = deck?.Orientation ?? 0,
        SleepAfterSeconds = deck?.SleepAfterSeconds ?? 0,
        FirmwareVersion = surface.FirmwareVersion,
        Warning = warning,
        ConflictAppId = conflictAppId,
        CurrentPage = worker.GetCurrentPage(surface.Serial),
        FolderPath = worker.GetFolderPath(surface.Serial).ToList(),
    };

    private static string FormatName(StreamDeckImageFormat format) => format switch
    {
        StreamDeckImageFormat.Bmp => "bmp",
        StreamDeckImageFormat.Jpeg => "jpeg",
        _ => "",
    };

    private static string ElgatoStatusName(ElgatoStoreStatus status) => status switch
    {
        ElgatoStoreStatus.Ok => "ok",
        ElgatoStoreStatus.UnsupportedVersion => "unsupportedVersion",
        _ => "notFound",
    };

    /// <summary>Normalizes any degree value to the nearest cardinal (quarter-turn) rotation, wrapping past a full turn.</summary>
    private static int ClampOrientation(int degrees)
    {
        var normalized = ((degrees % 360) + 360) % 360;
        return normalized switch
        {
            >= 45 and < 135 => 90,
            >= 135 and < 225 => 180,
            >= 225 and < 315 => 270,
            _ => 0,
        };
    }

    /// <summary>The catalog id to kill the contending Elgato app, or null while there is no contention.</summary>
    internal static string? ResolveConflictAppId(string? warning) =>
        warning is not null ? StreamDeckHandler.ElgatoConflictAppId : null;

    private static DeckPresetDto ToPresetDto(DeckPreset p) => new() { Id = p.Id, Name = p.Name };

    /// <summary>
    /// Deep-copies a DeckConfig by round-tripping it through DeckConfigConverter -
    /// the tree nests DeckFolder/DeckSlot/DeckAction/DeckSequenceStep, so a
    /// hand-rolled clone would have to mirror every branch of that converter.
    /// </summary>
    private static DeckConfig DeepCopyDeckConfig(DeckConfig source) =>
        JsonSerializer.Deserialize(
            JsonSerializer.SerializeToUtf8Bytes(source, AppJsonContext.Default.DeckConfig),
            AppJsonContext.Default.DeckConfig)!;

    /// <summary>
    /// True if any live v2-or-back slot (other than excludeKey) or any saved
    /// preset still references hash. A preset snapshots ImageRefs at save
    /// time, so an image hash the live upload route would otherwise evict
    /// can still be the only copy backing an older preset - evicting it
    /// early leaves that preset's keys blank the next time it activates. A
    /// live legacy pre-v2 key never counts, so a hash only a stale orphaned
    /// v1 entry still points at is not pinned alive forever.
    /// </summary>
    private static bool IsHashReferenced(PhysicalDeckSettings deck, string hash, string? excludeKey)
    {
        if (deck.ImageRefs.Any(kv => kv.Key != excludeKey && kv.Value == hash && IsV2OrBackImageRefKey(kv.Key)))
        {
            return true;
        }
        foreach (var preset in deck.Presets)
        {
            if (preset.ImageRefs.Values.Any(v => v == hash))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True for a v2 page-qualified ImageRefs key ("0.3/0") or the reserved
    /// page-independent "back/0" key. False for a legacy pre-v2 key ("3/0"),
    /// which no longer resolves to any slot (StreamDeckConnectionWorker.
    /// ResolveSlotImage only builds v2 keys) and must not keep its hash's
    /// blob alive.
    /// </summary>
    private static bool IsV2OrBackImageRefKey(string key)
    {
        var slashIndex = key.IndexOf('/');
        if (slashIndex < 0)
        {
            return false;
        }
        var slotPath = key[..slashIndex];
        return slotPath == "back" || DeckConfigNavigation.ParseImageRefSlotPath(slotPath) is not null;
    }

    /// <summary>
    /// True if a just-uploaded key's slotPath is part of the deck's current
    /// physical view, so the upload route should trigger a HID repaint. The
    /// web editor now uploads the whole config tree (every page, every
    /// folder) on each edit (image-refs v2), so most uploads target a
    /// page/folder the deck is not currently showing and must not trigger a
    /// full RefreshView - that repaint-per-key cascade was the original
    /// page next/prev latency.
    /// </summary>
    private static bool IsUploadedKeyVisible(string serial, string slotPath, StreamDeckConnectionWorker worker)
    {
        if (slotPath == "back")
        {
            return worker.IsShowingAFolder(serial);
        }
        var parsed = DeckConfigNavigation.ParseImageRefSlotPath(slotPath);
        return parsed is not null && worker.IsCurrentView(serial, parsed.Value.Page, parsed.Value.FolderPath);
    }

    /// <summary>
    /// A valid ImageRefs key is either the reserved "back" folder-back-key
    /// slot with state "0" (StreamDeckConnectionWorker.BackSlotPath, only
    /// ever pushed with state 0), or a real slot path (DeckConfigNavigation's
    /// dot-joined index chain) with state "0" or "1" (off/on for a toggle).
    /// </summary>
    private static bool IsValidImageSlotAndState(string slotPath, string state)
    {
        if (state != "0" && state != "1")
        {
            return false;
        }
        if (slotPath == "back")
        {
            return state == "0";
        }
        return DeckConfigNavigation.ParseSlotPath(slotPath) is not null;
    }

    /// <summary>Reads a request body up to maxBytes, checking the running total after every chunk so a chunked upload (no Content-Length) never buffers unbounded memory before the size check runs.</summary>
    private static async Task<(byte[]? Bytes, bool TooLarge)> ReadBoundedAsync(Stream source, long maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                return (null, true);
            }
            await ms.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return (ms.ToArray(), false);
    }

    // Fixed, visually distinct hues so each key is identifiable on the bench.
    private static readonly (byte r, byte g, byte b)[] TestPatternColors =
    {
        (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0),
        (255, 0, 255), (0, 255, 255), (255, 128, 0), (128, 0, 255),
    };

    /// <summary>
    /// Hand-rolled fixed-header 24bpp BMP builder for the dev-only test
    /// pattern route. Deliberately kept local to this file (not shared with
    /// the production ClearKey path): the service's image pipeline stays
    /// web-rendered per plan streamdeck-support.md §5, so the only
    /// solid-color/arbitrary-size encoder lives behind this bench route,
    /// never a general-purpose capability.
    /// </summary>
    private static byte[] BuildSolidBmp(int width, int height, byte r, byte g, byte b)
    {
        var pixelBytes = width * height * 3;
        var bmp = new byte[54 + pixelBytes];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), height);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(34), (uint)pixelBytes);
        for (var i = 54; i < bmp.Length; i += 3)
        {
            bmp[i] = b;
            bmp[i + 1] = g;
            bmp[i + 2] = r;
        }
        return bmp;
    }
}
