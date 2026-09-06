using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Media;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.LianLiWireless;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public sealed class Slv3LcdScreenDto
{
    public string Serial { get; set; } = "";
    /// <summary>GroupIndex from GetPosIndex(201); -1 if not yet known.</summary>
    public int Position { get; set; } = -1;
    public int Width { get; set; } = Slv3LcdProtocol.PanelWidth;
    public int Height { get; set; } = Slv3LcdProtocol.PanelHeight;
    public byte Brightness { get; set; }
    public byte Rotation { get; set; }
    public string ContentType { get; set; } = "off";
    public string? MediaId { get; set; }
    /// <summary>"cpuLoad" | "cpuTemp" | "gpuLoad" | "gpuTemp" | "memoryUsage" | "vramUsage" | "fanRpm". Set when contentType is "sensor".</summary>
    public string? SensorSource { get; set; }
    /// <summary>"ring" | "bar". Set when contentType is "sensor".</summary>
    public string? SensorStyle { get; set; }
    /// <summary>"digital" | "digitalMinimal" | "analogClassic" | "analogMinimal". Set when contentType is "clock".</summary>
    public string? ClockFace { get; set; }
    /// <summary>"pulse" | "spectrum" | "spin". Set when contentType is "animation".</summary>
    public string? AnimationId { get; set; }
    /// <summary>Accent hex color "#RRGGBB": gauge fill, clock hands/digits, animation primary color.</summary>
    public string? ColorA { get; set; }
    /// <summary>Secondary hex color "#RRGGBB": gauge/clock text color, animation secondary color.</summary>
    public string? ColorB { get; set; }
    /// <summary>"c" | "f". Display unit for a temperature sensor source.</summary>
    public string? TempUnit { get; set; }
    /// <summary>User-chosen list position (0-based); -1 when not set. The list is already sorted by it.</summary>
    public int Order { get; set; } = -1;
}

public sealed class Slv3LcdScreensResponse
{
    public List<Slv3LcdScreenDto> Screens { get; set; } = new();
}

public sealed class Slv3LcdSettingsRequest
{
    public string Serial { get; set; } = "";
    public byte? Brightness { get; set; }
    public byte? Rotation { get; set; }
}

public sealed class Slv3LcdOrderRequest
{
    /// <summary>Screen serials in the wanted list order; each gets its index as its order.</summary>
    public List<string> Serials { get; set; } = new();
}

public sealed class Slv3LcdContentRequest
{
    public string Serial { get; set; } = "";
    /// <summary>"off" | "image" | "gif" | "video" | "sensor" | "clock" | "animation".</summary>
    public string ContentType { get; set; } = "";
    public string? MediaId { get; set; }
    /// <summary>"cpuLoad" | "cpuTemp" | "gpuLoad" | "gpuTemp" | "memoryUsage" | "vramUsage" | "fanRpm". Used when contentType is "sensor".</summary>
    public string? SensorSource { get; set; }
    /// <summary>"ring" | "bar". Used when contentType is "sensor".</summary>
    public string? SensorStyle { get; set; }
    /// <summary>"digital" | "digitalMinimal" | "analogClassic" | "analogMinimal". Used when contentType is "clock".</summary>
    public string? ClockFace { get; set; }
    /// <summary>"pulse" | "spectrum" | "spin". Used when contentType is "animation".</summary>
    public string? AnimationId { get; set; }
    /// <summary>Accent hex color "#RRGGBB": gauge fill, clock hands/digits, animation primary color.</summary>
    public string? ColorA { get; set; }
    /// <summary>Secondary hex color "#RRGGBB": gauge/clock text color, animation secondary color.</summary>
    public string? ColorB { get; set; }
    /// <summary>"c" | "f". Display unit for a temperature sensor source.</summary>
    public string? TempUnit { get; set; }
}

public sealed class Slv3LcdMediaDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"image" | "gif" | "video".</summary>
    public string Kind { get; set; } = "";
}

public sealed class Slv3LcdMediaListResponse
{
    public List<Slv3LcdMediaDto> Items { get; set; } = new();
}

public sealed class Slv3LcdImportResponse
{
    public bool Error { get; set; }
    public string? Msg { get; set; }
    public string? MediaId { get; set; }
    public string? Name { get; set; }
    public string? Kind { get; set; }
}

