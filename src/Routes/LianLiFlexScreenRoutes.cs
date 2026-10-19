using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.LianLiFlex;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public sealed class LianLiFlexSpriteDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class LianLiFlexSpritesResponse
{
    public LianLiFlexSpriteDto[] Sprites { get; set; } = Array.Empty<LianLiFlexSpriteDto>();
}

public sealed class LianLiFlexSpriteImportResponse
{
    public bool Error { get; set; }
    public string Msg { get; set; } = "";
    public LianLiFlexSpriteDto? Sprite { get; set; }
}

public sealed class LianLiFlexModularSourceDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Unit { get; set; } = "";
    public bool Available { get; set; }
}

public sealed class LianLiFlexModularSourcesResponse
{
    public LianLiFlexModularSourceDto[] Sources { get; set; } = Array.Empty<LianLiFlexModularSourceDto>();
}

public static partial class LianLiFlexRoutes
{
    private static readonly string[] OverlayTypes = ["circular", "rotate", "bounce", "collision"];

    /// <summary>Checks the Flex-only content types; null when the request is fine (or is a shared type).</summary>
    internal static string? ValidateExtraContent(Slv3LcdContentRequest body, FlexSpriteLibrary sprites)
    {
        if (string.IsNullOrWhiteSpace(body.Serial))
        {
            return "missing serial";
        }
        if (body.ContentType == FlexExtraContent.ScreenMode)
        {
            var s = body.ScreenSource;
            if (s is not null && (s.CropSize < 0 || s.CropSize > 1 || s.CropX < 0 || s.CropY < 0 || s.CropX > 1 || s.CropY > 1 || s.DisplayId.Length > 256))
            {
                return "invalid screenSource";
            }
            return null;
        }
        var o = body.Overlay;
        if (o is null)
        {
            return "missing overlay";
        }
        if (Array.IndexOf(OverlayTypes, o.Type) < 0)
        {
            return "invalid overlay type";
        }
        if (o.Speed < 1 || o.Speed > 10 || o.Zoom < 50 || o.Zoom > 200 || (o.Direction != 0 && o.Direction != 1))
        {
            return "invalid overlay motion";
        }
        if (o.CenterX < 0 || o.CenterX > 400 || o.CenterY < 0 || o.CenterY > 400)
        {
            return "invalid overlay center";
        }
        if (o.Params.Bounciness is < 0 or > 4 || o.Params.RotationIntensity is < 0 or > 2 || o.Params.Radius is < 0 or > 400
            || o.Params.UpHeight is < 0 or > 400 || o.Params.RotateAngle is < 0 or > 3600 || o.Params.DwellSeconds is < 0 or > 60)
        {
            return "invalid overlay params";
        }
        if (!Nexus.Service.Media.MediaLibrary.IsValidId(o.SpriteMediaId) || sprites.PngPath(o.SpriteMediaId) is null)
        {
            return "unknown sprite";
        }
        if (body.MediaId is not null && !Nexus.Service.Media.MediaLibrary.IsValidId(body.MediaId))
        {
            return "invalid mediaId";
        }
        return null;
    }

    internal static bool IsExtraContent(string contentType) =>
        contentType is FlexExtraContent.ScreenMode or FlexExtraContent.Animation2dMode;

    internal static void ApplyExtraContent(LianLiWirelessScreenSettings screen, Slv3LcdContentRequest body)
    {
        screen.ContentType = body.ContentType;
        if (body.ContentType == FlexExtraContent.ScreenMode)
        {
            screen.MediaId = null;
            screen.ScreenSource = body.ScreenSource ?? screen.ScreenSource;
        }
        else
        {
            screen.MediaId = body.MediaId;
            screen.Overlay = body.Overlay;
        }
    }

    internal static LianLiFlexModularSourceDto[] ModularSources(Func<string, bool> available) =>
        new (string Key, string Label, string Unit)[]
        {
            ("cpuTemp", "CPU temperature", "C"),
            ("cpuLoad", "CPU usage", "%"),
            ("gpuTemp", "GPU temperature", "C"),
            ("gpuLoad", "GPU usage", "%"),
            ("memoryUsage", "Memory usage", "%"),
            ("vramUsage", "VRAM usage", "%"),
            ("fanRpm", "Fan speed", "RPM"),
        }.Select(s => new LianLiFlexModularSourceDto { Key = s.Key, Label = s.Label, Unit = s.Unit, Available = available(s.Key) }).ToArray();

    private static void MapScreenExtras(WebApplication app)
    {
        app.MapGet("/devices/lianli-flex/modular-sources", (Nexus.Service.Peripherals.LianLiWireless.Slv3LcdSensorReader reader) =>
        {
            var list = ModularSources(key => reader.Read(key, "c").Value > 0f || key == "fanRpm");
            return Results.Json(new LianLiFlexModularSourcesResponse { Sources = list }, AppJsonContext.Default.LianLiFlexModularSourcesResponse);
        });

        app.MapGet("/devices/lianli-flex/sprites", (FlexSpriteLibrary sprites) =>
            Results.Json(new LianLiFlexSpritesResponse
            {
                Sprites = sprites.List().Select(s => new LianLiFlexSpriteDto { Id = s.Id, Name = s.Name, Width = s.Width, Height = s.Height }).ToArray(),
            }, AppJsonContext.Default.LianLiFlexSpritesResponse));

        app.MapGet("/devices/lianli-flex/sprites/{id}/image", (string id, FlexSpriteLibrary sprites) =>
            sprites.PngPath(id) is { } path ? Results.File(path, "image/png") : Results.NotFound());

        app.MapPost("/devices/lianli-flex/sprites", async (HttpContext ctx, FlexSpriteLibrary sprites) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.Json(new LianLiFlexSpriteImportResponse { Error = true, Msg = "Expected multipart/form-data" },
                    AppJsonContext.Default.LianLiFlexSpriteImportResponse, statusCode: 400);
            }
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0 || file.Length > FlexSpriteLibrary.MaxBytes)
            {
                return Results.Json(new LianLiFlexSpriteImportResponse { Error = true, Msg = "No usable file" },
                    AppJsonContext.Default.LianLiFlexSpriteImportResponse, statusCode: 400);
            }
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var (sprite, error) = sprites.Import(ms.ToArray(), file.FileName);
            if (sprite is null)
            {
                return Results.Json(new LianLiFlexSpriteImportResponse { Error = true, Msg = error ?? "Import failed" },
                    AppJsonContext.Default.LianLiFlexSpriteImportResponse, statusCode: 400);
            }
            return Results.Json(new LianLiFlexSpriteImportResponse
            {
                Sprite = new LianLiFlexSpriteDto { Id = sprite.Id, Name = sprite.Name, Width = sprite.Width, Height = sprite.Height },
            }, AppJsonContext.Default.LianLiFlexSpriteImportResponse);
        });

        app.MapDelete("/devices/lianli-flex/sprites/{id}", (string id, FlexSpriteLibrary sprites) =>
            sprites.Delete(id)
                ? Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse)
                : Results.Json(ApiResponse.Fail("unknown sprite"), AppJsonContext.Default.ApiResponse, statusCode: 404));
    }
}
