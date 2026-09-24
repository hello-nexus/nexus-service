using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Peripherals.LianLiTl;
using Nexus.Service.Models;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static bool IsHexColor(string? value)
    {
        var s = value is null ? "" : value.TrimStart('#');
        if (s.Length != 6)
        {
            return false;
        }
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    private static void MapLianLiTlEndpoints(WebApplication app)
    {
        // GET /devices/lianli-tl/state - connected flag, per-fan port/fanIndex/rpm/duty.
        app.MapGet("/devices/lianli-tl/state", (TlFanHub hub) =>
        {
            // Read snapshot once; index all arrays from this single reference.
            var snap = hub.Snapshot;
            int count = snap.ChannelCount;
            var fans = new LianLiTlFanDto[count];
            for (int i = 0; i < count; i++)
            {
                fans[i] = new LianLiTlFanDto
                {
                    Port = snap.Port[i],
                    FanIndex = snap.FanIndex[i],
                    Rpm = snap.Rpm[i] >= 0 ? snap.Rpm[i] : 0,
                    Duty = snap.Duty[i],
                };
            }
            return Results.Json(
                new LianLiTlStateResponse { IsConnected = hub.IsConnected, Fans = fans },
                AppJsonContext.Default.LianLiTlStateResponse);
        });

        // GET /devices/lianli-tl/lighting - saved look plus the modes this controller renders.
        app.MapGet("/devices/lianli-tl/lighting", (IConfigStore store) =>
        {
            var ls = store.Load().Devices.TlLighting;
            var catalog = Nexus.Service.Lighting.TlLightingModes.All;
            var modes = new LianLiTlModeDto[catalog.Count];
            for (int i = 0; i < catalog.Count; i++)
            {
                modes[i] = new LianLiTlModeDto { Key = catalog[i].Key, Label = catalog[i].Label };
            }
            return Results.Json(new LianLiTlLightingResponse
            {
                Mode = ls.Mode,
                Speed = ls.Speed,
                Direction = ls.Direction,
                Brightness = ls.Brightness,
                Scope = ls.Scope,
                Colors = ls.Colors.ToArray(),
                Modes = modes,
                MaxColors = Nexus.Service.Lighting.TlLightingModes.MaxColors,
            }, AppJsonContext.Default.LianLiTlLightingResponse);
        });

        // PUT /devices/lianli-tl/lighting - patch the look; the writer commits it.
        app.MapPut("/devices/lianli-tl/lighting", (LianLiTlLightingRequest body, IConfigStore store) =>
        {
            if (body.Mode is not null && Nexus.Service.Lighting.TlLightingModes.Find(body.Mode).Key != body.Mode)
            {
                return Results.BadRequest(ApiResponse.Fail("unknown mode"));
            }
            if (body.Speed is { } sp && (sp < 0 || sp > Nexus.Service.Lighting.TlLightingModes.MaxSpeed))
            {
                return Results.BadRequest(ApiResponse.Fail("speed must be 0..4"));
            }
            if (body.Brightness is { } br && (br < 0 || br > Nexus.Service.Lighting.TlLightingModes.MaxBrightness))
            {
                return Results.BadRequest(ApiResponse.Fail("brightness must be 0..4"));
            }
            if (body.Direction is { } dir && (dir < 0 || dir > Nexus.Service.Lighting.TlLightingModes.MaxDirection))
            {
                return Results.BadRequest(ApiResponse.Fail("direction must be 0..5"));
            }
            if (body.Scope is not null && body.Scope != "all" && body.Scope != "top" && body.Scope != "bottom")
            {
                return Results.BadRequest(ApiResponse.Fail("scope must be all, top or bottom"));
            }
            if (body.Colors is not null)
            {
                if (body.Colors.Length > Nexus.Service.Lighting.TlLightingModes.MaxColors)
                {
                    return Results.BadRequest(ApiResponse.Fail("at most 4 colors"));
                }
                foreach (var color in body.Colors)
                {
                    if (!IsHexColor(color))
                    {
                        return Results.BadRequest(ApiResponse.Fail("colors must be #RRGGBB"));
                    }
                }
            }

            store.Update(s =>
            {
                var ls = s.Devices.TlLighting;
                if (body.Mode is not null) { ls.Mode = body.Mode; }
                if (body.Speed is { } speed) { ls.Speed = speed; }
                if (body.Direction is { } direction) { ls.Direction = direction; }
                if (body.Brightness is { } brightness) { ls.Brightness = brightness; }
                if (body.Scope is not null) { ls.Scope = body.Scope; }
                if (body.Colors is not null) { ls.Colors = new List<string>(body.Colors); }
            });
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }
}

public sealed class LianLiTlLightingResponse
{
    public string Mode { get; set; } = "";
    public int Speed { get; set; }
    public int Direction { get; set; }
    public int Brightness { get; set; }
    public string Scope { get; set; } = "all";
    public string[] Colors { get; set; } = Array.Empty<string>();
    public LianLiTlModeDto[] Modes { get; set; } = Array.Empty<LianLiTlModeDto>();
    public int MaxColors { get; set; }
}

public sealed class LianLiTlModeDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class LianLiTlLightingRequest
{
    public string? Mode { get; set; }
    public int? Speed { get; set; }
    public int? Direction { get; set; }
    public int? Brightness { get; set; }
    public string? Scope { get; set; }
    public string[]? Colors { get; set; }
}

public sealed class LianLiTlStateResponse
{
    public bool IsConnected { get; set; }
    public LianLiTlFanDto[] Fans { get; set; } = Array.Empty<LianLiTlFanDto>();
}

public sealed class LianLiTlFanDto
{
    public int Port { get; set; }
    public int FanIndex { get; set; }
    public int Rpm { get; set; }
    public int Duty { get; set; }
}
