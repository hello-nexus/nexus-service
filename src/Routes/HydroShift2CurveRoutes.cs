using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapHydroShift2CurveEndpoints(WebApplication app)
    {
        app.MapGet("/devices/lianli-hydroshift2-curve/head", (HydroShift2CurveBoard board) =>
        {
            var head = board.Head;
            return Results.Json(
                new HydroShift2CurveHeadResponse
                {
                    Connected = board.IsAvailable,
                    Tilt = head.Tilt,
                    Slide = head.Slide,
                    TargetTilt = head.TargetTilt,
                    TargetSlide = head.TargetSlide,
                    Moving = head.Moving,
                    Calibrating = head.Calibrating,
                    TiltMax = HydroShift2CurveProtocol.TiltMax,
                    SlideMin = HydroShift2CurveProtocol.SlideMin,
                    SlideMax = HydroShift2CurveProtocol.SlideMax,
                },
                AppJsonContext.Default.HydroShift2CurveHeadResponse);
        });

        app.MapPut("/devices/lianli-hydroshift2-curve/head", (HydroShift2CurveHeadRequest body, HydroShift2CurveBoard board) =>
        {
            board.SetHeadTarget(body.Tilt, body.Slide);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPost("/devices/lianli-hydroshift2-curve/head/recalibrate", (HydroShift2CurveBoard board) =>
        {
            if (!board.IsAvailable)
            {
                return Results.BadRequest(ApiResponse.Fail("not connected"));
            }
            board.Recalibrate();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }
}

public sealed class HydroShift2CurveHeadResponse
{
    public bool Connected { get; set; }
    /// <summary>Degrees the head is believed to sit at; the motors have no position readback.</summary>
    public int Tilt { get; set; }
    /// <summary>Height, 0 being the middle of the slide's range.</summary>
    public int Slide { get; set; }
    public int TargetTilt { get; set; }
    public int TargetSlide { get; set; }
    public bool Moving { get; set; }
    public bool Calibrating { get; set; }
    public int TiltMax { get; set; }
    public int SlideMin { get; set; }
    public int SlideMax { get; set; }
}

public sealed class HydroShift2CurveHeadRequest
{
    public int? Tilt { get; set; }
    public int? Slide { get; set; }
}
