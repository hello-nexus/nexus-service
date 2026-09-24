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
                    SleepWhenLocked = deck.SleepWhenLocked,
                    FirmwareVersion = "",
                    Warning = null,
                    ConflictAppId = null,
                    CurrentPage = worker.GetCurrentPage(serial),
                    FolderPath = worker.GetFolderPath(serial).ToList(),
                    InstanceId = DeckInstanceResolver.PhysicalInstanceId(serial),
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
                if (body.SleepWhenLocked is not null)
                {
                    deck.SleepWhenLocked = body.SleepWhenLocked.Value;
                }
            });
            // Skipped while the deck is asleep (sleep-after or the session
            // lock blanked it): the new value already persisted above and
            // applies the moment the deck wakes.
            if (body.Brightness is not null)
            {
                worker.SetBrightnessIfAwake(serial, Math.Clamp(body.Brightness.Value, 0, 100));
            }
            PanelTopics.BroadcastStreamDeck(hub, new StreamDeckChangedFrame { Kind = "decks", Serial = serial });
            return ApiResponse.Ok();
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

        app.MapPost("/streamdeck/decks/{serial}/test-press/{slotPath}", (
            string serial, string slotPath, StreamDeckConnectionWorker worker, IConfigStore store) =>
        {
            var indices = DeckConfigNavigation.ParseSlotPath(slotPath);
            if (indices is null)
            {
                return ApiResponse.Fail("invalid slot path");
            }
            var settings = store.Load().StreamDeck;
            var preset = DeckInstanceResolver.ResolveActivePreset(settings, DeckInstanceResolver.PhysicalInstanceId(serial));
            if (preset is null)
            {
                return ApiResponse.Fail("deck not found");
            }
            // A disconnected deck with no resolvable model still test-presses
            // against its own authored grid (FitToGrid is then an identity copy).
            var grid = ResolvePhysicalGrid(serial, worker, store) ?? (preset.Cols, preset.Rows);
            var config = DeckConfigNavigation.FitToGrid(preset.Cols, preset.Rows, preset.Deck, grid.Cols, grid.Rows, DeckTargetKind.Physical);

            // Routes through the same nav-or-dispatch decision a real key
            // press makes (HandleSlotAction), so testing a page or folder
            // slot navigates the tracked deck state exactly like pressing
            // the physical key would, not just the leaf-action executor.
            return worker.SimulatePress(serial, indices, config)
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

        // Elgato Stream Deck profile import: read-only against the local
        // Elgato software's own store. Never persists anything - the caller
        // decides whether to save the returned config as a preset via
        // POST /deck/presets with its Deck field.
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
            StreamDeckConnectionWorker worker, IConfigStore store) =>
        {
            worker.ClearSimulatedModel();
            // A simulated deck is ephemeral, but interacting with it (nav,
            // instance activation) persists a `sim-<pid>` deck record and
            // instance row. Left behind, GET /streamdeck/decks re-lists it as
            // an offline deck, so the sim never fully disconnects. Purge
            // every sim- record on disconnect; the preset(s) it pointed at
            // are host-wide and shared, so they are left alone.
            store.Update(s =>
            {
                foreach (var serial in s.StreamDeck.Decks.Keys.Where(k => k.StartsWith("sim-", StringComparison.Ordinal)).ToList())
                {
                    s.StreamDeck.Decks.Remove(serial);
                    s.StreamDeck.Instances.Remove(DeckInstanceResolver.PhysicalInstanceId(serial));
                }
            });
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
        SleepWhenLocked = deck?.SleepWhenLocked ?? true,
        FirmwareVersion = surface.FirmwareVersion,
        Warning = warning,
        ConflictAppId = conflictAppId,
        CurrentPage = worker.GetCurrentPage(surface.Serial),
        FolderPath = worker.GetFolderPath(surface.Serial).ToList(),
        InstanceId = DeckInstanceResolver.PhysicalInstanceId(surface.Serial),
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

    /// <summary>The persisted or live grid dimensions for a serial, or null when neither a live surface nor a persisted ProductId resolves a model.</summary>
    private static (int Cols, int Rows)? ResolvePhysicalGrid(string serial, StreamDeckConnectionWorker worker, IConfigStore store)
    {
        var model = worker.FindBySerial(serial)?.Model;
        if (model is null && store.Load().StreamDeck.Decks.TryGetValue(serial, out var deck))
        {
            model = StreamDeckModels.ByProductId(deck.ProductId);
        }
        return model is null ? null : (model.Columns, model.Rows);
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