/// <summary>
/// First-party Lian Li SL-LCD Wireless fan-screen routes: discovery/settings
/// list, per-screen brightness/rotation/content, and the media library
/// (import/list/delete). See plans/lianli-wireless-support.md section 4-5.
/// </summary>
public static class Slv3LcdRoutes
{
    private static readonly string[] ValidContentTypes = { "off", "image", "gif", "video", "sensor", "clock", "animation" };
    private static readonly string[] ValidSensorSources = { "cpuLoad", "cpuTemp", "gpuLoad", "gpuTemp", "memoryUsage", "vramUsage", "fanRpm" };
    private static readonly string[] ValidSensorStyles = { "ring", "bar" };
    private static readonly string[] ValidClockFaces = { "digital", "digitalMinimal", "analogClassic", "analogMinimal" };
    private static readonly string[] ValidAnimationIds = { "pulse", "spectrum", "spin" };
    private static readonly string[] ValidTempUnits = { "c", "f" };

    /// <summary>
    /// Screens with a saved order first, by that order; the rest keep the hub's
    /// discovery order behind them. Stable, so an unordered set lists as before.
    /// </summary>
    internal static List<Slv3LcdScreenInfo> OrderScreens(
        IReadOnlyList<Slv3LcdScreenInfo> screens, IReadOnlyDictionary<string, LianLiWirelessScreenSettings> settings)
    {
        var ordered = new List<(Slv3LcdScreenInfo Screen, int Order, int Index)>(screens.Count);
        for (var i = 0; i < screens.Count; i++)
        {
            var order = settings.TryGetValue(screens[i].Serial, out var s) && s.Order >= 0 ? s.Order : int.MaxValue;
            ordered.Add((screens[i], order, i));
        }
        ordered.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Index.CompareTo(b.Index));
        var result = new List<Slv3LcdScreenInfo>(ordered.Count);
        foreach (var entry in ordered)
        {
            result.Add(entry.Screen);
        }
        return result;
    }

    public static void MapSlv3LcdEndpoints(this WebApplication app)
    {
        app.MapGet("/devices/lianli-wireless/screens", (Slv3LcdHub hub, IConfigStore store) =>
        {
            var screenSettings = store.Load().Devices.LianLiWireless.Screens;
            var response = new Slv3LcdScreensResponse();
            foreach (var screen in OrderScreens(hub.Screens, screenSettings))
            {
                var settings = screenSettings.TryGetValue(screen.Serial, out var s)
                    ? s
                    : new LianLiWirelessScreenSettings();
                response.Screens.Add(new Slv3LcdScreenDto
                {
                    Serial = screen.Serial,
                    Position = screen.Position,
                    Order = settings.Order,
                    Brightness = settings.Brightness,
                    Rotation = settings.Rotation,
                    ContentType = settings.ContentType,
                    MediaId = settings.MediaId,
                    SensorSource = settings.SensorSource,
                    SensorStyle = settings.SensorStyle,
                    ClockFace = settings.ClockFace,
                    AnimationId = settings.AnimationId,
                    ColorA = settings.ColorA,
                    ColorB = settings.ColorB,
                    TempUnit = settings.TempUnit,
                });
            }
            return Results.Json(response, AppJsonContext.Default.Slv3LcdScreensResponse);
        });

        app.MapPost("/devices/lianli-wireless/screen/settings", (Slv3LcdSettingsRequest body, Slv3LcdHub hub, IConfigStore store) =>
        {
            if (string.IsNullOrWhiteSpace(body.Serial))
            {
                return Results.Json(ApiResponse.Fail("missing serial"), AppJsonContext.Default.ApiResponse);
            }
            if (body.Brightness is > 100)
            {
                return Results.Json(ApiResponse.Fail("brightness must be 0-100"), AppJsonContext.Default.ApiResponse);
            }
            if (body.Rotation is > 3)
            {
                return Results.Json(ApiResponse.Fail("rotation must be 0-3"), AppJsonContext.Default.ApiResponse);
            }

            if (body.Brightness.HasValue) hub.SetBrightness(body.Serial, body.Brightness.Value);
            if (body.Rotation.HasValue) hub.SetRotation(body.Serial, body.Rotation.Value);

            store.Update(s =>
            {
                var screen = s.Devices.LianLiWireless.Screens.TryGetValue(body.Serial, out var existing)
                    ? existing
                    : new LianLiWirelessScreenSettings();
                if (body.Brightness.HasValue) screen.Brightness = body.Brightness.Value;
                if (body.Rotation.HasValue) screen.Rotation = body.Rotation.Value;
                s.Devices.LianLiWireless.Screens[body.Serial] = screen;
            });

            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // GetPosIndex answers 0 for every screen on this firmware, so the saved
        // list order is what lines the tile numbers up with the physical fans.
        app.MapPost("/devices/lianli-wireless/screens/order", (Slv3LcdOrderRequest body, IConfigStore store) =>
        {
            var serials = body.Serials ?? new List<string>();
            if (serials.Count == 0)
            {
                return Results.Json(ApiResponse.Fail("missing serials"), AppJsonContext.Default.ApiResponse);
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var serial in serials)
            {
                if (string.IsNullOrWhiteSpace(serial) || !seen.Add(serial))
                {
                    return Results.Json(ApiResponse.Fail("serials must be distinct and non-empty"), AppJsonContext.Default.ApiResponse);
                }
            }

            store.Update(s =>
            {
                for (var i = 0; i < serials.Count; i++)
                {
                    var screen = s.Devices.LianLiWireless.Screens.TryGetValue(serials[i], out var existing)
                        ? existing
                        : new LianLiWirelessScreenSettings();
                    screen.Order = i;
                    s.Devices.LianLiWireless.Screens[serials[i]] = screen;
                }
            });

            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPost("/devices/lianli-wireless/screen/content", (Slv3LcdContentRequest body, IConfigStore store) =>
        {
            if (string.IsNullOrWhiteSpace(body.Serial))
            {
                return Results.Json(ApiResponse.Fail("missing serial"), AppJsonContext.Default.ApiResponse);
            }
            if (Array.IndexOf(ValidContentTypes, body.ContentType) < 0)
            {
                return Results.Json(ApiResponse.Fail("invalid contentType"), AppJsonContext.Default.ApiResponse);
            }
            if (body.MediaId is not null && !MediaLibrary.IsValidId(body.MediaId))
            {
                return Results.Json(ApiResponse.Fail("invalid mediaId"), AppJsonContext.Default.ApiResponse);
            }
            if (body.SensorSource is not null && Array.IndexOf(ValidSensorSources, body.SensorSource) < 0)
            {
                return Results.Json(ApiResponse.Fail("invalid sensorSource"), AppJsonContext.Default.ApiResponse);
            }
            if (body.SensorStyle is not null && Array.IndexOf(ValidSensorStyles, body.SensorStyle) < 0)
            {
                return Results.Json(ApiResponse.Fail("invalid sensorStyle"), AppJsonContext.Default.ApiResponse);
            }
            if (body.ClockFace is not null && Array.IndexOf(ValidClockFaces, body.ClockFace) < 0)
            {
                return Results.Json(ApiResponse.Fail("invalid clockFace"), AppJsonContext.Default.ApiResponse);
            }
            if (body.AnimationId is not null && Array.IndexOf(ValidAnimationIds, body.AnimationId) < 0)
            {
                return Results.Json(ApiResponse.Fail("invalid animationId"), AppJsonContext.Default.ApiResponse);
            }
            if (body.TempUnit is not null && Array.IndexOf(ValidTempUnits, body.TempUnit) < 0)
            {
                return Results.Json(ApiResponse.Fail("invalid tempUnit"), AppJsonContext.Default.ApiResponse);
            }
            if (body.ColorA is not null && !IsValidHexColor(body.ColorA))
            {
                return Results.Json(ApiResponse.Fail("invalid colorA"), AppJsonContext.Default.ApiResponse);
            }
            if (body.ColorB is not null && !IsValidHexColor(body.ColorB))
            {
                return Results.Json(ApiResponse.Fail("invalid colorB"), AppJsonContext.Default.ApiResponse);
            }

            store.Update(s =>
            {
                var screen = s.Devices.LianLiWireless.Screens.TryGetValue(body.Serial, out var existing)
                    ? existing
                    : new LianLiWirelessScreenSettings();
                screen.ContentType = body.ContentType;
                screen.MediaId = body.ContentType == "off" ? null : body.MediaId;
                screen.SensorSource = body.SensorSource ?? screen.SensorSource;
                screen.SensorStyle = body.SensorStyle ?? screen.SensorStyle;
                screen.ClockFace = body.ClockFace ?? screen.ClockFace;
                screen.AnimationId = body.AnimationId ?? screen.AnimationId;
                screen.ColorA = body.ColorA ?? screen.ColorA;
                screen.ColorB = body.ColorB ?? screen.ColorB;
                screen.TempUnit = body.TempUnit ?? screen.TempUnit;
                s.Devices.LianLiWireless.Screens[body.Serial] = screen;
            });

            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPost("/devices/lianli-wireless/media/import", async (HttpContext ctx, Slv3LcdMediaLibrary library) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.Json(
                    new Slv3LcdImportResponse { Error = true, Msg = "Expected multipart/form-data" },
                    AppJsonContext.Default.Slv3LcdImportResponse,
                    statusCode: 400);
            }

            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.Json(
                    new Slv3LcdImportResponse { Error = true, Msg = "No file provided" },
                    AppJsonContext.Default.Slv3LcdImportResponse,
                    statusCode: 400);
            }

            if (file.Length > MediaImporter.MaxFileSize)
            {
                return Results.Json(
                    new Slv3LcdImportResponse
                    {
                        Error = true,
                        Msg = $"File too large (max {MediaImporter.MaxFileSize / 1024 / 1024} MB)",
                    },
                    AppJsonContext.Default.Slv3LcdImportResponse,
                    statusCode: 400);
            }

            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"nexus-lianli-lcd-{Guid.NewGuid()}{Path.GetExtension(file.FileName)}");
            try
            {
                using (var stream = File.Create(tempPath))
                {
                    await file.CopyToAsync(stream);
                }

                var result = await library.ImportAsync(tempPath, file.FileName, ParseCropField(form["crop"].ToString()));
                if (!result.Ok)
                {
                    return Results.Json(
                        new Slv3LcdImportResponse { Error = true, Msg = result.Error ?? "Import failed" },
                        AppJsonContext.Default.Slv3LcdImportResponse,
                        statusCode: 400);
                }

                return Results.Json(
                    new Slv3LcdImportResponse { MediaId = result.Item!.Id, Name = result.Item.Name, Kind = result.Item.Kind },
                    AppJsonContext.Default.Slv3LcdImportResponse);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }).DisableAntiforgery();

        app.MapGet("/devices/lianli-wireless/media", (Slv3LcdMediaLibrary library) =>
        {
            var response = new Slv3LcdMediaListResponse();
            foreach (var item in library.ListItems())
            {
                response.Items.Add(new Slv3LcdMediaDto { Id = item.Id, Name = item.Name, Kind = item.Kind });
            }
            return Results.Json(response, AppJsonContext.Default.Slv3LcdMediaListResponse);
        });

        // First-frame thumbnail; auth via ?token= (ExtractBearerOrQueryToken) so
        // an <img src> can load it without a header.
        app.MapGet("/devices/lianli-wireless/media/{id}/thumb", async (string id, Slv3LcdMediaLibrary library) =>
        {
            var path = library.GetThumbnailPath(id);
            if (path is null)
            {
                return Results.NotFound();
            }
            try
            {
                return Results.File(await File.ReadAllBytesAsync(path), "image/jpeg");
            }
            catch (IOException)
            {
                // A concurrent delete can remove the frame between the exists
                // check and the read.
                return Results.NotFound();
            }
        });

        app.MapDelete("/devices/lianli-wireless/media/{id}", (string id, Slv3LcdMediaLibrary library) =>
        {
            if (!MediaLibrary.IsValidId(id))
            {
                return Results.Json(ApiResponse.Fail("invalid id"), AppJsonContext.Default.ApiResponse, statusCode: 400);
            }
            library.DeleteItem(id);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    private static bool IsValidHexColor(string value)
    {
        if (value.Length != 7 || value[0] != '#')
        {
            return false;
        }
        for (var i = 1; i < 7; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
            {
                return false;
            }
        }
        return true;
    }

    // "x,y,w,h" or "x,y,w,h,rotate,mirror" normalized (0..1). Returns null
    // (import uncropped) when absent or malformed; clamps into range so a bad
    // rect can't escape the frame.
    private static Slv3LcdCropRect? ParseCropField(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var parts = raw.Split(',');
        if ((parts.Length != 4 && parts.Length != 6)
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
        {
            return null;
        }
        // TryParse accepts NaN/Infinity, and Math.Clamp is a pass-through on NaN,
        // so a non-finite x/y would survive into the ffmpeg filter.
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h))
        {
            return null;
        }
        var rotate = 0;
        var mirror = false;
        if (parts.Length == 6 && !CropRect.TryParseOrientation(parts[4], parts[5], out rotate, out mirror))
        {
            return null;
        }
        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);
        w = Math.Clamp(w, 0, 1 - x);
        h = Math.Clamp(h, 0, 1 - y);
        return w > 0 && h > 0 ? new Slv3LcdCropRect(x, y, w, h, rotate, mirror) : null;
    }
}
