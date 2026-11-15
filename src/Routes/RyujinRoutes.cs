using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Media;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.Ryujin;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapRyujinEndpoints(WebApplication app)
    {
        app.MapGet("/devices/asus-ryujin-extreme/display", (IConfigStore store, RyujinCoolingHub hub) =>
            Results.Json(BuildRyujinDisplayState(store.Load().Devices.RyujinDisplay, hub.IsConnected, hub.Upload), AppJsonContext.Default.RyujinDisplayStateResponse));

        app.MapPut("/devices/asus-ryujin-extreme/display", (RyujinDisplayRequest body, IConfigStore store, RyujinCoolingHub hub) =>
        {
            if (ValidateRyujinDisplay(body) is { } error)
            {
                return Results.BadRequest(ApiResponse.Fail(error));
            }
            if (body.ScreenMode == RyujinCoolingWorker.ModeImage && store.Load().Devices.RyujinDisplay.MediaSha.Length == 0)
            {
                return Results.BadRequest(ApiResponse.Fail("pick a picture or GIF first"));
            }
            store.Update(s => ApplyRyujinDisplay(s.Devices.RyujinDisplay, body));
            hub.Wake.Release();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPost("/devices/asus-ryujin-extreme/display/media", async (HttpContext ctx, IConfigStore store, RyujinCoolingHub hub) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.BadRequest(ApiResponse.Fail("Expected multipart/form-data"));
            }
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(ApiResponse.Fail("No file provided"));
            }
            if (file.Length > MediaImporter.MaxFileSize)
            {
                return Results.BadRequest(ApiResponse.Fail($"File too large (max {MediaImporter.MaxFileSize / 1024 / 1024} MB)"));
            }
            if (hub.Upload.State == "uploading")
            {
                return Results.BadRequest(ApiResponse.Fail("An upload is already running"));
            }
            byte[] source;
            using (var buffer = new MemoryStream())
            {
                await file.CopyToAsync(buffer, ctx.RequestAborted);
                source = buffer.ToArray();
            }
            var encoded = await RyujinMediaEncoder.EncodeAsync(source, Path.GetExtension(file.FileName), ctx.RequestAborted);
            if (!encoded.Ok)
            {
                return Results.BadRequest(ApiResponse.Fail(encoded.Error ?? "Conversion failed"));
            }
            var avi = encoded.Avi!;
            var sha = Convert.ToHexString(SHA256.HashData(avi));
            var path = RyujinCoolingWorker.MediaPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, avi, ctx.RequestAborted);
            File.Move(tmp, path, overwrite: true);
            ServiceLog.Info($"[ryujin-upload] picked {file.Length} byte source as {encoded.Frames} frame(s), {avi.Length} byte AVI");
            // A pick always uploads: UploadedSha is cleared even when the bytes match, so the user can force a re-send.
            store.Update(s =>
            {
                var d = s.Devices.RyujinDisplay;
                d.MediaName = Path.GetFileName(file.FileName);
                d.MediaSha = sha;
                d.UploadedSha = "";
                d.ScreenMode = RyujinCoolingWorker.ModeImage;
            });
            hub.Upload = RyujinUploadState.Idle;
            hub.Wake.Release();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        }).DisableAntiforgery();
    }

    internal static RyujinDisplayStateResponse BuildRyujinDisplayState(RyujinDisplaySettings settings, bool connected, RyujinUploadState? upload = null) => new()
    {
        IsConnected = connected,
        Brightness = settings.Brightness,
        Rotation = settings.Rotation,
        SensorCount = RyujinMonitorSensors.NormalizeCount(settings.SensorCount),
        Slots = new List<string>(RyujinMonitorSensors.NormalizeSlots(settings.Slots)),
        AvailableSensors = new List<string>(RyujinMonitorSensors.Available),
        ScreenMode = settings.ScreenMode == RyujinCoolingWorker.ModeImage ? RyujinCoolingWorker.ModeImage : RyujinCoolingWorker.ModeMonitor,
        MediaName = settings.MediaName,
        MediaHeld = settings.MediaSha.Length > 0 && settings.MediaSha == settings.UploadedSha,
        UploadState = (upload ?? RyujinUploadState.Idle).State,
        UploadSent = (upload ?? RyujinUploadState.Idle).Sent,
        UploadTotal = (upload ?? RyujinUploadState.Idle).Total,
        UploadMessage = (upload ?? RyujinUploadState.Idle).Message,
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
        if (body.ScreenMode is { } mode && mode is not (RyujinCoolingWorker.ModeMonitor or RyujinCoolingWorker.ModeImage))
        {
            return "screenMode must be monitor or image";
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
        if (body.ScreenMode is { } screenMode)
        {
            target.ScreenMode = screenMode;
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
    public string? ScreenMode { get; set; }
}

public sealed class RyujinDisplayStateResponse
{
    public bool IsConnected { get; set; }
    public int Brightness { get; set; }
    public int Rotation { get; set; }
    public int SensorCount { get; set; }
    public List<string> Slots { get; set; } = new();
    public List<string> AvailableSensors { get; set; } = new();
    public string ScreenMode { get; set; } = RyujinCoolingWorker.ModeMonitor;
    public string MediaName { get; set; } = "";
    public bool MediaHeld { get; set; }
    public string UploadState { get; set; } = "idle";
    public int UploadSent { get; set; }
    public int UploadTotal { get; set; }
    public string UploadMessage { get; set; } = "";
}
