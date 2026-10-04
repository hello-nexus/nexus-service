using System.Threading;
using Nexus.Service.Integrations.HomeAssistant;
using Nexus.Service.Models;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapHomeAssistantEndpoints(WebApplication app)
    {
        app.MapGet("/home-assistant/config", (HomeAssistantHub hub) =>
            hub.GetConfigResponse());

        app.MapPost("/home-assistant/config", async (HaConfigBody body, HomeAssistantHub hub, CancellationToken ct) =>
            await hub.SetConfigAsync(body.Url, body.Token, ct));

        app.MapGet("/home-assistant/entities", (HomeAssistantHub hub) =>
            hub.GetEntitiesResponse());

        app.MapGet("/home-assistant/dashboards", async (HomeAssistantHub hub, CancellationToken ct) =>
            await hub.GetDashboardsAsync(ct));

        app.MapGet("/home-assistant/dashboard", async (string? id, HomeAssistantHub hub, CancellationToken ct) =>
            await hub.GetDashboardAsync(id ?? "", ct));

        // Returns HaEntityDto on success or ApiResponse on failure; both branches
        // use the source-gen TypeInfo overload as required for AOT.
        app.MapPost("/home-assistant/entity/set", async (HaSetEntityBody body, HomeAssistantHub hub, CancellationToken ct) =>
        {
            var result = await hub.SetEntityAsync(body, ct);
            if (result is null)
            {
                return Results.Json(
                    ApiResponse.Fail("entity not found or hub not configured"),
                    AppJsonContext.Default.ApiResponse);
            }
            return Results.Json(result, AppJsonContext.Default.HaEntityDto);
        });
    }
}
