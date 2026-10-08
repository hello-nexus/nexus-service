using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Media;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapHydroShift2CurveEndpoints(WebApplication app)
    {
        app.MapGet("/devices/lianli-hydroshift2-curve/head", (HydroShift2CurveBoard board) =>
        {
            var head = board.Head;
            return Results.Json(
                new HydroShift2CurveHeadResponse
                {
                    Connected = board.IsAvailable,
                    Tilt = head.Tilt,
                    Slide = head.Slide,
                    TargetTilt = head.TargetTilt,
                    TargetSlide = head.TargetSlide,
                    Moving = head.Moving,
                    Calibrating = head.Calibrating,
                    TiltMax = HydroShift2CurveProtocol.TiltMax,
                    SlideMin = HydroShift2CurveProtocol.SlideMin,
                    SlideMax = HydroShift2CurveProtocol.SlideMax,
                },
                AppJsonContext.Default.HydroShift2CurveHeadResponse);
        });

        app.MapPut("/devices/lianli-hydroshift2-curve/head", (HydroShift2CurveHeadRequest body, HydroShift2CurveBoard board) =>
        {
            board.SetHeadTarget(body.Tilt, body.Slide);
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapPost("/devices/lianli-hydroshift2-curve/head/recalibrate", (HydroShift2CurveBoard board) =>
        {
            if (!board.IsAvailable)
            {
                return Results.BadRequest(ApiResponse.Fail("not connected"));
            }
            board.Recalibrate();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapGet("/devices/lianli-hydroshift2-curve/settings", (HydroShift2CurvePlayer player, HydroShift2CurveBoard board, IConfigStore store) =>
        {
            var s = store.Load().Devices.HydroShift2Curve;
            return Results.Json(
                new HydroShift2CurveSettingsResponse
                {
                    Connected = player.IsConnected,
                    ScreenMode = s.ScreenMode,
                    Video = s.Video,
                    Playing = player.Playing,
                    ScreenSaverMinutes = s.ScreenSaverMinutes,
                    ScreenSaverVideo = s.ScreenSaverVideo,
                    ScreenSaverBrightness = s.ScreenSaverBrightness,
                    OfflineClock = s.OfflineClock == true,
                    PumpFollowsMotherboard = board.FollowsMotherboardWhenIdle,
                },
                AppJsonContext.Default.HydroShift2CurveSettingsResponse);
        });

        app.MapPut("/devices/lianli-hydroshift2-curve/settings", (HydroShift2CurveSettingsRequest body, HydroShift2CurvePlayer player, HydroShift2CurveMedia media, IConfigStore store) =>
        {
            if (body.ScreenMode is { } mode && mode is not (HydroShift2CurveSettings.ScreenNexus or HydroShift2CurveSettings.ScreenVideo))
            {
                return Results.BadRequest(ApiResponse.Fail("screenMode must be 'nexus' or 'video'"));
            }
            if (body.ScreenSaverMinutes is { } minutes && Array.IndexOf(ScreenSaverIntervals, minutes) < 0)
            {
                return Results.BadRequest(ApiResponse.Fail("screenSaverMinutes must be 0, 5, 10, 15, 30, 45 or 60"));
            }
            if (body.ScreenSaverBrightness is < 0 or > 100)
            {
                return Results.BadRequest(ApiResponse.Fail("screenSaverBrightness must be 0-100"));
            }
            if ((body.Video is { Length: > 0 } v && !media.Exists(v)) || (body.ScreenSaverVideo is { Length: > 0 } sv && !media.Exists(sv)))
            {
                return Results.BadRequest(ApiResponse.Fail("no such video"));
            }
            store.Update(s =>
            {
                var c = s.Devices.HydroShift2Curve;
                c.ScreenMode = body.ScreenMode ?? c.ScreenMode;
                c.Video = body.Video is null ? c.Video : body.Video.Length > 0 ? body.Video : null;
                c.ScreenSaverMinutes = body.ScreenSaverMinutes ?? c.ScreenSaverMinutes;
                c.ScreenSaverVideo = body.ScreenSaverVideo is null ? c.ScreenSaverVideo : body.ScreenSaverVideo.Length > 0 ? body.ScreenSaverVideo : null;
                c.ScreenSaverBrightness = body.ScreenSaverBrightness ?? c.ScreenSaverBrightness;
                c.OfflineClock = body.OfflineClock ?? c.OfflineClock;
                c.PumpFollowsMotherboard = body.PumpFollowsMotherboard ?? c.PumpFollowsMotherboard;
            });
            player.Wake();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        app.MapGet("/devices/lianli-hydroshift2-curve/media", (HydroShift2CurvePlayer player, HydroShift2CurveMedia media) =>
        {
            var (flip, mirror) = player.Mount();
            var items = new List<HydroShift2CurveMediaDto>();
            foreach (var item in media.List(flip, mirror))
            {
                var thumb = media.Thumbnail(item.Name);
                items.Add(new HydroShift2CurveMediaDto
                {
                    Name = item.Name,
                    Label = item.Label,
                    Thumb = thumb is null ? null : "data:image/jpeg;base64," + Convert.ToBase64String(thumb),
                    DurationSec = item.DurationSec,
                    Ready = item.Ready,
                });
            }
            return Results.Json(new HydroShift2CurveMediaResponse { Media = items }, AppJsonContext.Default.HydroShift2CurveMediaResponse);
        });

        app.MapPost("/devices/lianli-hydroshift2-curve/media", async (HttpContext ctx, HydroShift2CurvePlayer player, HydroShift2CurveMedia media) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return UploadResult(null, "Expected multipart/form-data");
            }
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.Count > 0 ? form.Files[0] : null;
            if (file is null || file.Length == 0)
            {
                return UploadResult(null, "No file provided");
            }
            var rawCrop = form["crop"].ToString();
            var crop = new CropRect(0, 0, 1, 1);
            if (rawCrop.Length > 0 && !CropRect.TryParse(rawCrop, out crop))
            {
                return UploadResult(null, "Invalid crop");
            }
            var label = form["label"].ToString();
            if (string.IsNullOrWhiteSpace(label))
            {
                label = Path.GetFileNameWithoutExtension(file.FileName);
            }
            var staged = Path.Combine(Path.GetTempPath(), $"nexus-curve-in-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
            try
            {
                await using (var stream = File.Create(staged))
                {
                    await file.CopyToAsync(stream, ctx.RequestAborted);
                }
                var name = await media.ImportAsync(staged, label, crop, ctx.RequestAborted);
                if (name is null)
                {
                    return UploadResult(null, "Could not read that video");
                }
                // Encoded off the request: the library lists it as not ready until done.
                var (flip, mirror) = player.Mount();
                _ = Task.Run(async () =>
                {
                    if (await media.EnsureVariantAsync(name, flip, mirror, CancellationToken.None) is null)
                    {
                        media.Delete(name);
                    }
                });
                return UploadResult(name, null);
            }
            finally
            {
                try { File.Delete(staged); } catch (IOException) { }
            }
        }).DisableAntiforgery();

        app.MapPost("/devices/lianli-hydroshift2-curve/media/delete", (HydroShift2CurveMediaDeleteRequest body, HydroShift2CurvePlayer player, HydroShift2CurveMedia media, IConfigStore store) =>
        {
            if (!HydroShift2CurveMedia.IsSafeName(body.Name) || !media.Delete(body.Name!))
            {
                return Results.BadRequest(ApiResponse.Fail("no such video"));
            }
            store.Update(s =>
            {
                var c = s.Devices.HydroShift2Curve;
                if (c.Video == body.Name)
                {
                    c.Video = null;
                    c.ScreenMode = HydroShift2CurveSettings.ScreenNexus;
                }
                if (c.ScreenSaverVideo == body.Name)
                {
                    c.ScreenSaverVideo = null;
                }
            });
            player.Wake();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    private static readonly int[] ScreenSaverIntervals = { 0, 5, 10, 15, 30, 45, 60 };

    private static IResult UploadResult(string? name, string? error) =>
        Results.Json(
            new HydroShift2CurveMediaUploadResponse { Error = error is not null, Msg = error, Name = name },
            AppJsonContext.Default.HydroShift2CurveMediaUploadResponse);
}

public sealed class HydroShift2CurveHeadResponse
{
    public bool Connected { get; set; }
    /// <summary>Degrees the head is believed to sit at; the motors have no position readback.</summary>
    public int Tilt { get; set; }
    /// <summary>Height, 0 being the middle of the slide's range.</summary>
    public int Slide { get; set; }
    public int TargetTilt { get; set; }
    public int TargetSlide { get; set; }
    public bool Moving { get; set; }
    public bool Calibrating { get; set; }
    public int TiltMax { get; set; }
    public int SlideMin { get; set; }
    public int SlideMax { get; set; }
}

public sealed class HydroShift2CurveHeadRequest
{
    public int? Tilt { get; set; }
    public int? Slide { get; set; }
}

public sealed class HydroShift2CurveSettingsResponse
{
    public bool Connected { get; set; }
    public string ScreenMode { get; set; } = HydroShift2CurveSettings.ScreenNexus;
    public string? Video { get; set; }
    /// <summary>Library item on the glass now (video mode or a running screen saver).</summary>
    public string? Playing { get; set; }
    public int ScreenSaverMinutes { get; set; }
    public string? ScreenSaverVideo { get; set; }
    public int ScreenSaverBrightness { get; set; }
    public bool OfflineClock { get; set; }
    public bool PumpFollowsMotherboard { get; set; }
}

public sealed class HydroShift2CurveSettingsRequest
{
    public string? ScreenMode { get; set; }
    /// <summary>Empty string clears it.</summary>
    public string? Video { get; set; }
    public int? ScreenSaverMinutes { get; set; }
    /// <summary>Empty string clears it.</summary>
    public string? ScreenSaverVideo { get; set; }
    public int? ScreenSaverBrightness { get; set; }
    public bool? OfflineClock { get; set; }
    public bool? PumpFollowsMotherboard { get; set; }
}

public sealed class HydroShift2CurveMediaDto
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>JPEG data URL.</summary>
    public string? Thumb { get; set; }
    public double? DurationSec { get; set; }
    /// <summary>Encoded for the head's current mount and playable.</summary>
    public bool Ready { get; set; }
}

public sealed class HydroShift2CurveMediaResponse
{
    public List<HydroShift2CurveMediaDto> Media { get; set; } = new();
}

public sealed class HydroShift2CurveMediaUploadResponse
{
    public bool Error { get; set; }
    public string? Msg { get; set; }
    public string? Name { get; set; }
}

public sealed class HydroShift2CurveMediaDeleteRequest
{
    public string? Name { get; set; }
}
