using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.Ryujin;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapRyujinEndpoints(WebApplication app)
    {
        app.MapGet("/devices/asus-ryujin-extreme/display", (IConfigStore store, RyujinCoolingHub hub) =>
            Results.Json(BuildRyujinDisplayState(store.Load().Devices.RyujinDisplay, hub.IsConnected), AppJsonContext.Default.RyujinDisplayStateResponse));

        app.MapPut("/devices/asus-ryujin-extreme/display", (RyujinDisplayRequest body, IConfigStore store, RyujinCoolingHub hub) =>
        {
            if (body.Brightness is int brightness && (brightness < RyujinMonitorProtocol.MinBrightness || brightness > RyujinMonitorProtocol.MaxBrightness))
            {
                return Results.BadRequest(ApiResponse.Fail($"brightness must be {RyujinMonitorProtocol.MinBrightness} to {RyujinMonitorProtocol.MaxBrightness}"));
            }
            if (body.Rotation is int rotation && rotation is not (0 or 90))
            {
                return Results.BadRequest(ApiResponse.Fail("rotation must be 0 or 90"));
            }
            store.Update(s =>
            {
                if (body.Brightness is int b)
                {
                    s.Devices.RyujinDisplay.Brightness = b;
                }
                if (body.Rotation is int r)
                {
                    s.Devices.RyujinDisplay.Rotation = r;
                }
            });
            hub.Wake.Release();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    internal static RyujinDisplayStateResponse BuildRyujinDisplayState(RyujinDisplaySettings settings, bool connected) => new()
    {
        IsConnected = connected,
        Brightness = settings.Brightness,
        Rotation = settings.Rotation,
    };
}

public sealed class RyujinDisplayRequest
{
    public int? Brightness { get; set; }
    public int? Rotation { get; set; }
}

public sealed class RyujinDisplayStateResponse
{
    public bool IsConnected { get; set; }
    public int Brightness { get; set; }
    public int Rotation { get; set; }
}
