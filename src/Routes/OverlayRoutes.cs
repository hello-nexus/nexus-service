using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Auth;
using Nexus.Service.Models;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel.Streams;
using Nexus.Service.Panel;
using Nexus.Service.Models.Displays;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

/// <summary>
/// Floating desktop widget endpoints. State is profile-scoped under
/// <c>OverlaySettings.Layout</c>; mutations broadcast the existing
/// <c>prefs</c> topic so the desktop host (and any open SPA tab) refetches.
/// </summary>
public static class OverlayRoutes
{
    // Defaults assume the SPA's 86-px desktop cell on a 1920x1200 monitor:
    // floor(1920/86) = 22, floor(1200/86) = 13. Sizing here only affects
    // first-free-cell auto-placement; the SPA still owns runtime cell px.
    private const int DefaultGridCols = 22;
    private const int DefaultGridRows = 13;

    // Whitelist of widget sizes the panel engine renders. Anything else
    // would poison the persisted layout - the SPA falls back to "2x2"
    // visually but the bogus string round-trips on every save.
    private static readonly HashSet<string> AllowedSizes = new(StringComparer.Ordinal)
    {
        "1x1", "2x2", "2x4", "4x2", "4x4",
    };

    private static string NormalizeSize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "2x2";
        return AllowedSizes.Contains(raw) ? raw : "2x2";
    }

    public static void MapOverlayEndpoints(this WebApplication app)
    {
        // Everything nexus-overlay reconciles against, in one read: a failed
        // read must leave the host's kiosks and widgets exactly as they are,
        // which per-setting reads could not express.
        app.MapGet("/overlay/state", (HttpContext ctx, IConfigStore store, PanelDeviceRegistry registry, StreamedPanelCoordinator streams, TokenService tokens) =>
        {
            if (!ServiceTokenRequests.HasServiceToken(ctx, tokens))
                return Results.Unauthorized();
            var s = store.Load();
            var state = new OverlayStateResponse
            {
                AutoLaunch = s.Panel.AutoLaunch,
                ReserveMonitor = s.Panel.ReserveMonitor,
                Y70Backdrop = registry.GetY70Backdrop(),
                Y70CompatibilityRendering = s.Y70.CompatibilityRendering,
                OverlayEnabled = s.Overlay.Enabled,
                AlwaysOnTop = s.Overlay.AlwaysOnTop,
                Monitor = s.Overlay.Monitor,
                Pinned = s.Overlay.Layout.Count,
                Streams = streams.GetAssignments().Assignments,
            };
            foreach (var (displayId, panelDeviceId, reserveMonitor, backdrop) in registry.ListAssignments())
            {
                state.Assignments.Add(new DisplayAssignmentDto
                {
                    DisplayId = displayId,
                    PanelDeviceId = panelDeviceId,
                    ReserveMonitor = reserveMonitor,
                    Backdrop = backdrop,
                });
            }
            return Results.Json(state, AppJsonContext.Default.OverlayStateResponse);
        });

        app.MapGet("/overlay/widgets", (IConfigStore store) =>
        {
            return Results.Json(
                store.Load().Overlay.Layout,
                AppJsonContext.Default.ListOverlayWidgetDto);
        }).AllowPanel();

        app.MapPost("/overlay/widgets", (OverlayWidgetCreateBody body, IConfigStore store, ProfileManager pm, MultiplexHub hub) =>
        {
            if (string.IsNullOrWhiteSpace(body.Type))
                return Results.BadRequest(ApiResponse.Fail("type is required"));

            var entry = new OverlayWidgetDto
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = body.Type,
                Size = NormalizeSize(body.Size),
                Monitor = body.Monitor ?? 0,
                Config = body.Config,
            };

            store.Update(s =>
            {
                double col, row;
                if (body.Col.HasValue && body.Row.HasValue)
                {
                    col = body.Col.Value;
                    row = body.Row.Value;
                }
                else
                {
                    var (c, r) = FindFirstFreeCell(
                        s.Overlay.Layout, entry.Monitor,
                        SizeWidth(entry.Size), SizeHeight(entry.Size));
                    col = c;
                    row = r;
                }
                entry.Col = col;
                entry.Row = row;
                s.Overlay.Layout.Add(entry);

                if (!s.Overlay.Enabled)
                    s.Overlay.Enabled = true;
            });
            pm.MarkDirty();
            PanelTopics.BroadcastPrefs(hub);

            return Results.Json(entry, AppJsonContext.Default.OverlayWidgetDto);
        }).AllowPanel();

        app.MapPatch("/overlay/widgets/{id}", (string id, OverlayWidgetPatch body, IConfigStore store, ProfileManager pm, MultiplexHub hub) =>
        {
            OverlayWidgetDto? updated = null;
            store.Update(s =>
            {
                var entry = s.Overlay.Layout.FirstOrDefault(w => w.Id == id);
                if (entry is null) return;
                if (body.Size is not null) entry.Size = NormalizeSize(body.Size);
                if (body.Monitor.HasValue) entry.Monitor = body.Monitor.Value;
                if (body.Col.HasValue) entry.Col = body.Col.Value;
                if (body.Row.HasValue) entry.Row = body.Row.Value;
                if (body.Locked.HasValue) entry.Locked = body.Locked.Value;
                if (body.Config is not null) entry.Config = body.Config;
                updated = entry;
            });

            if (updated is null)
                return Results.NotFound(ApiResponse.Fail("widget not found"));

            pm.MarkDirty();
            PanelTopics.BroadcastPrefs(hub);
            return Results.Json(updated, AppJsonContext.Default.OverlayWidgetDto);
        }).AllowPanel();

        app.MapPost("/overlay/widgets/lock", (OverlayLockAllBody body, IConfigStore store, ProfileManager pm, MultiplexHub hub) =>
        {
            store.Update(s =>
            {
                foreach (var entry in s.Overlay.Layout)
                    entry.Locked = body.Locked;
            });
            pm.MarkDirty();
            PanelTopics.BroadcastPrefs(hub);
            return Results.Ok(ApiResponse.Ok(body.Locked ? "locked" : "unlocked"));
        }).AllowPanel();

        app.MapDelete("/overlay/widgets/{id}", (string id, IConfigStore store, ProfileManager pm, MultiplexHub hub) =>
        {
            var removed = false;
            store.Update(s =>
            {
                var idx = s.Overlay.Layout.FindIndex(w => w.Id == id);
                if (idx < 0) return;
                s.Overlay.Layout.RemoveAt(idx);
                removed = true;
            });
            if (!removed)
                return Results.NotFound(ApiResponse.Fail("widget not found"));
            pm.MarkDirty();
            PanelTopics.BroadcastPrefs(hub);
            return Results.Ok(ApiResponse.Ok("removed"));
        }).AllowPanel();
    }

    private static (int col, int row) FindFirstFreeCell(
        List<OverlayWidgetDto> existing, int monitor, int w, int h)
    {
        // Row-major scan over a default grid in WHOLE cells. Existing widgets
        // can be at fractional positions (0.25 increments); we conservatively
        // mark every cell their bounding box overlaps as occupied so the
        // first-free scan doesn't drop a new widget on top of a half-shifted
        // neighbour.
        var occupied = new HashSet<(int, int)>();
        foreach (var entry in existing)
        {
            if (entry.Monitor != monitor) continue;
            var left = (int)Math.Floor(entry.Col);
            var right = (int)Math.Ceiling(entry.Col + SizeWidth(entry.Size));
            var top = (int)Math.Floor(entry.Row);
            var bottom = (int)Math.Ceiling(entry.Row + SizeHeight(entry.Size));
            for (var c = left; c < right; c++)
            {
                for (var r = top; r < bottom; r++)
                {
                    occupied.Add((c, r));
                }
            }
        }
        for (var row = 0; row + h <= DefaultGridRows; row++)
        {
            for (var col = 0; col + w <= DefaultGridCols; col++)
            {
                var clear = true;
                for (var dc = 0; dc < w && clear; dc++)
                {
                    for (var dr = 0; dr < h && clear; dr++)
                    {
                        if (occupied.Contains((col + dc, row + dr))) clear = false;
                    }
                }
                if (clear) return (col, row);
            }
        }
        return (0, 0);
    }

    private static int SizeWidth(string size) => size switch
    {
        "1x1" => 1,
        "2x2" => 2,
        "2x4" => 2,
        "4x2" => 4,
        "4x4" => 4,
        _ => 2,
    };

    private static int SizeHeight(string size) => size switch
    {
        "1x1" => 1,
        "2x2" => 2,
        "2x4" => 4,
        "4x2" => 2,
        "4x4" => 4,
        _ => 2,
    };
}
