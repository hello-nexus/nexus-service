using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Lighting;
using Nexus.Service.Models;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapStrimerEndpoints(WebApplication app)
    {
        // GET /devices/strimer/lighting - current settings and mode catalog.
        app.MapGet("/devices/strimer/lighting", (IConfigStore store) =>
        {
            var s       = store.Load();
            var ls      = s.Devices.StrimerLighting;
            var catalog = new StrimerModeInfoDto[StrimerLightingModes.Catalog.Length];
            for (var i = 0; i < StrimerLightingModes.Catalog.Length; i++)
            {
                var m = StrimerLightingModes.Catalog[i];
                catalog[i] = new StrimerModeInfoDto
                {
                    Key          = m.Key,
                    Label        = m.Label,
                    HasSpeed     = m.HasSpeed,
                    HasDirection = m.HasDirection,
                    HasBrightness= m.HasBrightness,
                    ColorsMin    = m.ColorsMin,
                    ColorsMax    = m.ColorsMax,
                };
            }
            return Results.Json(new StrimerLightingResponse
            {
                Mode      = ls.Mode,
                EffectMode= ls.Mode != "custom" ? ls.Mode : ls.EffectMode ?? "rainbow",
                Speed     = ls.Speed,
                Direction = ls.Direction,
                Brightness= ls.Brightness,
                Colors    = ls.Colors.ToArray(),
                Modes     = catalog,
            }, AppJsonContext.Default.StrimerLightingResponse);
        });

        // PUT /devices/strimer/lighting - patch mode/speed/direction/brightness/colors.
        app.MapPut("/devices/strimer/lighting", (
            StrimerLightingRequest body,
            IConfigStore store,
            Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            if (body.Mode != null && StrimerLightingModes.Find(body.Mode) == null)
            {
                return Results.BadRequest(ApiResponse.Fail("unknown mode key"));
            }
            store.Update(s =>
            {
                var ls = s.Devices.StrimerLighting;
                if (body.Mode != null)
                {
                    ls.Mode = body.Mode;
                    if (body.Mode != "custom") ls.EffectMode = body.Mode;
                }
                if (body.Speed.HasValue)      ls.Speed      = Math.Clamp(body.Speed.Value, 0, 4);
                if (body.Direction.HasValue)  ls.Direction  = Math.Clamp(body.Direction.Value, 0, 1);
                if (body.Brightness.HasValue) ls.Brightness = Math.Clamp(body.Brightness.Value, 0, 4);
                if (body.Colors != null)
                {
                    var modeInfo  = StrimerLightingModes.Find(ls.Mode);
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

public sealed class StrimerModeInfoDto
{
    public string Key          { get; set; } = "";
    public string Label        { get; set; } = "";
    public bool   HasSpeed     { get; set; }
    public bool   HasDirection { get; set; }
    public bool   HasBrightness{ get; set; }
    public int    ColorsMin    { get; set; }
    public int    ColorsMax    { get; set; }
}

public sealed class StrimerLightingResponse
{
    public string             Mode      { get; set; } = "";
    /// <summary>The mode the device returns to when Lighting page control is turned off.</summary>
    public string             EffectMode{ get; set; } = "";
    public int                Speed     { get; set; }
    public int                Direction { get; set; }
    public int                Brightness{ get; set; }
    public string[]           Colors    { get; set; } = Array.Empty<string>();
    public StrimerModeInfoDto[] Modes   { get; set; } = Array.Empty<StrimerModeInfoDto>();
}

public sealed class StrimerLightingRequest
{
    public string?  Mode      { get; set; }
    public int?     Speed     { get; set; }
    public int?     Direction { get; set; }
    public int?     Brightness{ get; set; }
    public string[]? Colors   { get; set; }
}
