using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Lighting;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.Galahad2;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapGalahad2Endpoints(WebApplication app)
    {
        // GET /devices/lianli-aio/state
        app.MapGet("/devices/lianli-aio/state", (Galahad2Hub hub) =>
        {
            // Read snapshot once; all field accesses use this single reference.
            var snap = hub.Snapshot;
            return Results.Json(
                new Galahad2StateResponse
                {
                    IsConnected = hub.IsConnected,
                    FanRpm      = snap.FanRpm,
                    PumpRpm     = snap.PumpRpm,
                    FanDuty     = snap.FanDuty,
                    PumpDuty    = snap.PumpDuty,
                },
                AppJsonContext.Default.Galahad2StateResponse);
        });

        // GET /devices/lianli-aio/lighting
        app.MapGet("/devices/lianli-aio/lighting", (IConfigStore store) =>
        {
            var s       = store.Load();
            var ls      = s.Devices.Galahad2Lighting;
            var catalog = new Galahad2ModeInfoDto[Galahad2LightingModes.Catalog.Length];
            for (var i = 0; i < Galahad2LightingModes.Catalog.Length; i++)
            {
                var m = Galahad2LightingModes.Catalog[i];
                catalog[i] = new Galahad2ModeInfoDto
                {
                    Key           = m.Key,
                    Label         = m.Label,
                    HasSpeed      = m.HasSpeed,
                    HasDirection  = m.HasDirection,
                    HasBrightness = m.HasBrightness,
                    ColorsMin     = m.ColorsMin,
                    ColorsMax     = m.ColorsMax,
                };
            }
            return Results.Json(
                new Galahad2LightingResponse
                {
                    Mode       = ls.Mode,
                    EffectMode = ls.Mode != "canvas" ? ls.Mode : ls.EffectMode ?? "rainbow",
                    Speed      = ls.Speed,
                    Direction  = ls.Direction,
                    Brightness = ls.Brightness,
                    InnerColor = ls.InnerColor,
                    OuterColor = ls.OuterColor,
                    Colors     = ls.Colors.ToArray(),
                    Modes      = catalog,
                },
                AppJsonContext.Default.Galahad2LightingResponse);
        });

        // PUT /devices/lianli-aio/lighting
        app.MapPut("/devices/lianli-aio/lighting", (
            Galahad2LightingRequest body,
            IConfigStore store,
            Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            if (body.Mode != null
                && body.Mode != "canvas"
                && Galahad2LightingModes.Find(body.Mode) == null)
            {
                return Results.BadRequest(ApiResponse.Fail("unknown mode key"));
            }
            store.Update(s =>
            {
                var ls = s.Devices.Galahad2Lighting;
                if (body.Mode != null)
                {
                    ls.Mode = body.Mode;
                    if (body.Mode != "canvas") ls.EffectMode = body.Mode;
                }
                if (body.Speed.HasValue)       { ls.Speed     = Math.Clamp(body.Speed.Value, 0, 4); }
                if (body.Direction.HasValue)   { ls.Direction = Math.Clamp(body.Direction.Value, 0, 1); }
                if (body.Brightness.HasValue)  { ls.Brightness= Math.Clamp(body.Brightness.Value, 0, 4); }
                if (body.InnerColor != null)   { ls.InnerColor= body.InnerColor; }
                if (body.OuterColor != null)   { ls.OuterColor= body.OuterColor; }
                if (body.Colors != null)
                {
                    var modeInfo  = Galahad2LightingModes.Find(ls.Mode);
                    var maxColors = modeInfo?.ColorsMax ?? 0;
                    var count     = Math.Min(body.Colors.Length, maxColors);
                    ls.Colors     = new List<string>(count);
                    for (var i = 0; i < count; i++)
                    {
                        ls.Colors.Add(body.Colors[i]);
                    }
                }
            });
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }
}

public sealed class Galahad2StateResponse
{
    public bool IsConnected { get; set; }
    public int FanRpm { get; set; }
    public int PumpRpm { get; set; }
    public int FanDuty { get; set; }
    public int PumpDuty { get; set; }
}

public sealed class Galahad2ModeInfoDto
{
    public string Key           { get; set; } = "";
    public string Label         { get; set; } = "";
    public bool   HasSpeed      { get; set; }
    public bool   HasDirection  { get; set; }
    public bool   HasBrightness { get; set; }
    public int    ColorsMin     { get; set; }
    public int    ColorsMax     { get; set; }
}

public sealed class Galahad2LightingResponse
{
    public string              Mode       { get; set; } = "";
    /// <summary>The mode the device returns to when Lighting page control is turned off.</summary>
    public string              EffectMode { get; set; } = "";
    public int                 Speed      { get; set; }
    public int                 Direction  { get; set; }
    public int                 Brightness { get; set; }
    public string              InnerColor { get; set; } = "";
    public string              OuterColor { get; set; } = "";
    public string[]            Colors     { get; set; } = Array.Empty<string>();
    public Galahad2ModeInfoDto[] Modes    { get; set; } = Array.Empty<Galahad2ModeInfoDto>();
}

public sealed class Galahad2LightingRequest
{
    public string?  Mode       { get; set; }
    public int?     Speed      { get; set; }
    public int?     Direction  { get; set; }
    public int?     Brightness { get; set; }
    public string?  InnerColor { get; set; }
    public string?  OuterColor { get; set; }
    public string[]? Colors    { get; set; }
}
