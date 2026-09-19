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
/// that points at it. Phase 0 only: apps binding, export/import, templates
/// and recent-apps stay unmapped (404) until their phases land.
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
            CreateDeckPresetRequest body, HttpContext ctx, TokenService tokens, IConfigStore store, MultiplexHub hub) =>
        {
            var optionCount = (body.Deck is not null ? 1 : 0) + (body.TemplateId is not null ? 1 : 0) + (body.CopyOfPresetId is not null ? 1 : 0);
            if (optionCount > 1)
            {
                return Results.Json(ApiResponse.Fail("specify at most one of deck, templateId, copyOfPresetId"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            if (body.TemplateId is not null)
            {
                return Results.Json(ApiResponse.Fail("templates are not available yet"), AppJsonContext.Default.ApiResponse, statusCode: 501);
            }
            if (body.Deck is not null && !ServiceTokenRequests.HasServiceToken(ctx, tokens)
                && DeckLayoutPolicy.IntroducesPrivilegedActions(body.Deck, stored: null))
            {
                return Results.Json(ApiResponse.Fail("deck_action_requires_desktop"), AppJsonContext.Default.ApiResponse, statusCode: 403);
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
                    deck = body.Deck is not null ? DeckConfigNavigation.DeepCopyConfig(body.Deck) : new DeckConfig();
                }

                created = new DeckPreset
                {
                    Id = DeckModesMigration.NewPresetId(),
                    Name = trimmedName,
                    Cols = System.Math.Max(1, body.Cols),
                    Rows = System.Math.Max(1, body.Rows),
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
                if (body.Cols is not null)
                {
                    p.Cols = System.Math.Max(1, body.Cols.Value);
                }
                if (body.Rows is not null)
                {
                    p.Rows = System.Math.Max(1, body.Rows.Value);
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
            string id, UpdateDeckInstanceRequest body, IConfigStore store, MultiplexHub hub, StreamDeckConnectionWorker worker) =>
        {
            if (!IsValidInstanceId(id))
            {
                return Results.Json(ApiResponse.Fail("invalid instance id"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            if (body.Mode is not null && body.Mode is not ("fixed" or "recentApps" or "appAware"))
            {
                return Results.Json(ApiResponse.Fail("invalid mode"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            if (body.ActivePresetId is not null && !store.Load().StreamDeck.Presets.Any(p => p.Id == body.ActivePresetId))
            {
                return Results.Json(ApiResponse.Fail("preset not found"), AppJsonContext.Default.ApiResponse, statusCode: 404);
            }

            DeckInstance? result = null;
            store.Update(s =>
            {
                if (!s.StreamDeck.Instances.TryGetValue(id, out var instance))
                {
                    instance = new DeckInstance();
                    s.StreamDeck.Instances[id] = instance;
                }
                if (body.Mode is not null)
                {
                    instance.Mode = body.Mode;
                }
                if (body.ActivePresetId is not null)
                {
                    instance.ActivePresetId = body.ActivePresetId;
                }
                result = instance;
            });

            if (id.StartsWith("streamdeck:", System.StringComparison.Ordinal))
            {
                var serial = id["streamdeck:".Length..];
                worker.SetNav(serial, 0, System.Array.Empty<int>());
                PanelTopics.BroadcastStreamDeck(hub, new Nexus.Service.Models.Peripherals.StreamDeck.StreamDeckChangedFrame { Kind = "config", Serial = serial });
            }

            PanelTopics.BroadcastDeck(hub, new DeckChangedFrame { Kind = "active", InstanceId = id, Instance = result! });
            return Results.Json(new DeckInstanceResponse { Instance = result! }, AppJsonContext.Default.DeckInstanceResponse);
        }).AllowPanel();

        app.MapGet("/deck/recent-apps", (IConfigStore store, RecentAppsState state) =>
        {
            var settings = store.Load().StreamDeck;
            return Results.Json(new RecentAppsResponse
            {
                Apps = new System.Collections.Generic.List<Nexus.Service.Persistence.RecentApp>(settings.RecentApps),
                Excluded = new System.Collections.Generic.List<string>(settings.RecentAppsExcluded),
                FocusedProcessKey = state.FocusedProcessKey,
            }, AppJsonContext.Default.RecentAppsResponse);
        }).AllowPanel();

        app.MapPut("/deck/recent-apps/excluded", (SetRecentAppsExcludedRequest body, IConfigStore store, MultiplexHub hub, RecentAppsState state) =>
        {
            var excluded = body.ProcessKeys.ConvertAll(Nexus.Service.Lighting.AppPresetMatching.ProcessKey);
            store.Update(s =>
            {
                s.StreamDeck.RecentAppsExcluded = excluded;
                // A key excluded after it already entered the ring disappears
                // immediately rather than lingering until its next MRU update.
                s.StreamDeck.RecentApps.RemoveAll(a => excluded.Contains(a.ProcessKey));
            });
            BroadcastRecentsChanged(hub, store, state);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        }).LocalhostOnly();

        app.MapDelete("/deck/recent-apps", (IConfigStore store, MultiplexHub hub, RecentAppsState state) =>
        {
            store.Update(s => s.StreamDeck.RecentApps.Clear());
            BroadcastRecentsChanged(hub, store, state);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        }).LocalhostOnly();

        app.MapPost("/deck/recent-apps/activate", async (
            ActivateRecentAppRequest body, IConfigStore store, RecentAppsState state, RecentAppsActivator activator) =>
        {
            var processKey = body.ProcessKey ?? "";
            if (processKey.Length == 0 || processKey == state.FocusedProcessKey)
            {
                return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
            }
            var entry = store.Load().StreamDeck.RecentApps.Find(a => a.ProcessKey == processKey);
            if (entry is null)
            {
                return Results.Json(ApiResponse.Fail("app not in the recent apps ring"), AppJsonContext.Default.ApiResponse, statusCode: 404);
            }
            await activator.ActivateAsync(entry).ConfigureAwait(false);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        }).AllowPanel();
    }

    private static void BroadcastRecentsChanged(MultiplexHub hub, IConfigStore store, RecentAppsState state)
    {
        var settings = store.Load().StreamDeck;
        PanelTopics.BroadcastDeck(hub, new DeckChangedFrame
        {
            Kind = "recents",
            RecentApps = new System.Collections.Generic.List<Nexus.Service.Persistence.RecentApp>(settings.RecentApps),
            FocusedProcessKey = state.FocusedProcessKey,
        });
    }

    private static void BroadcastPresetsChanged(MultiplexHub hub, IConfigStore store)
    {
        var presets = store.Load().StreamDeck.Presets.Select(ToSummary).ToList();
        PanelTopics.BroadcastDeck(hub, new DeckChangedFrame { Kind = "presets", Presets = presets });
    }

    /// <summary>Creates and persists a fresh empty "Deck" preset + fixed instance for a widget id seen for the first time, so a Deck widget added after settings load still resolves an instance.</summary>
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
                if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_')
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
