using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting.Scene;
using Nexus.Service.Models;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>The machine-local 3D cooling layout. Desktop-token only, like the scene and PC setup routes.</summary>
public static class CoolingLayoutRoutes
{
    private static readonly object WriteLock = new();

    public static void MapCoolingLayoutEndpoints(this WebApplication app)
    {
        app.MapGet("/cooling/layout", (LightingSceneStore scene) =>
            Results.Json(ToResponse(scene.Cooling.Load()), AppJsonContext.Default.CoolingLayoutResponse));

        app.MapPut("/cooling/layout", async (HttpContext ctx, LightingSceneStore scene) =>
        {
            var body = await ReadBodyAsync(ctx, AppJsonContext.Default.PutCoolingLayoutBody);
            if (body is null)
            {
                return Fail("body is malformed or over the size cap");
            }
            if (body.BaseRev is not { } baseRev)
            {
                return Fail("baseRev is required");
            }
            if (CoolingLayoutValidation.Validate(body.Layout) is { } error)
            {
                return Fail(error);
            }
            lock (WriteLock)
            {
                return scene.Cooling.TrySave(baseRev, body.Layout!, out var current)
                    ? Results.Json(new CoolingLayoutRevResponse { Rev = current.Rev }, AppJsonContext.Default.CoolingLayoutRevResponse)
                    : Results.Json(ToResponse(current), AppJsonContext.Default.CoolingLayoutResponse, statusCode: StatusCodes.Status409Conflict);
            }
        });
    }

    /// <summary>Pings the <c>cooling/layout</c> topic after every save.</summary>
    public static void WireTopic(CoolingLayoutStore store, Nexus.Service.Sockets.MultiplexHub hub) =>
        store.Changed += rev => Nexus.Service.Sockets.PanelTopics.BroadcastCoolingLayout(hub, rev);

    // The service-wide body limit is sized for media uploads, so this route sets its own before reading.
    private static async Task<T?> ReadBodyAsync<T>(HttpContext ctx, JsonTypeInfo<T> info)
        where T : class
    {
        var size = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false })
        {
            size.MaxRequestBodySize = CoolingLayoutValidation.MaxBodyBytes;
        }
        try
        {
            return await JsonSerializer.DeserializeAsync(ctx.Request.Body, info, ctx.RequestAborted);
        }
        catch (Exception e) when (e is JsonException or BadHttpRequestException)
        {
            return null;
        }
    }

    private static CoolingLayoutResponse ToResponse(CoolingLayoutFile file) => new() { Rev = file.Rev, Layout = file.Layout };

    private static IResult Fail(string msg) =>
        Results.Json(new ApiResponse { Error = true, Msg = msg }, AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status400BadRequest);
}
