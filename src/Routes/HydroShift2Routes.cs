using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapHydroShift2Endpoints(WebApplication app)
    {
        app.MapGet("/devices/lianli-hydroshift2/lighting", (IConfigStore store) =>
        {
            var ls = store.Load().Devices.HydroShift2Lighting;
            var modes = new HydroShift2ModeDto[HydroShift2RingEffects.Modes.Count];
            for (int i = 0; i < modes.Length; i++)
            {
                var key = HydroShift2RingEffects.Modes[i];
                modes[i] = new HydroShift2ModeDto
                {
                    Key = key,
                    Colors = HydroShift2RingEffects.ColorCount(key),
                    HasDirection = HydroShift2RingEffects.HasDirection(key),
                    HasSpeed = key != HydroShift2RingEffects.Off && key != HydroShift2RingEffects.Static,
                    HasBrightness = key != HydroShift2RingEffects.Off,
                };
            }
            return Results.Json(
                new HydroShift2LightingResponse
                {
                    Mode = ls.Mode,
                    EffectMode = ls.Mode != HydroShift2LightingSettings.CanvasMode
                        ? ls.Mode
                        : ls.EffectMode ?? HydroShift2RingEffects.Rainbow,
                    Speed = ls.Speed,
                    Direction = ls.Direction,
                    Brightness = ls.Brightness,
                    Colors = ls.Colors.ToArray(),
                    Modes = modes,
                },
                AppJsonContext.Default.HydroShift2LightingResponse);
        });

        app.MapPut("/devices/lianli-hydroshift2/lighting", (
            HydroShift2LightingRequest body,
            IConfigStore store,
            Nexus.Service.Sockets.MultiplexHub mux) =>
        {
            if (body.Mode is { } mode
                && mode != HydroShift2LightingSettings.CanvasMode
                && !HydroShift2RingEffects.Modes.Contains(mode))
            {
                return Results.BadRequest(ApiResponse.Fail("unknown mode key"));
            }
            if (body.Colors is { } colors && (colors.Length > 4 || Array.Exists(colors, c => !HexColor().IsMatch(c))))
            {
                return Results.BadRequest(ApiResponse.Fail("colors must be at most four #rrggbb values"));
            }
            store.Update(s =>
            {
                var ls = s.Devices.HydroShift2Lighting;
                if (body.Mode is { } m)
                {
                    ls.Mode = m;
                    if (m != HydroShift2LightingSettings.CanvasMode) ls.EffectMode = m;
                }
                if (body.Speed is { } speed) ls.Speed = Math.Clamp(speed, 0, HydroShift2RingEffects.MaxLevel);
                if (body.Direction is { } direction) ls.Direction = Math.Clamp(direction, 0, 1);
                if (body.Brightness is { } brightness) ls.Brightness = Math.Clamp(brightness, 0, HydroShift2RingEffects.MaxLevel);
                if (body.Colors is { } c) ls.Colors = new List<string>(c);
            });
            Nexus.Service.Sockets.PanelTopics.BroadcastLighting(mux);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();
}

public sealed class HydroShift2ModeDto
{
    public string Key { get; set; } = "";
    /// <summary>Palette entries the mode uses.</summary>
    public int Colors { get; set; }
    public bool HasDirection { get; set; }
    public bool HasSpeed { get; set; }
    public bool HasBrightness { get; set; }
}

public sealed class HydroShift2LightingResponse
{
    public string Mode { get; set; } = "";
    /// <summary>The ring effect played when Lighting page control is off.</summary>
    public string EffectMode { get; set; } = "";
    public int Speed { get; set; }
    public int Direction { get; set; }
    public int Brightness { get; set; }
    public string[] Colors { get; set; } = Array.Empty<string>();
    public HydroShift2ModeDto[] Modes { get; set; } = Array.Empty<HydroShift2ModeDto>();
}

public sealed class HydroShift2LightingRequest
{
    public string? Mode { get; set; }
    public int? Speed { get; set; }
    public int? Direction { get; set; }
    public int? Brightness { get; set; }
    public string[]? Colors { get; set; }
}
