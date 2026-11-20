using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Lighting;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.JpegPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapGalahad2LcdEndpoints(WebApplication app)
    {
        // GET /devices/lianli-galahad2-lcd/ring-effect - the animations the pump ring can play on
        // its own and what was last written. The panel cannot be asked what it plays, so the
        // values come from settings.
        app.MapGet("/devices/lianli-galahad2-lcd/ring-effect", (Galahad2LcdAio aio, IConfigStore store) =>
        {
            var settings = store.Load();
            var saved = settings.Devices.Galahad2LcdRingEffect;
            var effects = new Galahad2LcdRingEffectDto[Galahad2LcdRingEffects.All.Length];
            for (var i = 0; i < effects.Length; i++)
            {
                var e = Galahad2LcdRingEffects.All[i];
                effects[i] = new Galahad2LcdRingEffectDto { Id = e.Id, Colors = e.Colors };
            }
            return Results.Json(
                new Galahad2LcdRingEffectResponse
                {
                    IsConnected = aio.IsConnected,
                    Effect = saved.Effect,
                    Speed = saved.Speed,
                    Brightness = saved.Brightness,
                    Colors = saved.Colors.ToArray(),
                    NexusDriven = !settings.Devices.UncontrolledLightingDevices.Contains(Galahad2LcdLightingProvider.RingZoneId),
                    Effects = effects,
                },
                AppJsonContext.Default.Galahad2LcdRingEffectResponse);
        });

        // PUT /devices/lianli-galahad2-lcd/ring-effect - play one animation on the ring.
        app.MapPut("/devices/lianli-galahad2-lcd/ring-effect", (Galahad2LcdRingEffectRequest body, Galahad2LcdAio aio, IConfigStore store) =>
        {
            if (!aio.IsConnected)
            {
                return Results.Conflict(ApiResponse.Fail("galahad ii lcd not connected"));
            }
            var effect = Galahad2LcdRingEffects.Find(body.Effect);
            if (effect is null)
            {
                return Results.BadRequest(ApiResponse.Fail($"unknown effect '{body.Effect}'"));
            }
            var hex = body.Colors ?? Array.Empty<string>();
            if (hex.Length != effect.Colors)
            {
                return Results.BadRequest(ApiResponse.Fail(
                    $"effect '{effect.Id}' takes {effect.Colors} colours, got {hex.Length}"));
            }
            var rgb = new byte[hex.Length * 3];
            for (var i = 0; i < hex.Length; i++)
            {
                if (!TryParseHexColor(hex[i], out var r, out var g, out var b))
                {
                    return Results.BadRequest(ApiResponse.Fail($"colour '{hex[i]}' is not #rrggbb"));
                }
                rgb[i * 3] = r;
                rgb[i * 3 + 1] = g;
                rgb[i * 3 + 2] = b;
            }
            var speed = Math.Clamp(body.Speed, 0, 4);
            var brightness = Math.Clamp(body.Brightness, 0, 4);
            if (!aio.SetRingEffect(effect.WireByte, (byte)brightness, (byte)speed, rgb))
            {
                return Results.Problem("Failed to write the animation to the pump ring.");
            }
            store.Update(s =>
            {
                s.Devices.Galahad2LcdRingEffect = new Galahad2LcdRingEffectSettings
                {
                    Effect = effect.Id,
                    Speed = speed,
                    Brightness = brightness,
                    Colors = new List<string>(hex),
                };
            });
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }
}

public sealed class Galahad2LcdRingEffectDto
{
    public string Id { get; set; } = "";
    public int Colors { get; set; }
}

public sealed class Galahad2LcdRingEffectResponse
{
    public bool IsConnected { get; set; }
    public string Effect { get; set; } = "";
    public int Speed { get; set; }
    public int Brightness { get; set; }
    public string[] Colors { get; set; } = Array.Empty<string>();
    /// <summary>False when the ring is left to the firmware, which is when a written animation keeps playing.</summary>
    public bool NexusDriven { get; set; }
    public Galahad2LcdRingEffectDto[] Effects { get; set; } = Array.Empty<Galahad2LcdRingEffectDto>();
}

public sealed class Galahad2LcdRingEffectRequest
{
    public string Effect { get; set; } = "";
    public int Speed { get; set; } = 2;
    public int Brightness { get; set; } = 4;
    public string[]? Colors { get; set; }
}
