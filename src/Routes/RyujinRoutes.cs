using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
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
            if (ctx.Request.ContentLength is > RyujinMediaBodyLimit)
            {
                return Results.Json(ApiResponse.Fail(RyujinTooLargeMessage), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status413PayloadTooLarge);
            }
            if (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = RyujinMediaBodyLimit;
            }
            if (hub.Upload.State == "uploading" || !RyujinMediaGate.Wait(0))
            {
                return Results.Json(ApiResponse.Fail("A picture or GIF is already being processed"), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status409Conflict);
            }
            try
            {
                Microsoft.AspNetCore.Http.IFormCollection form;
                try
                {
                    form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                }
                catch (Exception ex) when (ex is Microsoft.AspNetCore.Http.BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } or InvalidDataException)
                {
                    return Results.Json(ApiResponse.Fail(RyujinTooLargeMessage), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status413PayloadTooLarge);
                }
                var file = form.Files.FirstOrDefault();
                if (file is null || file.Length == 0)
                {
                    return Results.BadRequest(ApiResponse.Fail("No file provided"));
                }
                if (file.Length > RyujinMediaBodyLimit)
                {
                    return Results.Json(ApiResponse.Fail(RyujinTooLargeMessage), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status413PayloadTooLarge);
                }
                var source = new byte[file.Length];
                await using (var input = file.OpenReadStream())
                {
                    await input.ReadExactlyAsync(source, ctx.RequestAborted);
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
                try
                {
                    await File.WriteAllBytesAsync(tmp, avi, ctx.RequestAborted);
                    File.Move(tmp, path, overwrite: true);
                }
                catch (IOException ex)
                {
                    ServiceLog.Warn($"[ryujin-upload] encoded file not saved: {ex.Message}");
                    return Results.Json(ApiResponse.Fail("The stored file is in use; try again in a moment"), AppJsonContext.Default.ApiResponse, statusCode: StatusCodes.Status409Conflict);
                }
                ServiceLog.Info($"[ryujin-upload] picked {file.Length} byte source as {encoded.Frames} frame(s), {avi.Length} byte AVI");
                var name = Path.GetFileName(file.FileName);
                if (name.Length > RyujinMediaNameMax)
                {
                    int cut = char.IsHighSurrogate(name[RyujinMediaNameMax - 1]) ? RyujinMediaNameMax - 1 : RyujinMediaNameMax;
                    name = name[..cut];
                }
                // A pick always uploads: UploadedSha and UploadFailedSha are cleared even when the bytes match, so the user can force a re-send.
                store.Update(s =>
                {
                    var d = s.Devices.RyujinDisplay;
                    d.MediaName = name;
                    d.MediaSha = sha;
                    d.UploadedSha = "";
                    d.UploadFailedSha = "";
                    d.ScreenMode = RyujinCoolingWorker.ModeImage;
                });
                hub.Upload = RyujinUploadState.Idle;
                hub.Wake.Release();
                return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
            }
            finally
            {
                RyujinMediaGate.Release();
            }
        }).DisableAntiforgery();
    }

    private const long RyujinMediaBodyLimit = 50L * 1024 * 1024;
    private const int RyujinMediaNameMax = 120;
    private static readonly string RyujinTooLargeMessage = $"File too large (max {RyujinMediaBodyLimit / 1024 / 1024} MB)";
    private static readonly SemaphoreSlim RyujinMediaGate = new(1, 1);

    internal static RyujinDisplayStateResponse BuildRyujinDisplayState(RyujinDisplaySettings settings, bool connected, RyujinUploadState? upload = null) => BuildRyujinDisplayStateCore(settings, connected, EffectiveUpload(settings, upload));

    private static RyujinUploadState EffectiveUpload(RyujinDisplaySettings settings, RyujinUploadState? upload) =>
        upload is { State: not "idle" } ? upload
        : settings.MediaSha.Length > 0 && settings.UploadFailedSha == settings.MediaSha ? new RyujinUploadState("failed", 0, 0, "the last upload of this file failed", RyujinCoolingWorker.ReasonFailed)
        : RyujinUploadState.Idle;

    private static RyujinDisplayStateResponse BuildRyujinDisplayStateCore(RyujinDisplaySettings settings, bool connected, RyujinUploadState upload) => new()
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
        UploadState = upload.State,
        UploadSent = upload.Sent,
        UploadTotal = upload.Total,
        UploadMessage = upload.Message,
        UploadReason = upload.Reason,
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
    public string UploadReason { get; set; } = "";
}
