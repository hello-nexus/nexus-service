using System.Linq;
using Nexus.Service.Auth;
using Nexus.Service.Deck;
using Nexus.Service.Models;
using Nexus.Service.Models.Deck;
using Nexus.Service.Persistence;
using Nexus.Service.Peripherals.StreamDeck;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

/// <summary>
/// Host-wide deck preset + instance contract (plan deck-modes.md CONTRACT
/// ADDENDUM). Presets and instances are shared by every physical Stream Deck
/// AND the on-screen Deck widget; a preset edit here repaints every instance
/// that points at it.
/// </summary>
public static class DeckRoutes
{
    private const int PresetCap = 50;

    public static void MapDeckEndpoints(this WebApplication app)
    {
        app.MapGet("/deck/presets", (IConfigStore store) =>
        {
            var presets = store.Load().StreamDeck.Presets.Select(ToSummary).ToList();
            return Results.Json(new DeckPresetsListResponse { Presets = presets }, AppJsonContext.Default.DeckPresetsListResponse);
        }).AllowPanel();

        app.MapGet("/deck/presets/{id}", (string id, IConfigStore store) =>
        {
            var preset = store.Load().StreamDeck.Presets.Find(p => p.Id == id);
            if (preset is null)
            {
                return Results.Json(ApiResponse.Fail("preset not found"), AppJsonContext.Default.ApiResponse, statusCode: 404);
            }
            return Results.Json(new DeckPresetResponse { Preset = ToFull(preset) }, AppJsonContext.Default.DeckPresetResponse);
        }).AllowPanel();

        app.MapPost("/deck/presets", (
            CreateDeckPresetRequest body, HttpContext ctx, TokenService tokens, IConfigStore store, MultiplexHub hub,
            DeckPresetCatalog catalog, Nexus.Service.Activity.IShortcutsProvider shortcuts) =>
        {
            var optionCount = (body.Deck is not null ? 1 : 0) + (body.TemplateId is not null ? 1 : 0) + (body.CopyOfPresetId is not null ? 1 : 0);
            if (optionCount > 1)
            {
                return Results.Json(ApiResponse.Fail("specify at most one of deck, templateId, copyOfPresetId"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            if (body.TemplateId is not null)
            {
                return CreateFromTemplate(body.TemplateId, store, hub, catalog, shortcuts);
            }
            var isDesktopCreate = ServiceTokenRequests.HasServiceToken(ctx, tokens);
            if (body.Deck is not null && !isDesktopCreate
                && DeckLayoutPolicy.IntroducesPrivilegedActions(body.Deck, stored: null))
            {
                return Results.Json(ApiResponse.Fail("deck_action_requires_desktop"), AppJsonContext.Default.ApiResponse, statusCode: 403);
            }
            if (body.CopyOfPresetId is not null && !isDesktopCreate)
            {
                var copySource = store.Load().StreamDeck.Presets.Find(p => p.Id == body.CopyOfPresetId);
                if (copySource is not null && DeckLayoutPolicy.PrivilegedActions(copySource.Deck).Any())
                {
                    return Results.Json(ApiResponse.Fail("deck_action_requires_desktop"), AppJsonContext.Default.ApiResponse, statusCode: 403);
                }
            }

            var trimmedName = (body.Name ?? "").Trim();
            if (trimmedName.Length == 0)
            {
                return Results.Json(ApiResponse.Fail("name is required"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }

            var capped = false;
            var nameTaken = false;
            var copySourceMissing = false;
            DeckPreset? created = null;
            store.Update(s =>
            {
                if (s.StreamDeck.Presets.Count >= PresetCap)
                {
                    capped = true;
                    return;
                }
                if (s.StreamDeck.Presets.Any(p => string.Equals(p.Name, trimmedName, System.StringComparison.OrdinalIgnoreCase)))
                {
                    nameTaken = true;
                    return;
                }

                DeckConfig deck;
                if (body.CopyOfPresetId is not null)
                {
                    var source = s.StreamDeck.Presets.Find(p => p.Id == body.CopyOfPresetId);
                    if (source is null)
                    {
                        copySourceMissing = true;
                        return;
                    }
                    deck = DeckConfigNavigation.DeepCopyConfig(source.Deck);
                }
                else
                {
                    deck = body.Deck is not null ? DeckConfigNavigation.DeepCopyConfig(body.Deck) : DeckConfigNavigation.EmptyConfig();
                }

                created = new DeckPreset
                {
                    Id = DeckModesMigration.NewPresetId(),
                    Name = trimmedName,
                    Cols = System.Math.Clamp(body.Cols, 1, 8),
                    Rows = System.Math.Clamp(body.Rows, 1, 8),
                    Deck = deck,
                };
                s.StreamDeck.Presets.Add(created);
            });

            if (capped)
            {
                return Results.Json(ApiResponse.Fail("Deck preset cap of 50 reached"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            if (nameTaken)
            {
                return Results.Json(ApiResponse.Fail("preset_name_taken"), AppJsonContext.Default.ApiResponse, statusCode: 409);
            }
            if (copySourceMissing)
            {
                return Results.Json(ApiResponse.Fail("copy source preset not found"), AppJsonContext.Default.ApiResponse, statusCode: 404);
            }

            BroadcastPresetsChanged(hub, store);
            return Results.Json(new DeckPresetResponse { Preset = ToFull(created!) }, AppJsonContext.Default.DeckPresetResponse);
        }).AllowPanel();

        app.MapPut("/deck/presets/{id}", (
            string id, UpdateDeckPresetRequest body, HttpContext ctx, TokenService tokens, IConfigStore store, MultiplexHub hub) =>
        {
            var existing = store.Load().StreamDeck.Presets.Find(p => p.Id == id);
            if (existing is null)
            {
                return Results.Json(ApiResponse.Fail("preset not found"), AppJsonContext.Default.ApiResponse, statusCode: 404);
            }
            if (body.Deck is not null && !ServiceTokenRequests.HasServiceToken(ctx, tokens)
                && DeckLayoutPolicy.IntroducesPrivilegedActions(body.Deck, existing.Deck))
            {
                return Results.Json(ApiResponse.Fail("deck_action_requires_desktop"), AppJsonContext.Default.ApiResponse, statusCode: 403);
            }

            var nameTaken = false;
            DeckPreset? updated = null;
            store.Update(s =>
            {
                var p = s.StreamDeck.Presets.Find(x => x.Id == id);
                if (p is null)
                {
                    return;
                }
                if (!string.IsNullOrEmpty(body.Name))
                {
                    var trimmedName = body.Name.Trim();
                    if (trimmedName.Length > 0)
                    {
                        if (s.StreamDeck.Presets.Any(x => x.Id != id && string.Equals(x.Name, trimmedName, System.StringComparison.OrdinalIgnoreCase)))
                        {
                            nameTaken = true;
                            return;
                        }
                        p.Name = trimmedName;
                    }
                }
                if (body.Deck is not null)
                {
                    p.Deck = DeckConfigNavigation.DeepCopyConfig(body.Deck);
                }
                // A preset must always have at least one page for its editor to add keys to.
                if (p.Deck.Pages.Count == 0)
                {
                    p.Deck.Pages.Add(new DeckPage());
                }
                if (body.Cols is not null)
                {
                    p.Cols = System.Math.Clamp(body.Cols.Value, 1, 8);
                }
                if (body.Rows is not null)
                {
                    p.Rows = System.Math.Clamp(body.Rows.Value, 1, 8);
                }
                updated = p;
            });

            if (nameTaken)
            {
                return Results.Json(ApiResponse.Fail("preset_name_taken"), AppJsonContext.Default.ApiResponse, statusCode: 409);
            }
            if (updated is null)
            {
                return Results.Json(ApiResponse.Fail("preset not found"), AppJsonContext.Default.ApiResponse, statusCode: 404);
            }

            PanelTopics.BroadcastDeck(hub, new DeckChangedFrame { Kind = "preset", PresetId = id, Summary = ToSummary(updated), Deck = updated.Deck });
            return Results.Json(new DeckPresetResponse { Preset = ToFull(updated) }, AppJsonContext.Default.DeckPresetResponse);
        }).AllowPanel();

        app.MapDelete("/deck/presets/{id}", (string id, IConfigStore store, MultiplexHub hub) =>
        {
            var lastAndInUse = false;
            var movedInstanceIds = new System.Collections.Generic.List<string>();
            store.Update(s =>
            {
                var preset = s.StreamDeck.Presets.Find(p => p.Id == id);
                if (preset is null)
                {
                    return;
                }
                var inUse = s.StreamDeck.Instances.Values.Any(i => i.ActivePresetId == id);
                if (inUse && s.StreamDeck.Presets.Count == 1)
                {
                    lastAndInUse = true;
                    return;
                }

                s.StreamDeck.Presets.RemoveAll(p => p.Id == id);
                if (inUse && s.StreamDeck.Presets.Count > 0)
                {
                    var fallbackId = s.StreamDeck.Presets[0].Id;
                    foreach (var (instanceId, instance) in s.StreamDeck.Instances)
                    {
                        if (instance.ActivePresetId == id)
                        {
                            instance.ActivePresetId = fallbackId;
                            movedInstanceIds.Add(instanceId);
                        }
                    }
                }
            });

            if (lastAndInUse)
            {
                return Results.Json(ApiResponse.Fail("cannot delete the last preset while an instance is using it"), AppJsonContext.Default.ApiResponse, statusCode: 409);
            }

            BroadcastPresetsChanged(hub, store);
            var settings = store.Load().StreamDeck;
            foreach (var instanceId in movedInstanceIds)
            {
                if (settings.Instances.TryGetValue(instanceId, out var instance))
                {
                    PanelTopics.BroadcastDeck(hub, new DeckChangedFrame { Kind = "active", InstanceId = instanceId, Instance = instance });
                }
            }
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        }).LocalhostOnly();

        // App Aware bindings, mirroring PUT /devices/lighting-devices/layout-presets/{id}/apps
        // exactly: an app drives exactly one preset host-wide (deck presets and
        // layout presets are separate pools), so assigning it here unbinds it
        // from any other DECK preset only.
        app.MapPut("/deck/presets/{id}/apps", (
            string id, Nexus.Service.Models.Devices.SetPresetAppsBody body, IConfigStore store, Nexus.Service.Activity.IShortcutsProvider shortcuts, MultiplexHub hub) =>
        {
            var known = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var preset in store.Load().StreamDeck.Presets)
            {
                if (preset.Apps is null) continue;
                foreach (var b in preset.Apps)
                {
                    if (b.ProcessName.Length > 0) known[b.Id] = b.ProcessName;
                }
            }

            var resolved = new System.Collections.Generic.List<PresetAppBinding>();
            foreach (var app in body.Apps)
            {
                if (string.IsNullOrWhiteSpace(app.Id)
                    || resolved.Exists(r => string.Equals(r.Id, app.Id, System.StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                var processName = ResolveBindingProcessName(app.Id, shortcuts);
                if (processName.Length == 0 && known.TryGetValue(app.Id, out var previous))
                {
                    processName = previous;
                }
                resolved.Add(new PresetAppBinding { Id = app.Id, Name = app.Name, ProcessName = processName });
            }

            var existing = store.Load().StreamDeck.Presets;
            var target = existing.Find(p => p.Id == id);
            if (target is null)
            {
                return Results.Json(ApiResponse.Fail("preset not found"), AppJsonContext.Default.ApiResponse, statusCode: 404);
            }

            foreach (var other in existing)
            {
                if (other.Id == id || other.Apps is null)
                {
                    continue;
                }
                foreach (var taken in other.Apps)
                {
                    var clash = resolved.Find(r =>
                        string.Equals(r.Id, taken.Id, System.StringComparison.OrdinalIgnoreCase)
                        || (r.ProcessName.Length > 0 && string.Equals(r.ProcessName, taken.ProcessName, System.StringComparison.Ordinal)));
                    if (clash is not null)
                    {
                        return Results.Json(
                            new Nexus.Service.Models.Devices.PresetAppConflictResponse { Error = true, Msg = "app_already_bound", AppName = clash.Name, PresetName = other.Name },
                            AppJsonContext.Default.PresetAppConflictResponse,
                            statusCode: 409);
                    }
                }
            }

            DeckPreset? updated = null;
            store.Update(s =>
            {
                var preset = s.StreamDeck.Presets.Find(p => p.Id == id);
                if (preset is null)
                {
                    return;
                }
                preset.Apps = resolved;
                updated = preset;
            });
            if (updated is null)
            {
                return Results.Json(ApiResponse.Fail("preset not found"), AppJsonContext.Default.ApiResponse, statusCode: 404);
            }

            PanelTopics.BroadcastDeck(hub, new DeckChangedFrame { Kind = "preset", PresetId = id, Summary = ToSummary(updated), Deck = updated.Deck });
            return Results.Json(new DeckPresetAppsResponse { Preset = ToSummary(updated) }, AppJsonContext.Default.DeckPresetAppsResponse);
        }).LocalhostOnly();

        app.MapGet("/deck/presets/{id}/export", (string id, IConfigStore store, DeckImageStore images) =>
        {
            var preset = store.Load().StreamDeck.Presets.Find(p => p.Id == id);
            if (preset is null)
            {
                return Results.Json(ApiResponse.Fail("preset not found"), AppJsonContext.Default.ApiResponse, statusCode: 404);
            }
            var zipBytes = DeckPresetPackage.Write(preset, images);
            return Results.File(zipBytes, "application/zip", SafeExportFileName(preset.Name) + ".nexus-deck");
        }).LocalhostOnly();

        app.MapPost("/deck/presets/import", async (
            HttpContext ctx, IConfigStore store, MultiplexHub hub, DeckImageStore images, DeckPresetCatalog catalog) =>
        {
            var bodySize = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (bodySize is { IsReadOnly: false })
            {
                bodySize.MaxRequestBodySize = DeckPresetPackage.MaxPackageBytes;
            }

            using var buffered = new System.IO.MemoryStream();
            try
            {
                await ctx.Request.Body.CopyToAsync(buffered, ctx.RequestAborted);
            }
            catch (System.Exception e) when (e is not System.OperationCanceledException)
            {
                return Results.Json(ApiResponse.Fail("package exceeds the 20 MB size limit"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            buffered.Position = 0;

            DeckPackageReadResult read;
            try
            {
                read = DeckPresetPackage.Read(new ZipPackageSource(buffered));
            }
            catch (System.IO.InvalidDataException)
            {
                return Results.Json(ApiResponse.Fail("not a valid .nexus-deck package"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            if (!read.Ok)
            {
                return Results.Json(ApiResponse.Fail(read.Error!), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            var manifest = read.Manifest!;

            var allowPrivileged = ctx.Request.Query["allowPrivileged"] == "1";
            if (!allowPrivileged && DeckLayoutPolicy.PrivilegedActions(manifest.Deck).Any())
            {
                return Results.Json(ApiResponse.Fail("package contains privileged actions"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }

            var trimmedName = manifest.Name.Trim();
            if (trimmedName.Length == 0)
            {
                return Results.Json(ApiResponse.Fail("preset.json is missing name"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }

            var capped = false;
            var nameTaken = false;
            DeckPreset? created = null;
            store.Update(s =>
            {
                if (s.StreamDeck.Presets.Count >= PresetCap)
                {
                    capped = true;
                    return;
                }
                if (s.StreamDeck.Presets.Any(p => string.Equals(p.Name, trimmedName, System.StringComparison.OrdinalIgnoreCase)))
                {
                    nameTaken = true;
                    return;
                }
                created = new DeckPreset
                {
                    Id = DeckModesMigration.NewPresetId(),
                    Name = trimmedName,
                    Cols = System.Math.Clamp(manifest.Cols, 1, 8),
                    Rows = System.Math.Clamp(manifest.Rows, 1, 8),
                    Deck = DeckConfigNavigation.DeepCopyConfig(manifest.Deck),
                    TemplateId = catalog.Open(manifest.Id) is not null ? manifest.Id : null,
                    Author = manifest.Author,
                    Version = manifest.Version,
                    Description = manifest.Description,
                };
                s.StreamDeck.Presets.Add(created);
            });

            if (capped)
            {
                return Results.Json(ApiResponse.Fail("Deck preset cap of 50 reached"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            if (nameTaken)
            {
                return Results.Json(ApiResponse.Fail("preset_name_taken"), AppJsonContext.Default.ApiResponse, statusCode: 409);
            }

            foreach (var (assetId, asset) in read.Assets)
            {
                images.StoreValidated(assetId, asset.Ext, asset.Bytes);
            }

            BroadcastPresetsChanged(hub, store);
            return Results.Json(new DeckPresetResponse { Preset = ToFull(created!) }, AppJsonContext.Default.DeckPresetResponse);
        }).LocalhostOnly();

        app.MapGet("/deck/templates", (DeckPresetCatalog catalog, Nexus.Service.Activity.IShortcutsProvider shortcuts) =>
        {
            var installed = shortcuts.GetAll();
            var templates = catalog.Templates.Select(t => ResolveTemplate(t, installed)).ToList();
            return Results.Json(new DeckTemplatesListResponse { Templates = templates }, AppJsonContext.Default.DeckTemplatesListResponse);
        }).LocalhostOnly();

        app.MapGet("/deck/instances", (IConfigStore store) =>
        {
            var instances = new System.Collections.Generic.Dictionary<string, DeckInstance>(store.Load().StreamDeck.Instances);
            return Results.Json(new DeckInstancesResponse { Instances = instances }, AppJsonContext.Default.DeckInstancesResponse);
        }).LocalhostOnly();

        // A widget passes its own grid so a first-time instance gets a preset
        // authored at that size instead of the 2x2 fallback.
        app.MapGet("/deck/instances/{id}", (string id, int? cols, int? rows, IConfigStore store) =>
        {
            if (!IsValidInstanceId(id))
            {
                return Results.Json(ApiResponse.Fail("invalid instance id"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            var settings = store.Load().StreamDeck;
            if (settings.Instances.TryGetValue(id, out var existing))
            {
                return Results.Json(new DeckInstanceResponse { Instance = existing }, AppJsonContext.Default.DeckInstanceResponse);
            }
            if (!id.StartsWith("streamdeck:", System.StringComparison.Ordinal))
            {
                var lazy = CreateLazyWidgetInstance(store, id, cols, rows);
                return Results.Json(new DeckInstanceResponse { Instance = lazy }, AppJsonContext.Default.DeckInstanceResponse);
            }
            return Results.Json(ApiResponse.Fail("instance not found"), AppJsonContext.Default.ApiResponse, statusCode: 404);
        }).AllowPanel();

        app.MapPut("/deck/instances/{id}", (
            string id, UpdateDeckInstanceRequest body, HttpContext ctx, TokenService tokens, IConfigStore store, DeckPresetActivator activator) =>
        {
            if (!IsValidInstanceId(id))
            {
                return Results.Json(ApiResponse.Fail("invalid instance id"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            if (body.Mode is not null && body.Mode is not ("fixed" or "recentApps" or "appAware"))
            {
                return Results.Json(ApiResponse.Fail("invalid mode"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }

            // A physical deck is workstation hardware, not a panel's own
            // surface: only the desktop app may retarget one.
            var isDesktop = ServiceTokenRequests.HasServiceToken(ctx, tokens);
            if (!isDesktop && id.StartsWith("streamdeck:", System.StringComparison.Ordinal))
            {
                return Results.Json(ApiResponse.Fail("deck_action_requires_desktop"), AppJsonContext.Default.ApiResponse, statusCode: 403);
            }

            if (body.ActivePresetId is not null)
            {
                var target = store.Load().StreamDeck.Presets.Find(p => p.Id == body.ActivePresetId);
                if (target is null)
                {
                    return Results.Json(ApiResponse.Fail("preset not found"), AppJsonContext.Default.ApiResponse, statusCode: 404);
                }
                // A panel session must not point its own widget at a preset
                // carrying desktop-authored privileged keys (file/hotkey/text/
                // audio) - /panel/deck/dispatch executes whatever the resolved
                // slot holds with no dispatch-time policy check of its own, so
                // this reassignment is the only gate standing between a panel
                // and code execution as the console user.
                if (!isDesktop && DeckLayoutPolicy.PrivilegedActions(target.Deck).Any())
                {
                    return Results.Json(ApiResponse.Fail("deck_action_requires_desktop"), AppJsonContext.Default.ApiResponse, statusCode: 403);
                }
            }

            var result = activator.Activate(id, body.ActivePresetId, body.Mode);
            return Results.Json(new DeckInstanceResponse { Instance = result }, AppJsonContext.Default.DeckInstanceResponse);
        }).AllowPanel();

        // RecentAppsState is the live ring - settings.json trails it by up to
        // RecentAppsService's persist interval, so every recent-apps route
        // reads/writes through the state, never store.Load().StreamDeck.RecentApps*
        // directly.
        app.MapGet("/deck/recent-apps", (RecentAppsState state) =>
        {
            return Results.Json(new RecentAppsResponse
            {
                Apps = state.RingSnapshot(),
                Excluded = state.ExcludedSnapshot(),
                FocusedProcessKey = state.FocusedProcessKey,
            }, AppJsonContext.Default.RecentAppsResponse);
        }).AllowPanel();

        app.MapPut("/deck/recent-apps/excluded", (SetRecentAppsExcludedRequest body, MultiplexHub hub, RecentAppsState state, RecentAppsService recentApps) =>
        {
            var excluded = body.ProcessKeys.ConvertAll(Nexus.Service.Lighting.AppPresetMatching.ProcessKey);
            // A key excluded after it already entered the ring disappears
            // immediately rather than lingering until its next MRU update.
            state.SetExcluded(excluded);
            recentApps.Persist(force: true);
            BroadcastRecentsChanged(hub, state);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        }).LocalhostOnly();

        app.MapDelete("/deck/recent-apps", (MultiplexHub hub, RecentAppsState state, RecentAppsService recentApps) =>
        {
            state.Clear();
            recentApps.Persist(force: true);
            BroadcastRecentsChanged(hub, state);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        }).LocalhostOnly();

        app.MapPost("/deck/recent-apps/activate", async (
            ActivateRecentAppRequest body, RecentAppsState state, RecentAppsActivator activator) =>
        {
            var processKey = body.ProcessKey ?? "";
            if (processKey.Length == 0 || processKey == state.FocusedProcessKey)
            {
                return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
            }
            var entry = state.RingSnapshot().Find(a => a.ProcessKey == processKey);
            if (entry is null)
            {
                return Results.Json(ApiResponse.Fail("app not in the recent apps ring"), AppJsonContext.Default.ApiResponse, statusCode: 404);
            }
            await activator.ActivateAsync(entry).ConfigureAwait(false);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        }).AllowPanel();
    }

    private static void BroadcastRecentsChanged(MultiplexHub hub, RecentAppsState state)
    {
        PanelTopics.BroadcastDeck(hub, new DeckChangedFrame
        {
            Kind = "recents",
            Apps = state.RingSnapshot(),
            FocusedProcessKey = state.FocusedProcessKey,
        });
    }

    private static void BroadcastPresetsChanged(MultiplexHub hub, IConfigStore store)
    {
        var presets = store.Load().StreamDeck.Presets.Select(ToSummary).ToList();
        PanelTopics.BroadcastDeck(hub, new DeckChangedFrame { Kind = "presets", Presets = presets });
    }

    /// <summary>
    /// A new widget joins the first existing preset (adding a deck is like
    /// plugging in a second Stream Deck, not starting a fresh layout); only a
    /// host with no presets at all gets a fresh empty one at the widget's grid.
    /// </summary>
    private static DeckInstance CreateLazyWidgetInstance(IConfigStore store, string instanceId, int? cols, int? rows)
    {
        DeckInstance? created = null;
        store.Update(s =>
        {
            if (s.StreamDeck.Instances.TryGetValue(instanceId, out var already))
            {
                created = already;
                return;
            }
            var presetId = s.StreamDeck.Presets.Count > 0 ? s.StreamDeck.Presets[0].Id : null;
            if (presetId is null)
            {
                var preset = new DeckPreset
                {
                    Id = DeckModesMigration.NewPresetId(),
                    Name = DeckModesMigration.UniqueName(s.StreamDeck.Presets, "Deck", "widget"),
                    Cols = System.Math.Clamp(cols ?? 2, 1, 8),
                    Rows = System.Math.Clamp(rows ?? 2, 1, 8),
                    Deck = DeckConfigNavigation.EmptyConfig(),
                };
                s.StreamDeck.Presets.Add(preset);
                presetId = preset.Id;
            }
            created = new DeckInstance { Mode = "fixed", ActivePresetId = presetId };
            s.StreamDeck.Instances[instanceId] = created;
        });
        return created!;
    }

    /// <summary>"streamdeck:&lt;serial per StreamDeckImageCache.IsValidSerial&gt;" or "widget:&lt;[A-Za-z0-9_-]{1,64}&gt;".</summary>
    private static bool IsValidInstanceId(string id)
    {
        if (id.StartsWith("streamdeck:", System.StringComparison.Ordinal))
        {
            return StreamDeckImageCache.IsValidSerial(id["streamdeck:".Length..]);
        }
        if (id.StartsWith("widget:", System.StringComparison.Ordinal))
        {
            var widgetId = id["widget:".Length..];
            if (widgetId.Length is 0 or > 64)
            {
                return false;
            }
            foreach (var ch in widgetId)
            {
                // ASCII only, matching the [A-Za-z0-9_-] contract exactly -
                // char.IsLetterOrDigit is Unicode-aware and would accept
                // characters the contract does not.
                var isAsciiLetterOrDigit = (ch is >= 'a' and <= 'z') || (ch is >= 'A' and <= 'Z') || (ch is >= '0' and <= '9');
                if (!isAsciiLetterOrDigit && ch != '-' && ch != '_')
                {
                    return false;
                }
            }
            return true;
        }
        return false;
    }

    // Mirrors LightingDevicesRoutes' helper of the same name: a pick off the
    // running-apps list already carries the process name, a Start-menu pick
    // does not.
    private static string ResolveBindingProcessName(string appId, Nexus.Service.Activity.IShortcutsProvider shortcuts)
    {
        const string RunningPrefix = "proc:";
        if (appId.StartsWith(RunningPrefix, System.StringComparison.Ordinal))
        {
            return Nexus.Service.Lighting.AppPresetMatching.ProcessKey(appId[RunningPrefix.Length..]);
        }
        try
        {
            return Nexus.Service.Lighting.AppPresetMatching.ProcessKey(shortcuts.ResolveProcessName(appId));
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Copies a bundled template into a new host-wide preset: name unique-suffixed on collision (DeckModesMigration's numbered form, since a template has no natural device-name grouping to parenthesize), apps pre-bound to the resolved installed app when one exists and is not already bound to another preset.</summary>
    private static Microsoft.AspNetCore.Http.IResult CreateFromTemplate(
        string templateId, IConfigStore store, MultiplexHub hub, DeckPresetCatalog catalog, Nexus.Service.Activity.IShortcutsProvider shortcuts)
    {
        var template = catalog.Open(templateId);
        if (template is null)
        {
            return Results.Json(ApiResponse.Fail("template not found"), AppJsonContext.Default.ApiResponse, statusCode: 404);
        }
        var manifest = template.Manifest!;
        var installedApp = ResolveInstalledApp(manifest.Match, shortcuts.GetAll());

        var capped = false;
        DeckPreset? created = null;
        store.Update(s =>
        {
            if (s.StreamDeck.Presets.Count >= PresetCap)
            {
                capped = true;
                return;
            }
            var name = s.StreamDeck.Presets.Any(p => string.Equals(p.Name, manifest.Name, System.StringComparison.OrdinalIgnoreCase))
                ? DeckModesMigration.EnsureUnique(s.StreamDeck.Presets, manifest.Name)
                : manifest.Name;

            System.Collections.Generic.List<PresetAppBinding>? apps = null;
            if (installedApp is not null)
            {
                var binding = new PresetAppBinding { Id = installedApp.Id, Name = installedApp.Name, ProcessName = installedApp.ProcessName };
                if (FindAppBindingConflict(s.StreamDeck.Presets, binding) is null)
                {
                    apps = new System.Collections.Generic.List<PresetAppBinding> { binding };
                }
            }

            created = new DeckPreset
            {
                Id = DeckModesMigration.NewPresetId(),
                Name = name,
                Cols = manifest.Cols,
                Rows = manifest.Rows,
                Deck = DeckConfigNavigation.DeepCopyConfig(manifest.Deck),
                Apps = apps,
                TemplateId = templateId,
                Author = manifest.Author,
                Version = manifest.Version,
                Description = manifest.Description,
            };
            s.StreamDeck.Presets.Add(created);
        });

        if (capped)
        {
            return Results.Json(ApiResponse.Fail("Deck preset cap of 50 reached"), AppJsonContext.Default.ApiResponse, statusCode: 400);
        }

        BroadcastPresetsChanged(hub, store);
        return Results.Json(new DeckPresetResponse { Preset = ToFull(created!) }, AppJsonContext.Default.DeckPresetResponse);
    }

    /// <summary>The first other preset's app binding that would clash with candidate (same app id, or same non-empty process name), mirroring PUT /deck/presets/{id}/apps' conflict check.</summary>
    private static PresetAppBinding? FindAppBindingConflict(System.Collections.Generic.List<DeckPreset> presets, PresetAppBinding candidate)
    {
        foreach (var other in presets)
        {
            if (other.Apps is null)
            {
                continue;
            }
            foreach (var taken in other.Apps)
            {
                if (string.Equals(taken.Id, candidate.Id, System.StringComparison.OrdinalIgnoreCase)
                    || (candidate.ProcessName.Length > 0 && string.Equals(candidate.ProcessName, taken.ProcessName, System.StringComparison.Ordinal)))
                {
                    return taken;
                }
            }
        }
        return null;
    }

    private static DeckTemplateDto ResolveTemplate(DeckTemplateSummary t, System.Collections.Generic.IReadOnlyList<Nexus.Service.Models.Activity.Shortcut> installed)
    {
        var dto = new DeckTemplateDto
        {
            Id = t.Id,
            Name = t.Name,
            Description = t.Description,
            Cols = t.Cols,
            Rows = t.Rows,
            Match = t.Match,
            PageCount = t.PageCount,
        };
        var app = ResolveInstalledApp(t.Match, installed);
        if (app is not null)
        {
            dto.InstalledAppId = app.Id;
            dto.InstalledAppName = app.Name;
            dto.ProcessName = app.ProcessName;
        }
        return dto;
    }

    /// <summary>Process-name match against Shortcut.ProcessName first, then a display-name fallback - the same two-phase shape AppPresetMatching.Matches uses for one binding at a time.</summary>
    private static Nexus.Service.Models.Activity.Shortcut? ResolveInstalledApp(DeckPackageMatch? match, System.Collections.Generic.IReadOnlyList<Nexus.Service.Models.Activity.Shortcut> installed)
    {
        if (match is null)
        {
            return null;
        }
        var processKeys = (match.ProcessNames ?? new()).Select(Nexus.Service.Lighting.AppPresetMatching.ProcessKey).Where(k => k.Length > 0).ToHashSet();
        if (processKeys.Count > 0)
        {
            var byProcess = installed.FirstOrDefault(s => s.ProcessName.Length > 0 && processKeys.Contains(Nexus.Service.Lighting.AppPresetMatching.ProcessKey(s.ProcessName)));
            if (byProcess is not null)
            {
                return byProcess;
            }
        }
        var displayKeys = (match.DisplayNames ?? new()).Select(Nexus.Service.Lighting.AppPresetMatching.DisplayKey).Where(k => k.Length > 0).ToHashSet();
        if (displayKeys.Count == 0)
        {
            return null;
        }
        return installed.FirstOrDefault(s => displayKeys.Contains(Nexus.Service.Lighting.AppPresetMatching.DisplayKey(s.Name)));
    }

    /// <summary>Strips control/reserved filename characters from a preset name for a Content-Disposition download name; "preset" if nothing usable remains.</summary>
    private static string SafeExportFileName(string presetName)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var chars = presetName.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] < 0x20 || System.Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }
        var safe = new string(chars).Trim('.', ' ');
        if (safe.Length == 0)
        {
            return "preset";
        }
        return safe.Length > 100 ? safe[..100] : safe;
    }

    private static DeckPresetSummary ToSummary(DeckPreset p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Cols = p.Cols,
        Rows = p.Rows,
        Apps = p.Apps,
        TemplateId = p.TemplateId,
        PageCount = p.Deck.Pages.Count,
    };

    private static DeckPresetFull ToFull(DeckPreset p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Cols = p.Cols,
        Rows = p.Rows,
        Apps = p.Apps,
        TemplateId = p.TemplateId,
        PageCount = p.Deck.Pages.Count,
        Deck = p.Deck,
    };
}
