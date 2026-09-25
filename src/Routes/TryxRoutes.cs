using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Media;
using Nexus.Service.Panel;
using Nexus.Service.Peripherals.Tryx.Panorama;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

/// <summary>One overlay item: a live sensor identity plus its normalized (0..1)
/// justification anchor (top of the value text). Shared shape for the request
/// body and the status snapshot.</summary>
public sealed class TryxOverlayItem
{
    public string SensorId { get; set; } = "";
    public string Device { get; set; } = "";
    public string Label { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class TryxOverlaySnapshot
{
    public TryxOverlayItem[] Items { get; set; } = [];
    public string Font { get; set; } = "";
    public int Size { get; set; } = 100;
    public string Color { get; set; } = "";
    public string Align { get; set; } = "";
    public bool Docked { get; set; }
}

/// <summary>Slideshow settings, shared shape for the request body and the status snapshot.</summary>
public sealed class TryxSlideshowSnapshot
{
    public bool Enabled { get; set; }
    public int IntervalSec { get; set; } = TryxSlideshowConfig.DefaultIntervalSec;
    public bool Shuffle { get; set; }
    public bool FinishVideos { get; set; } = true;
}

public sealed class TryxStatusResponse
{
    public bool Connected { get; set; }
    public TryxPanoramaState? State { get; set; }
    // Bytes stored on the panel from its media list, and the file count.
    public long MediaUsedBytes { get; set; }
    public int MediaFileCount { get; set; }
    // Fixed per-model capacity (TryxPanoramaHub.MediaCapacityBytes); the single source of
    // truth the web reads instead of hardcoding its own copy of the spec value.
    public long MediaCapacityBytes { get; set; }
    public TryxOverlaySnapshot? Overlay { get; set; }
    public TryxSlideshowSnapshot? Slideshow { get; set; }
}

public sealed class TryxMediaImportResponse
{
    public bool Error { get; set; }
    public string? Msg { get; set; }
    public string? Media { get; set; }
}

public sealed class TryxPresetItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Cover thumbnail as a base64 data URL, or null if none is bundled.</summary>
    public string? Thumb { get; set; }
}

public sealed class TryxPresetListResponse
{
    public List<TryxPresetItem> Presets { get; set; } = new();
}

public sealed class TryxMediaItem
{
    public string Name { get; set; } = "";
    public string? Thumb { get; set; }
    public double DurationSec { get; set; }
    /// <summary>Human display name for media Nexus did not upload (from Kanali's library),
    /// or null; the web shows this instead of the raw device filename.</summary>
    public string? Label { get; set; }
}

public sealed class TryxMediaListResponse
{
    public List<TryxMediaItem> Media { get; set; } = new();
}

/// <summary>Generic ack body for Tryx control routes.</summary>
public sealed class TryxAckResponse
{
    public bool Ok { get; set; }
    public string? Msg { get; set; }
}

public sealed class TryxCloudMaterialDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string CoverUrl { get; set; } = "";
    public bool Installed { get; set; }
}

public sealed class TryxCloudCatalogResponse
{
    public List<TryxCloudMaterialDto> Materials { get; set; } = new();
}

public sealed class TryxCloudInstallRequest
{
    public int Id { get; set; }
}

public sealed class TryxEnableRequest
{
    public bool Enable { get; set; }
}

public sealed class TryxBrightnessRequest
{
    public int Value { get; set; }
}

public sealed class TryxPresetRequest
{
    public string Id { get; set; } = "";
}

public sealed class TryxMediaSelectRequest
{
    public string Name { get; set; } = "";
}

public sealed class TryxMediaDeleteRequest
{
    public string Name { get; set; } = "";
}

public sealed class TryxOverlayRequest
{
    public TryxOverlayItem[] Items { get; set; } = [];
    public string Font { get; set; } = "";
    public int Size { get; set; } = 100;
    public string Color { get; set; } = "";
    public string Align { get; set; } = "";
    public bool Docked { get; set; }
}

