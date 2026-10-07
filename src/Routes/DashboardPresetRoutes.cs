using Nexus.Service.Models;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

/// <summary>Named desktop dashboard layouts. Dashboard-only, like the panel preset routes; every change rides the prefs topic, which the dashboard already refetches its layout on.</summary>
public static class DashboardPresetRoutes
{
    public static void MapDashboardPresetEndpoints(this WebApplication app)
    {
        app.MapGet("/dashboard/presets", (IConfigStore store) =>
            Results.Json(DashboardPresets.ToResponse(store.Load().Panel), AppJsonContext.Default.DashboardPresetsResponse));

        app.MapPost("/dashboard/presets/seed", (DashboardPresetSeedBody body, IConfigStore store, MultiplexHub hub) =>
        {
            var seeded = false;
            DashboardPresetsResponse result = new();
            store.Update(s =>
            {
                seeded = DashboardPresets.Seed(s.Panel, body.Presets);
                result = DashboardPresets.ToResponse(s.Panel);
            });
            if (seeded)
                PanelTopics.BroadcastPrefs(hub);
            return Results.Json(result, AppJsonContext.Default.DashboardPresetsResponse);
        });

        app.MapPost("/dashboard/presets", (PanelPresetNameBody body, IConfigStore store, MultiplexHub hub) =>
        {
            var name = (body.Name ?? "").Trim();
            if (name.Length == 0)
                return Results.BadRequest(ApiResponse.Fail("preset name required"));
            var created = false;
            DashboardPresetsResponse result = new();
            store.Update(s =>
            {
                created = DashboardPresets.Create(s.Panel, name);
                result = DashboardPresets.ToResponse(s.Panel);
            });
            if (!created)
                return Results.BadRequest(ApiResponse.Fail($"Preset cap of {DashboardPresets.Cap} reached"));
            PanelTopics.BroadcastPrefs(hub);
            return Results.Json(result, AppJsonContext.Default.DashboardPresetsResponse);
        });

        app.MapPut("/dashboard/presets/{presetId}", (string presetId, PanelPresetNameBody body, IConfigStore store, MultiplexHub hub) =>
        {
            var name = (body.Name ?? "").Trim();
            if (name.Length == 0)
                return Results.BadRequest(ApiResponse.Fail("preset name required"));
            var found = false;
            DashboardPresetsResponse result = new();
            store.Update(s =>
            {
                found = DashboardPresets.Rename(s.Panel, presetId, name);
                result = DashboardPresets.ToResponse(s.Panel);
            });
            if (!found)
                return Results.NotFound(ApiResponse.Fail("preset not found"));
            PanelTopics.BroadcastPrefs(hub);
            return Results.Json(result, AppJsonContext.Default.DashboardPresetsResponse);
        });

        app.MapDelete("/dashboard/presets/{presetId}", (string presetId, IConfigStore store, MultiplexHub hub) =>
        {
            DashboardPresetsResponse result = new();
            store.Update(s =>
            {
                DashboardPresets.Delete(s.Panel, presetId);
                result = DashboardPresets.ToResponse(s.Panel);
            });
            PanelTopics.BroadcastPrefs(hub);
            return Results.Json(result, AppJsonContext.Default.DashboardPresetsResponse);
        });

        app.MapPost("/dashboard/presets/{presetId}/activate", (string presetId, IConfigStore store, MultiplexHub hub) =>
        {
            var found = false;
            DashboardPresetsResponse result = new();
            store.Update(s =>
            {
                found = DashboardPresets.Activate(s.Panel, presetId);
                result = DashboardPresets.ToResponse(s.Panel);
            });
            if (!found)
                return Results.NotFound(ApiResponse.Fail("preset not found"));
            PanelTopics.BroadcastPrefs(hub);
            return Results.Json(result, AppJsonContext.Default.DashboardPresetsResponse);
        });
    }
}
