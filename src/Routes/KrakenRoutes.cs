using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Klipy;
using Nexus.Service.Models;
using Nexus.Service.Models.Klipy;
using Nexus.Service.Peripherals.Nzxt;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapKrakenEndpoints(WebApplication app)
    {
        // GET /devices/nzxt-kraken/state
        app.MapGet("/devices/nzxt-kraken/state", (KrakenHub hub) =>
        {
            // Read the snapshot once; every field below uses this one reference.
            var snap = hub.Snapshot;
            var channels = new KrakenChannelDto[snap.Channels.Count];
            for (var i = 0; i < snap.Channels.Count; i++)
            {
                var c = snap.Channels[i];
                channels[i] = new KrakenChannelDto
                {
                    Id = KrakenHub.ZoneIdForChannelIndex(i),
                    AccessoryName = c.AccessoryName,
                    LedCount = c.LedCount,
                };
            }
            return Results.Json(
                new KrakenStateResponse
                {
                    IsConnected = hub.IsConnected,
                    FirmwareVersion = snap.FirmwareVersion,
                    LiquidTempC = Math.Round(snap.LiquidTempC, 1),
                    PumpRpm = snap.PumpRpm,
                    PumpDuty = snap.PumpDuty,
                    FanRpm = snap.FanRpm,
                    FanDuty = snap.FanDuty,
                    HasLcd = hub.HasLcd,
                    LcdWidth = hub.LcdWidth,
                    LcdHeight = hub.LcdHeight,
                    LcdGif = hub.HasLcd && hub.Model.FirmwareGif,
                    LcdBrightness = snap.LcdBrightness,
                    LcdOrientation = snap.LcdOrientationQuarterTurns * 90,
                    LcdMode = snap.DisplayMode switch
                    {
                        KrakenDisplayMode.Blank => "off",
                        KrakenDisplayMode.Bucket => "image",
                        _ => "liquid",
                    },
                    Channels = channels,
                },
                AppJsonContext.Default.KrakenStateResponse);
        });

        // PUT /devices/nzxt-kraken/lcd - backlight, rotation and display mode.
        app.MapPut("/devices/nzxt-kraken/lcd", async (KrakenLcdRequest body, KrakenHub hub, HttpContext ctx) =>
        {
            if (!hub.IsConnected)
            {
                return Results.BadRequest(ApiResponse.Fail("kraken not connected"));
            }

            var snap = hub.Snapshot;
            if (body.Brightness.HasValue || body.Orientation.HasValue)
            {
                if (body.Brightness is { } b && (b < 0 || b > 100))
                {
                    return Results.BadRequest(ApiResponse.Fail("brightness must be 0-100"));
                }
                int quarterTurns = snap.LcdOrientationQuarterTurns;
                if (body.Orientation is { } deg)
                {
                    if (deg != 0 && deg != 90 && deg != 180 && deg != 270)
                    {
                        return Results.BadRequest(ApiResponse.Fail("orientation must be 0, 90, 180 or 270"));
                    }
                    quarterTurns = deg / 90;
                }
                // Backlight and rotation share one command, so both always go together.
                var shownGif = hub.ShownLcdGif;
                if (!hub.SetLcdBacklight(body.Brightness ?? snap.LcdBrightness, quarterTurns))
                {
                    return Results.BadRequest(ApiResponse.Fail("failed to set lcd backlight"));
                }
                // The hub re-renders a still on rotation itself; a GIF needs ffmpeg, so it is redone here.
                if (shownGif != null && quarterTurns != snap.LcdOrientationQuarterTurns)
                {
                    byte[] rotated;
                    try
                    {
                        rotated = await KrakenGif.RotateAsync(shownGif, quarterTurns, ctx.RequestAborted).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        ServiceLog.Warn($"[nzxt-kraken] gif rotation failed: {ex.Message}");
                        return Results.BadRequest(ApiResponse.Fail("failed to rotate the gif"));
                    }
                    // Stale: a newer upload or rotation took the panel over and owns its orientation.
                    if (hub.UploadLcdGif(rotated, shownGif, quarterTurns, onlyIfShown: true) == KrakenGifUpload.Failed)
                    {
                        return Results.BadRequest(ApiResponse.Fail("lcd upload rejected by device"));
                    }
                }
            }

            if (body.Mode != null)
            {
                var mode = body.Mode switch
                {
                    "off" => KrakenDisplayMode.Blank,
                    "liquid" => KrakenDisplayMode.Liquid,
                    "image" => KrakenDisplayMode.Bucket,
                    _ => (KrakenDisplayMode?)null,
                };
                if (mode == null)
                {
                    return Results.BadRequest(ApiResponse.Fail("mode must be off, liquid or image"));
                }
                if (!hub.SetDisplayMode(mode.Value))
                {
                    return Results.BadRequest(ApiResponse.Fail("failed to set lcd mode"));
                }
            }
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // POST /devices/nzxt-kraken/lcd/image - raw RGBA body, exactly one LCD frame.
        app.MapPost("/devices/nzxt-kraken/lcd/image", async (HttpRequest request, KrakenHub hub) =>
        {
            if (!hub.IsConnected)
            {
                return Results.BadRequest(ApiResponse.Fail("kraken not connected"));
            }
            if (!hub.HasLcd)
            {
                return Results.BadRequest(ApiResponse.Fail("lcd bulk pipe unavailable"));
            }

            var expected = hub.LcdFrameBytes;
            var buffer = new byte[expected];
            var read = 0;
            while (read < expected)
            {
                var n = await request.Body.ReadAsync(buffer.AsMemory(read, expected - read)).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }
                read += n;
            }
            if (read != expected)
            {
                return Results.BadRequest(ApiResponse.Fail(
                    $"expected {expected} bytes of RGBA ({hub.LcdWidth}x{hub.LcdHeight}), got {read}"));
            }
            // Guard against a body longer than one frame rather than silently truncating.
            if (await request.Body.ReadAsync(new byte[1].AsMemory(0, 1)).ConfigureAwait(false) != 0)
            {
                return Results.BadRequest(ApiResponse.Fail($"body longer than one {expected}-byte frame"));
            }

            return hub.UploadLcdImage(buffer)
                ? Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse)
                : Results.BadRequest(ApiResponse.Fail("lcd upload rejected by device"));
        });

        // POST /devices/nzxt-kraken/lcd/gif - raw GIF (or any clip ffmpeg reads); fitted to the panel here.
        app.MapPost("/devices/nzxt-kraken/lcd/gif", async (HttpRequest request, KrakenHub hub) =>
        {
            if (GifUnavailable(hub) is { } refused)
            {
                return refused;
            }
            var source = Path.Combine(Path.GetTempPath(), $"nexus-kraken-{Guid.NewGuid():N}.src");
            try
            {
                long total = 0;
                await using (var file = File.Create(source))
                {
                    var buffer = new byte[81920];
                    int n;
                    while ((n = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted).ConfigureAwait(false)) > 0)
                    {
                        total += n;
                        if (total > KrakenGif.MaxSourceBytes)
                        {
                            return Results.BadRequest(ApiResponse.Fail($"gif larger than {KrakenGif.MaxSourceBytes} bytes"));
                        }
                        await file.WriteAsync(buffer.AsMemory(0, n), request.HttpContext.RequestAborted).ConfigureAwait(false);
                    }
                }
                if (total == 0)
                {
                    return Results.BadRequest(ApiResponse.Fail("empty body"));
                }
                return await ShowGifAsync(hub, source, request.HttpContext.RequestAborted).ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(source); }
                catch { }
            }
        });

        // POST /devices/nzxt-kraken/lcd/klipy - { slug } from a Klipy search in this process.
        app.MapPost("/devices/nzxt-kraken/lcd/klipy", async (
            KlipyImportRequest body, KrakenHub hub, IKlipyCatalog catalog, HttpContext ctx) =>
        {
            if (GifUnavailable(hub) is { } refused)
            {
                return refused;
            }
            if (!KlipyCatalog.IsValidSlug(body.Slug))
            {
                return Results.BadRequest(ApiResponse.Fail("invalid slug"));
            }
            var source = await catalog.DownloadAsync(body.Slug, Path.GetTempPath(), ctx.RequestAborted).ConfigureAwait(false);
            if (source == null)
            {
                return Results.BadRequest(ApiResponse.Fail("download failed"));
            }
            try
            {
                var result = await ShowGifAsync(hub, source, ctx.RequestAborted).ConfigureAwait(false);
                _ = catalog.TriggerShareAsync(body.Slug);
                return result;
            }
            finally
            {
                try { File.Delete(source); }
                catch { }
            }
        });

        // GET /devices/nzxt-kraken/firmware-lighting - the animations the cooler can play
        // on its own, plus what was last written to each channel. The cooler cannot be
        // asked what it is playing, so the per-channel values come from settings.
        app.MapGet("/devices/nzxt-kraken/firmware-lighting", (KrakenHub hub, IConfigStore store) =>
        {
            var settings = store.Load();
            var saved = settings.Devices.KrakenFirmwareLighting;
            var uncontrolled = settings.Devices.UncontrolledLightingDevices;

            var effects = new KrakenEffectDto[KrakenEffects.All.Length];
            for (var i = 0; i < KrakenEffects.All.Length; i++)
            {
                var e = KrakenEffects.All[i];
                effects[i] = new KrakenEffectDto
                {
                    Id = e.Id,
                    MinColors = e.MinColors,
                    MaxColors = e.MaxColors,
                    Directional = e.Directional,
                };
            }

            var snap = hub.Snapshot;
            var channels = new KrakenFirmwareChannelDto[snap.Channels.Count];
            for (var i = 0; i < snap.Channels.Count; i++)
            {
                var zoneId = KrakenHub.ZoneIdForChannelIndex(i);
                saved.TryGetValue(zoneId, out var cfg);
                channels[i] = new KrakenFirmwareChannelDto
                {
                    Id = zoneId,
                    AccessoryName = snap.Channels[i].AccessoryName,
                    Effect = cfg?.Effect ?? "fixed",
                    Speed = cfg?.Speed ?? 2,
                    Forward = cfg?.Forward ?? true,
                    Colors = cfg?.Colors?.ToArray() ?? new[] { "#ff0000" },
                    NexusDriven = !uncontrolled.Contains(zoneId),
                };
            }

            return Results.Json(
                new KrakenFirmwareLightingResponse
                {
                    IsConnected = hub.IsConnected,
                    Effects = effects,
                    Channels = channels,
                },
                AppJsonContext.Default.KrakenFirmwareLightingResponse);
        });

        // PUT /devices/nzxt-kraken/firmware-lighting - write one channel's animation.
        app.MapPut("/devices/nzxt-kraken/firmware-lighting", (KrakenFirmwareLightingRequest body, KrakenHub hub, IConfigStore store) =>
        {
            if (!hub.IsConnected)
            {
                return Results.Conflict(ApiResponse.Fail("kraken not connected"));
            }

            // One snapshot for both the lookup and the channel id: re-reading hub.Snapshot
            // lets a detach empty the list between them and index past the end.
            var channels = hub.Snapshot.Channels;
            var channelIndex = IndexOfChannel(channels, body.Channel);
            if (channelIndex < 0)
            {
                return Results.BadRequest(ApiResponse.Fail("unknown channel"));
            }

            var effect = KrakenEffects.Find(body.Effect);
            if (effect is null)
            {
                return Results.BadRequest(ApiResponse.Fail($"unknown effect '{body.Effect}'"));
            }

            var hex = body.Colors ?? Array.Empty<string>();
            if (hex.Length < effect.MinColors || hex.Length > Math.Max(effect.MaxColors, effect.MinColors))
            {
                return Results.BadRequest(ApiResponse.Fail(
                    $"effect '{effect.Id}' takes {effect.MinColors} to {effect.MaxColors} colours, got {hex.Length}"));
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

            var speed = (KrakenAnimationSpeed)Math.Clamp(body.Speed, 0, 4);
            var channelId = channels[channelIndex].ChannelId;
            if (!hub.SetLighting(channelId, effect.Mode, speed, rgb, body.Forward))
            {
                return Results.Problem("Failed to write the animation to the cooler.");
            }

            var zoneId = KrakenHub.ZoneIdForChannelIndex(channelIndex);
            store.Update(s =>
            {
                s.Devices.KrakenFirmwareLighting[zoneId] = new KrakenFirmwareLighting
                {
                    Effect = effect.Id,
                    Speed = (int)speed,
                    Forward = body.Forward,
                    Colors = new List<string>(hex),
                };
            });

            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });
    }

    private static int IndexOfChannel(IReadOnlyList<KrakenLightingChannel> channels, string? zoneId)
    {
        for (var i = 0; i < channels.Count; i++)
        {
            if (string.Equals(KrakenHub.ZoneIdForChannelIndex(i), zoneId, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    private static bool TryParseHexColor(string? input, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        if (text[0] == '#')
        {
            text = text.Substring(1);
        }
        if (text.Length != 6)
        {
            return false;
        }
        return byte.TryParse(text.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out r)
            && byte.TryParse(text.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out g)
            && byte.TryParse(text.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out b);
    }
}

public static partial class DevicesRoutes
{
    private static IResult? GifUnavailable(KrakenHub hub)
    {
        if (!hub.IsConnected)
        {
            return Results.BadRequest(ApiResponse.Fail("kraken not connected"));
        }
        if (!hub.HasLcd)
        {
            return Results.BadRequest(ApiResponse.Fail("lcd bulk pipe unavailable"));
        }
        return hub.Model.FirmwareGif ? null : Results.BadRequest(ApiResponse.Fail("this kraken does not play gifs"));
    }

    private static async Task<IResult> ShowGifAsync(KrakenHub hub, string sourcePath, System.Threading.CancellationToken ct)
    {
        try
        {
            var fitted = await KrakenGif.FitAsync(sourcePath, hub.LcdWidth, hub.LcdHeight, ct).ConfigureAwait(false);
            // A rotation that lands during the conversion makes the upload Stale; redo it.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int turns = hub.Snapshot.LcdOrientationQuarterTurns;
                var wire = await KrakenGif.RotateAsync(fitted, turns, ct).ConfigureAwait(false);
                if (wire.Length == 0 || wire.Length > KrakenGif.MaxWireBytes)
                {
                    return Results.BadRequest(ApiResponse.Fail($"converted gif is {wire.Length} bytes; the pump takes up to {KrakenGif.MaxWireBytes}"));
                }
                switch (hub.UploadLcdGif(wire, fitted, turns))
                {
                    case KrakenGifUpload.Shown:
                        return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
                    case KrakenGifUpload.Failed:
                        return Results.BadRequest(ApiResponse.Fail("lcd upload rejected by device"));
                }
            }
            return Results.BadRequest(ApiResponse.Fail("the screen kept rotating during the upload"));
        }
        catch (InvalidOperationException ex)
        {
            ServiceLog.Warn($"[nzxt-kraken] gif conversion failed: {ex.Message}");
            return Results.BadRequest(ApiResponse.Fail("could not read that file as an animation"));
        }
    }
}

public sealed class KrakenChannelDto
{
    public string Id { get; set; } = "";
    public string AccessoryName { get; set; } = "";
    public int LedCount { get; set; }
}

public sealed class KrakenStateResponse
{
    public bool IsConnected { get; set; }
    public string FirmwareVersion { get; set; } = "";
    public double LiquidTempC { get; set; }
    public int PumpRpm { get; set; }
    public int PumpDuty { get; set; }
    public int FanRpm { get; set; }
    public int FanDuty { get; set; }
    public bool HasLcd { get; set; }
    public int LcdWidth { get; set; }
    public int LcdHeight { get; set; }
    /// <summary>True when the pump animates an uploaded GIF itself.</summary>
    public bool LcdGif { get; set; }
    public int LcdBrightness { get; set; }
    public int LcdOrientation { get; set; }
    public string LcdMode { get; set; } = "";
    public KrakenChannelDto[] Channels { get; set; } = Array.Empty<KrakenChannelDto>();
}

public sealed class KrakenLcdRequest
{
    public int? Brightness { get; set; }
    public int? Orientation { get; set; }
    public string? Mode { get; set; }
}

public sealed class KrakenEffectDto
{
    public string Id { get; set; } = "";
    public int MinColors { get; set; }
    public int MaxColors { get; set; }
    public bool Directional { get; set; }
}

public sealed class KrakenFirmwareChannelDto
{
    public string Id { get; set; } = "";
    public string AccessoryName { get; set; } = "";
    public string Effect { get; set; } = "";
    public int Speed { get; set; }
    public bool Forward { get; set; }
    public string[] Colors { get; set; } = Array.Empty<string>();
    /// <summary>True while Nexus still pushes frames to this channel, which overrides the animation.</summary>
    public bool NexusDriven { get; set; }
}

public sealed class KrakenFirmwareLightingResponse
{
    public bool IsConnected { get; set; }
    public KrakenEffectDto[] Effects { get; set; } = Array.Empty<KrakenEffectDto>();
    public KrakenFirmwareChannelDto[] Channels { get; set; } = Array.Empty<KrakenFirmwareChannelDto>();
}

public sealed class KrakenFirmwareLightingRequest
{
    public string Channel { get; set; } = "";
    public string Effect { get; set; } = "";
    public int Speed { get; set; } = 2;
    public bool Forward { get; set; } = true;
    public string[]? Colors { get; set; }
}