/// <summary>
/// First-party Tryx Panorama control routes. Calls into the shared
/// <see cref="TryxPanoramaHub"/> singleton; the dashboard device page talks to
/// these directly (no SDK dispatch layer).
/// </summary>
public static class TryxRoutes
{
    // The Panorama's factory wallpapers: Kanali's RK PresetDefaultSet nameEn values
    // for the 2240x1080 panel. Kanali ships a second PresetDefaultSet with 21
    // "default_NN.mp4.h264_640x480" entries (Sally, Lazybara, ...) - that is a
    // different product's screen; those names do not apply to the Panorama.
    private static readonly (string Id, string Name)[] KnownPresets =
    {
        ("default_01", "Cooling delivery"),
        ("default_02", "Migration"),
        ("default_03", "Exo-Ecologie"),
        ("default_04", "Cyber Bunker"),
        ("default_05", "Edge of Dream"),
        ("default_06", "Thermal Energy·Prohibited"),
    };

    public static void MapTryxEndpoints(this WebApplication app)
    {
        app.MapGet("/tryx/status", (TryxPanoramaHub hub) =>
        {
            var ov = hub.Overlay;
            var resp = new TryxStatusResponse
            {
                Connected = hub.IsConnected,
                State = hub.State,
                MediaUsedBytes = hub.MediaUsedBytes,
                MediaFileCount = hub.AvailableMediaFilenames.Count,
                MediaCapacityBytes = TryxPanoramaHub.MediaCapacityBytes,
                Overlay = new TryxOverlaySnapshot
                {
                    Items = BuildOverlayItems(ov),
                    Font = ov.Font,
                    Size = ov.Size,
                    Color = ov.Color,
                    Align = ov.Align,
                    Docked = ov.Docked,
                },
                Slideshow = BuildSlideshowSnapshot(hub.Slideshow),
            };
            return Results.Json(resp, AppJsonContext.Default.TryxStatusResponse);
        });

        app.MapPost("/tryx/slideshow", (TryxSlideshowSnapshot body, TryxPanoramaHub hub) =>
        {
            hub.SetSlideshow(new TryxSlideshowConfig
            {
                Enabled = body.Enabled,
                IntervalSec = body.IntervalSec,
                Shuffle = body.Shuffle,
                FinishVideos = body.FinishVideos,
            });
            return Results.Json(new TryxAckResponse { Ok = true }, AppJsonContext.Default.TryxAckResponse);
        });

        app.MapPost("/tryx/enable", (TryxEnableRequest body, TryxPanoramaHub hub) =>
        {
            var ok = hub.SetEnabled(body.Enable);
            return Results.Json(new TryxAckResponse { Ok = ok }, AppJsonContext.Default.TryxAckResponse);
        });

        app.MapPost("/tryx/brightness", (TryxBrightnessRequest body, TryxPanoramaHub hub) =>
        {
            if (body.Value < 0 || body.Value > 100)
            {
                return Results.Json(
                    new TryxAckResponse { Ok = false, Msg = "value must be 0..100" },
                    AppJsonContext.Default.TryxAckResponse);
            }
            var ok = hub.SetBrightness(body.Value);
            return Results.Json(new TryxAckResponse { Ok = ok }, AppJsonContext.Default.TryxAckResponse);
        });

        app.MapGet("/tryx/presets", (TryxPanoramaHub hub) =>
        {
            var resp = new TryxPresetListResponse { Presets = ResolveAvailablePresets(hub.AvailableMediaIds) };
            return Results.Json(resp, AppJsonContext.Default.TryxPresetListResponse);
        });

        app.MapGet("/tryx/cloud/catalog", async (TryxPanoramaHub hub, CancellationToken ct) =>
        {
            var resp = new TryxCloudCatalogResponse();
            try
            {
                foreach (var m in await hub.GetCloudCatalogAsync(ct))
                {
                    resp.Materials.Add(new TryxCloudMaterialDto
                    {
                        Id = m.Id,
                        Name = m.Name,
                        // Covers stream through the service so the dashboard needs no
                        // cross-origin image permission for the CDN.
                        CoverUrl = $"/tryx/cloud/cover/{m.Id}",
                        Installed = hub.IsCloudInstalled(m.Id),
                    });
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[tryx] cloud catalog fetch failed: {ex.GetType().Name}: {ex.Message}");
            }
            return Results.Json(resp, AppJsonContext.Default.TryxCloudCatalogResponse);
        });

        app.MapGet("/tryx/cloud/cover/{id:int}", async (int id, TryxPanoramaHub hub, CancellationToken ct) =>
        {
            try
            {
                var stream = await hub.OpenCloudCoverAsync(id, ct);
                return stream is null ? Results.NotFound() : Results.Stream(stream, "image/jpeg");
            }
            catch (Exception)
            {
                return Results.NotFound();
            }
        });

        app.MapPost("/tryx/cloud/install", async (TryxCloudInstallRequest body, TryxPanoramaHub hub, CancellationToken ct) =>
        {
            try
            {
                var (ok, msg) = await hub.InstallCloudMaterialAsync(body.Id, ct);
                return Results.Json(new TryxAckResponse { Ok = ok, Msg = msg }, AppJsonContext.Default.TryxAckResponse);
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[tryx] cloud install {body.Id} failed: {ex.GetType().Name}: {ex.Message}");
                return Results.Json(new TryxAckResponse { Ok = false, Msg = "install failed" }, AppJsonContext.Default.TryxAckResponse);
            }
        });

        app.MapPost("/tryx/preset", (TryxPresetRequest body, TryxPanoramaHub hub) =>
        {
            if (string.IsNullOrEmpty(body.Id))
            {
                return Results.Json(
                    new TryxAckResponse { Ok = false, Msg = "missing id" },
                    AppJsonContext.Default.TryxAckResponse);
            }
            // The id maps directly to the on-panel wallpaper filename, so any stored
            // wallpaper is selectable: a built-in default_NN or an installed cloud
            // download_NN. Selecting media the panel does not have is a silent no-op
            // there, so no availability gate is needed (the panel's media list is only
            // reported on a cold boot, which made the old gate reject valid picks).
            if (!TryxThumbnailCache.IsSafeDeviceName(body.Id))
            {
                return Results.Json(
                    new TryxAckResponse { Ok = false, Msg = "unknown preset" },
                    AppJsonContext.Default.TryxAckResponse);
            }
            var ok = hub.SetPreset(TryxRkProtocol.PresetMediaFile(body.Id));
            return Results.Json(new TryxAckResponse { Ok = ok }, AppJsonContext.Default.TryxAckResponse);
        });

        app.MapGet("/tryx/media", (TryxPanoramaHub hub) =>
        {
            hub.RefreshMediaList();
            var names = ListMediaFiles(hub);
            var resp = new TryxMediaListResponse();
            foreach (var name in names)
            {
                var thumb = TryxThumbnailCache.ReadDataUrl(name);
                string? label = null;
                // Media the panel holds but Nexus never uploaded (Kanali cloud themes /
                // prior uploads) has no cached frame; source the cover + name from Kanali,
                // else decode the first frame off the panel for a later read.
                if (thumb is null)
                {
                    var kanali = TryxKanaliData.Lookup(name);
                    if (kanali is { } k) { thumb = k.Thumb; label = k.DisplayName; }
                    if (thumb is null) hub.QueueThumbnail(name);
                }
                else
                {
                    label = TryxKanaliData.DisplayName(name);
                }
                resp.Media.Add(new TryxMediaItem
                {
                    Name = name,
                    Thumb = thumb,
                    DurationSec = TryxThumbnailCache.ReadDuration(name),
                    Label = label,
                });
            }
            return Results.Json(resp, AppJsonContext.Default.TryxMediaListResponse);
        });

        app.MapPost("/tryx/media/select", (TryxMediaSelectRequest body, TryxPanoramaHub hub) =>
        {
            if (string.IsNullOrEmpty(body.Name))
            {
                return Results.Json(
                    new TryxAckResponse { Ok = false, Msg = "missing name" },
                    AppJsonContext.Default.TryxAckResponse);
            }
            var ok = hub.SelectCustomMedia(body.Name);
            return Results.Json(new TryxAckResponse { Ok = ok }, AppJsonContext.Default.TryxAckResponse);
        });

        app.MapPost("/tryx/media/delete", (TryxMediaDeleteRequest body, TryxPanoramaHub hub) =>
        {
            if (string.IsNullOrEmpty(body.Name))
            {
                return Results.Json(
                    new TryxAckResponse { Ok = false, Msg = "missing name" },
                    AppJsonContext.Default.TryxAckResponse);
            }
            if (!TryxThumbnailCache.IsSafeDeviceName(body.Name))
            {
                return Results.Json(
                    new TryxAckResponse { Ok = false, Msg = "invalid name" },
                    AppJsonContext.Default.TryxAckResponse);
            }
            var ack = DeleteMediaFile(hub, body.Name);
            return Results.Json(ack, AppJsonContext.Default.TryxAckResponse);
        });

        app.MapPost("/tryx/overlay", (TryxOverlayRequest body, TryxPanoramaHub hub) =>
        {
            var cfg = BuildOverlayConfigFromRequest(body, hub.Overlay);
            var ok = hub.SetOverlay(cfg);
            return Results.Json(new TryxAckResponse { Ok = ok }, AppJsonContext.Default.TryxAckResponse);
        });

        app.MapGet("/tryx/media/file", async (string? name, TryxPanoramaHub hub, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(name) || !TryxThumbnailCache.IsSafeDeviceName(name))
            {
                return Results.NotFound();
            }

            if (TryxMediaStore.Exists(name))
            {
                return Results.File(TryxMediaStore.Path(name), "video/mp4", enableRangeProcessing: true);
            }

            if (!hub.IsConnected || string.IsNullOrEmpty(hub.State.AdbSerial))
            {
                return Results.NotFound();
            }

            try
            {
                var pulled = await hub.EnsureLocalCopyAsync(name, ct);
                if (!pulled)
                {
                    return Results.NotFound();
                }
                return Results.File(TryxMediaStore.Path(name), "video/mp4", enableRangeProcessing: true);
            }
            catch (Exception)
            {
                return Results.NotFound();
            }
        }).DisableAntiforgery();

        app.MapPost("/tryx/media", async (HttpContext ctx, TryxPanoramaHub hub) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return Results.Json(
                    new TryxMediaImportResponse { Error = true, Msg = "Expected multipart/form-data" },
                    AppJsonContext.Default.TryxMediaImportResponse);
            }
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.Count > 0 ? form.Files[0] : null;
            if (file is null || file.Length == 0)
            {
                return Results.Json(
                    new TryxMediaImportResponse { Error = true, Msg = "No file provided" },
                    AppJsonContext.Default.TryxMediaImportResponse);
            }
            if (!hub.IsConnected)
            {
                return Results.Json(
                    new TryxMediaImportResponse { Error = true, Msg = "Tryx Panorama not connected" },
                    AppJsonContext.Default.TryxMediaImportResponse);
            }
            // Optional crop ("x,y,w,h" normalized 0..1) + target size from the dashboard
            // cropper; applied in the transcode. Defaults to the panel's native 2:1 surface, no crop.
            var crop = ParseCrop(form["crop"].ToString());
            var targetW = ParseInt(form["targetWidth"].ToString(), 858);
            var targetH = ParseInt(form["targetHeight"].ToString(), 428);

            var ext = Path.GetExtension(file.FileName);
            var tempInput = Path.Combine(Path.GetTempPath(), $"nexus-tryx-in-{Guid.NewGuid()}{ext}");
            try
            {
                using (var s = File.Create(tempInput))
                {
                    await file.CopyToAsync(s);
                }
                var (ok, msg) = await hub.ImportAndPlayVideoAsync(tempInput, file.FileName, crop, targetW, targetH, ctx.RequestAborted);
                if (!ok)
                {
                    return Results.Json(
                        new TryxMediaImportResponse
                        {
                            Error = true,
                            Msg = string.IsNullOrEmpty(msg) ? "Import failed (transcode, push, or verification error)" : msg,
                        },
                        AppJsonContext.Default.TryxMediaImportResponse);
                }
                return Results.Json(
                    new TryxMediaImportResponse { Media = hub.State.CurrentMedia },
                    AppJsonContext.Default.TryxMediaImportResponse);
            }
            finally
            {
                try { File.Delete(tempInput); } catch { /* best effort */ }
            }
        }).DisableAntiforgery();
    }

    private static readonly HashSet<string> ValidOverlayAligns = new(StringComparer.Ordinal) { "left", "center", "right" };

    /// <summary>Maps the fixed overlay wire contract (items/font/size/color/align/docked)
    /// onto a <see cref="TryxOverlayConfig"/>, validating each field; Filter/Opacity are
    /// not part of this contract, so <paramref name="current"/>'s values pass through
    /// unchanged.</summary>
    internal static TryxOverlayConfig BuildOverlayConfigFromRequest(TryxOverlayRequest body, TryxOverlayConfig current)
    {
        var items = new List<TryxOverlaySensorItem>();
        foreach (var item in body.Items ?? [])
        {
            if (item is null || string.IsNullOrWhiteSpace(item.SensorId)) continue;
            items.Add(new TryxOverlaySensorItem
            {
                SensorId = item.SensorId,
                // A JSON body can carry an explicit null here despite the non-nullable
                // C# type (System.Text.Json overrides the property initializer), which
                // would otherwise throw downstream in AppendOverlayWidget's UTF8 encode.
                Device = item.Device ?? "",
                Label = item.Label ?? "",
                X = Math.Clamp(item.X, 0.0, 1.0),
                Y = Math.Clamp(item.Y, 0.0, 1.0),
            });
            if (items.Count >= 4) break;
        }
        return new TryxOverlayConfig
        {
            Items = items,
            Color = string.IsNullOrWhiteSpace(body.Color) ? "#ffffff" : body.Color,
            Align = ValidOverlayAligns.Contains(body.Align) ? body.Align : "left",
            Filter = current.Filter,
            Opacity = current.Opacity,
            Font = TryxRkProtocol.IsValidOverlayFont(body.Font) ? body.Font : "roboto-regular",
            Size = Math.Clamp(body.Size, 50, 150),
            Docked = body.Docked,
        };
    }

    internal static TryxSlideshowSnapshot BuildSlideshowSnapshot(TryxSlideshowConfig slideshow) => new()
    {
        Enabled = slideshow.Enabled,
        IntervalSec = slideshow.IntervalSec,
        Shuffle = slideshow.Shuffle,
        FinishVideos = slideshow.FinishVideos,
    };

    internal static TryxOverlayItem[] BuildOverlayItems(TryxOverlayConfig overlay)
    {
        var items = new TryxOverlayItem[overlay.Items.Count];
        for (var i = 0; i < overlay.Items.Count; i++)
        {
            var item = overlay.Items[i];
            items[i] = new TryxOverlayItem
            {
                SensorId = item.SensorId,
                Device = item.Device,
                Label = item.Label,
                X = item.X,
                Y = item.Y,
            };
        }
        return items;
    }

    private static TryxVideoCrop? ParseCrop(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(',');
        if (parts.Length != 4 && parts.Length != 6) return null;
        if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) &&
            double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
        {
            var rotate = 0;
            var mirror = false;
            if (parts.Length == 6 && !CropRect.TryParseOrientation(parts[4], parts[5], out rotate, out mirror))
            {
                return null;
            }
            return new TryxVideoCrop(x, y, w, h, rotate, mirror);
        }
        return null;
    }

    private static int ParseInt(string raw, int fallback)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;

    // The RK panel's stored-media list arrives asynchronously (pushed on connect,
    // parsed by the transport's drain loop); before it arrives, or on a transport
    // that never reports one, fall back to the panel's minimum factory set so the
    // picker is never empty and never offers a cloud-only preset the panel lacks.
    private const int FallbackPresetCount = 6;

    internal static List<TryxPresetItem> ResolveAvailablePresets(IReadOnlyList<string> availableMediaIds)
    {
        var items = new List<TryxPresetItem>();
        if (availableMediaIds.Count == 0)
        {
            for (var i = 0; i < KnownPresets.Length && i < FallbackPresetCount; i++)
            {
                var (id, name) = KnownPresets[i];
                items.Add(new TryxPresetItem { Id = id, Name = name, Thumb = TryxPresetThumbs.DataUrl(id) });
            }
            return items;
        }

        var available = new HashSet<string>(availableMediaIds, StringComparer.Ordinal);
        foreach (var (id, name) in KnownPresets)
        {
            if (available.Remove(id))
            {
                items.Add(new TryxPresetItem { Id = id, Name = name, Thumb = TryxPresetThumbs.DataUrl(id) });
            }
        }
        // A panel-reported default_NN without a catalog name (added by a firmware or
        // cloud update) stays selectable under its raw id rather than disappearing;
        // non-wallpaper entries (start, screensaver) stay hidden.
        var unnamed = new List<string>();
        foreach (var id in available)
        {
            if (id.StartsWith("default_", StringComparison.Ordinal))
            {
                unnamed.Add(id);
            }
        }
        unnamed.Sort(StringComparer.Ordinal);
        foreach (var id in unnamed)
        {
            items.Add(new TryxPresetItem { Id = id, Name = id, Thumb = TryxPresetThumbs.DataUrl(id) });
        }
        return items;
    }

    private static List<string> ListMediaFiles(TryxPanoramaHub hub)
    {
        var adbSerial = hub.State.AdbSerial;
        // RK firmware (usbprint, no adb): the hub's library, which the slideshow cycles too.
        if (string.IsNullOrEmpty(adbSerial)) return hub.ListCustomMedia();
        // Legacy serial firmware: enumerate /sdcard/pcMedia over adb, plus the thumbnail record.
        var files = TryxThumbnailCache.ListCustomMedia();
        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null) return files;
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = $"-s {adbSerial} shell ls /sdcard/pcMedia/",
                WorkingDirectory = Path.GetDirectoryName(adbPath) ?? string.Empty,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return files;
            p.WaitForExit(8_000);
            foreach (var line in p.StandardOutput.ReadToEnd().Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                // The panel's pcMedia dir is shared with any tool that ever drove
                // it (e.g. Kanali), which leaves non-video files behind. The app
                // only plays video, so list video files only.
                var lower = trimmed.ToLowerInvariant();
                if (lower.EndsWith(".mp4") || lower.EndsWith(".mov") || lower.EndsWith(".webm")
                    || lower.EndsWith(".mkv") || lower.EndsWith(".m4v") || lower.EndsWith(".avi"))
                {
                    files.Add(trimmed);
                }
            }
        }
        catch { /* adb unavailable */ }
        return files;
    }

    private static TryxAckResponse DeleteMediaFile(TryxPanoramaHub hub, string name)
    {
        var adbSerial = hub.State.AdbSerial;
        if (string.IsNullOrEmpty(adbSerial))
        {
            // RK firmware (usbprint, no adb): actually remove the on-panel file with the
            // file_remove command. RemoveDeviceMedia updates the used-bytes accounting on
            // success; drop the local thumbnail/duration record only if the panel accepted it.
            var removed = hub.RemoveDeviceMedia(name);
            if (removed) TryxThumbnailCache.Delete(name);
            return new TryxAckResponse { Ok = removed, Msg = removed ? null : "panel remove failed" };
        }
        var adbPath = AdbLocator.ResolveAdbPath();
        if (adbPath is null)
        {
            return new TryxAckResponse { Ok = false, Msg = "adb not found" };
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = $"-s {adbSerial} shell rm /sdcard/pcMedia/{name}",
                WorkingDirectory = Path.GetDirectoryName(adbPath) ?? string.Empty,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null)
            {
                return new TryxAckResponse { Ok = false, Msg = "failed to start adb" };
            }
            p.WaitForExit(8_000);
            if (p.ExitCode == 0)
            {
                TryxThumbnailCache.Delete(name);
                hub.RecordMediaDeleted(name);
            }
            return new TryxAckResponse { Ok = p.ExitCode == 0 };
        }
        catch (Exception ex)
        {
            return new TryxAckResponse { Ok = false, Msg = ex.Message };
        }
    }
}
