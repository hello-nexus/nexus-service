using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Sensors.Astral;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static class AstralRoutes
{
    public static void MapAstralEndpoints(this WebApplication app)
    {
        app.MapGet("/astral", (AstralMonitor astral) => Json(astral.GetState()));
        app.MapPost("/astral/settings", (AstralSettingsPatch body, AstralMonitor astral) => Json(astral.Patch(body)));
        app.MapPost("/astral/calibrate", (AstralCalibrateRequest? body, AstralMonitor astral) =>
        {
            astral.Recalibrate(body?.GpuId);
            return Json(astral.GetState());
        });
        app.MapPost("/astral/shutdown/cancel", (AstralMonitor astral) => Json(astral.Cancel()));
        app.MapPost("/astral/shutdown/now", (AstralMonitor astral) => Json(astral.ShutdownNow()));
    }

    private static IResult Json(AstralStateDto state) => Results.Json(state, AppJsonContext.Default.AstralStateDto);
}
