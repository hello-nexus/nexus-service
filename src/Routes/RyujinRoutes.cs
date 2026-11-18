using System.Collections.Generic;
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
            if (ValidateRyujinDisplay(body) is { } error)
            {
                return Results.BadRequest(ApiResponse.Fail(error));
            }
            store.Update(s => ApplyRyujinDisplay(s.Devices.RyujinDisplay, body));
            hub.Wake.Release();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    internal static RyujinDisplayStateResponse BuildRyujinDisplayState(RyujinDisplaySettings settings, bool connected) => new()
    {
        IsConnected = connected,
        Brightness = settings.Brightness,
        Rotation = settings.Rotation,
        SensorCount = RyujinMonitorSensors.NormalizeCount(settings.SensorCount),
        Slots = new List<string>(RyujinMonitorSensors.NormalizeSlots(settings.Slots)),
        AvailableSensors = new List<string>(RyujinMonitorSensors.Available),
    };

    internal static string? ValidateRyujinDisplay(RyujinDisplayRequest body)
    {
        if (body.Brightness is int brightness && (brightness < RyujinMonitorProtocol.MinBrightness || brightness > RyujinMonitorProtocol.MaxBrightness))
        {
            return $"brightness must be {RyujinMonitorProtocol.MinBrightness} to {RyujinMonitorProtocol.MaxBrightness}";
        }
        if (body.Rotation is int rotation && rotation is not (0 or 90))
        {
            return "rotation must be 0 or 90";
        }
        if (body.SensorCount is int count && count is not (RyujinMonitorSensors.MinCount or RyujinMonitorSensors.MaxCount))
        {
            return "sensorCount must be 2 or 3";
        }
        if (body.Slots is { } slots)
        {
            if (slots.Count != RyujinMonitorSensors.MaxCount)
            {
                return $"slots must list {RyujinMonitorSensors.MaxCount} sensors";
            }
            foreach (var id in slots)
            {
                if (!RyujinMonitorSensors.IsValid(id))
                {
                    return $"unknown sensor: {id}";
                }
            }
        }
        return null;
    }

    internal static void ApplyRyujinDisplay(RyujinDisplaySettings target, RyujinDisplayRequest body)
    {
        if (body.Brightness is int b)
        {
            target.Brightness = b;
        }
        if (body.Rotation is int r)
        {
            target.Rotation = r;
        }
        if (body.SensorCount is int c)
        {
            target.SensorCount = c;
        }
        if (body.Slots is { } slots)
        {
            target.Slots = new List<string>(slots);
        }
    }
}

public sealed class RyujinDisplayRequest
{
    public int? Brightness { get; set; }
    public int? Rotation { get; set; }
    public int? SensorCount { get; set; }
    public List<string>? Slots { get; set; }
}

public sealed class RyujinDisplayStateResponse
{
    public bool IsConnected { get; set; }
    public int Brightness { get; set; }
    public int Rotation { get; set; }
    public int SensorCount { get; set; }
    public List<string> Slots { get; set; } = new();
    public List<string> AvailableSensors { get; set; } = new();
}
