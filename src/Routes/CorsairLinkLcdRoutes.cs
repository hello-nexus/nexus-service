using System;
using System.IO;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Media;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapCorsairLcdEndpoints(WebApplication app)
    {
        app.MapPost("/devices/corsair/lcd/media/upload", async (
            HttpContext ctx,
            CorsairLinkLcdMediaLibrary library) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.Json(
                    new LcdUploadResponse { Error = true, Msg = "Expected multipart/form-data" },
                    AppJsonContext.Default.LcdUploadResponse,
                    statusCode: 400);
            }

            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.Json(
                    new LcdUploadResponse { Error = true, Msg = "No file provided" },
                    AppJsonContext.Default.LcdUploadResponse,
                    statusCode: 400);
            }

            if (file.Length > MediaImporter.MaxFileSize)
            {
                return Results.Json(
                    new LcdUploadResponse
                    {
                        Error = true,
                        Msg = $"File too large (max {MediaImporter.MaxFileSize / 1024 / 1024} MB)",
                    },
                    AppJsonContext.Default.LcdUploadResponse,
                    statusCode: 400);
            }

            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"nexus-lcd-{Guid.NewGuid()}{Path.GetExtension(file.FileName)}");
            try
            {
                using (var stream = File.Create(tempPath))
                {
                    await file.CopyToAsync(stream);
                }

                // A missing or unparseable crop falls back to a centre square, not an error.
                CropRect? crop = CropRect.TryParse(form["crop"].ToString(), out var c) ? c : null;
                var result = await library.ImportAsync(tempPath, file.FileName, crop);
                if (!result.Ok)
                {
                    return Results.Json(
                        new LcdUploadResponse { Error = true, Msg = result.Error ?? "Import failed" },
                        AppJsonContext.Default.LcdUploadResponse,
                        statusCode: 400);
                }

                return Results.Json(
                    new LcdUploadResponse { Item = result.Item },
                    AppJsonContext.Default.LcdUploadResponse);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }).DisableAntiforgery();

        app.MapGet("/devices/corsair/lcd/media", (CorsairLinkLcdMediaLibrary library) =>
            Results.Json(
                new LcdMediaListResponse { Items = library.ListItems().ToArray() },
                AppJsonContext.Default.LcdMediaListResponse));

        app.MapDelete("/devices/corsair/lcd/media/{id}", (string id, CorsairLinkLcdMediaLibrary library) =>
        {
            if (!CorsairLinkLcdMediaLibrary.IsValidId(id))
            {
                return Results.Json(
                    ApiResponse.Fail("invalid id"),
                    AppJsonContext.Default.ApiResponse,
                    statusCode: 400);
            }
            library.DeleteItem(id);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPut("/devices/corsair/lcd/settings", (
            LcdSettingsRequest body,
            CorsairLinkLcd lcd,
            IConfigStore store) =>
        {
            if (body.Brightness.HasValue) lcd.SetBrightness(body.Brightness.Value);
            if (body.Rotation.HasValue) lcd.SetRotation(body.Rotation.Value);

            store.Update(s =>
            {
                if (body.MediaId is not null)
                {
                    s.Devices.Corsair.LcdSelectedMediaId = body.MediaId;
                }
                if (body.Brightness.HasValue)
                {
                    s.Devices.Corsair.LcdBrightness = body.Brightness.Value;
                }
                if (body.Rotation.HasValue)
                {
                    s.Devices.Corsair.LcdRotation = body.Rotation.Value;
                }
            });

            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // What the glass shows while Nexus is not running: a library item written into the
        // LCD's flash (MediaId), or the firmware's coolant screen (null).
        app.MapPut("/devices/corsair/lcd/device-screen", (
            LcdDeviceScreenRequest body,
            CorsairLinkLcd lcd,
            CorsairLinkHubs hubs,
            CorsairLinkLcdMediaLibrary library,
            Nexus.Service.Devices.DeviceControlGate gate,
            IConfigStore store) =>
        {
            // Writing flash and toggling host mode would take the glass from whoever owns it.
            if (!lcd.HasDevice || !gate.IsEnabled(CorsairLinkLcd.DeviceId) || hubs.LcdHub is { ForeignHostSeen: true })
            {
                return Results.Json(ApiResponse.Fail("The cooler screen is not under Nexus Control"), AppJsonContext.Default.ApiResponse, statusCode: 409);
            }
            if (body.MediaId is { } id)
            {
                var image = CorsairLinkLcdMediaLibrary.IsValidId(id) ? library.LoadImageData(id) : null;
                if (image is null)
                {
                    return Results.Json(ApiResponse.Fail("Media not found"), AppJsonContext.Default.ApiResponse, statusCode: 404);
                }
                var frames = new byte[image.Frames.Length][];
                var delays = new int[image.Frames.Length];
                for (var i = 0; i < frames.Length; i++)
                {
                    var frame = CorsairLinkLcdDeviceFile.ForDevice(image.Frames[i].JpegBytes);
                    if (frame is null)
                    {
                        return Results.Json(ApiResponse.Fail("The image could not be read"), AppJsonContext.Default.ApiResponse, statusCode: 422);
                    }
                    frames[i] = frame;
                    delays[i] = image.Frames[i].DelayMs;
                }
                var file = CorsairLinkLcdDeviceFile.Build(frames, CorsairLinkLcdDeviceFile.FrameMs(delays, frames.Length));
                if (file is null)
                {
                    return Results.Json(ApiResponse.Fail("Too large to store on the cooler"), AppJsonContext.Default.ApiResponse, statusCode: 413);
                }
                if (!lcd.StoreImage(file))
                {
                    return Results.Json(ApiResponse.Fail("The cooler screen did not accept the image"), AppJsonContext.Default.ApiResponse, statusCode: 502);
                }
            }

            // Persisted first: the LCD worker re-reads the setting into ShowsStoredImage.
            store.Update(s => s.Devices.Corsair.LcdDeviceMediaId = body.MediaId);
            if (!lcd.SelectHandBackScreen(storedImage: body.MediaId is not null))
            {
                return Results.Json(ApiResponse.Fail("The cooler screen did not accept the change"), AppJsonContext.Default.ApiResponse, statusCode: 502);
            }
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapGet("/devices/corsair/lcd/state", (CorsairLinkLcd lcd, IConfigStore store) =>
        {
            var s = store.Load().Devices.Corsair;
            return Results.Json(
                new LcdStateResponse
                {
                    HasDevice = lcd.HasDevice,
                    SelectedMediaId = s.LcdSelectedMediaId,
                    Brightness = s.LcdBrightness,
                    Rotation = s.LcdRotation,
                    DeviceMediaId = s.LcdDeviceMediaId,
                },
                AppJsonContext.Default.LcdStateResponse);
        });
    }
}

public sealed class LcdMediaListResponse
{
    public LcdMediaItem[] Items { get; set; } = Array.Empty<LcdMediaItem>();
}

public sealed class LcdUploadResponse
{
    public bool Error { get; set; }
    public string? Msg { get; set; }
    public LcdMediaItem? Item { get; set; }
}

public sealed class LcdSettingsRequest
{
    public string? MediaId { get; set; }
    public byte? Brightness { get; set; }
    public byte? Rotation { get; set; }
}

public sealed class LcdStateResponse
{
    public bool HasDevice { get; set; }
    public string? SelectedMediaId { get; set; }
    public byte Brightness { get; set; }
    public byte Rotation { get; set; }
    public string? DeviceMediaId { get; set; }
}

public sealed class LcdDeviceScreenRequest
{
    public string? MediaId { get; set; }
}
