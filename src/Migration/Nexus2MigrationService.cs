using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Gallery;
using Nexus.Service.Media;
using Nexus.Service.Models.Gallery;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Routes;
using Nexus.Service.Sockets;

namespace Nexus.Service.Migration;

/// <summary>Preview (read-only) and apply orchestration for the Nexus 2
/// personalization import. Preview composes Nexus2Translator output into the
/// wire report; Apply drives the live services (panel records, gallery,
/// background media, settings) per selected category.</summary>
public sealed class Nexus2MigrationService
{
    private readonly INexus2ConfigReader _reader;
    private readonly PanelDeviceRegistry _panels;
    private readonly GalleryLibrary _gallery;
    private readonly PanelBgLibrary _bgLibrary;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;
    private readonly IServiceProvider _services;

    public Nexus2MigrationService(
        INexus2ConfigReader reader, PanelDeviceRegistry panels, GalleryLibrary gallery,
        PanelBgLibrary bgLibrary, IConfigStore store, MultiplexHub hub, IServiceProvider services)
    {
        _reader = reader;
        _panels = panels;
        _gallery = gallery;
        _bgLibrary = bgLibrary;
        _store = store;
        _hub = hub;
        _services = services;
    }

    public Nexus2PreviewResponse Preview()
    {
        using var read = _reader.Read();
        if (read is null)
        {
            return new Nexus2PreviewResponse { Available = false };
        }

        var root = read.Document.RootElement;
        var profile = Nexus2Translator.FindActiveProfile(root);
        var y70 = profile is { } p ? GetY70(p) : null;
        var q60Software = profile is { } p2 ? GetQ60Software(p2) : null;

        var response = new Nexus2PreviewResponse
        {
            Available = true,
            ProfileName = profile is { } p3 ? Nexus2Json.GetString(p3, "name") : null,
        };

        response.Categories.Add(BuildAppearanceCategory(y70, read.ConfigDir));
        response.Categories.Add(BuildY70LayoutCategory(y70));
        response.Categories.Add(BuildQ60FaceCategory(q60Software));
        response.Categories.Add(BuildWallpapersCategory(q60Software, read.ConfigDir));
        response.Categories.Add(BuildGallerySourcesCategory(y70));
        response.Categories.Add(BuildRotationCategory(root));
        response.Categories.Add(BuildLanguageCategory(root));
        return response;
    }

    private Nexus2PreviewCategoryDto BuildAppearanceCategory(JsonElement? y70, string configDir)
    {
        var cat = new Nexus2PreviewCategoryDto { Id = "appearance" };
        if (y70 is not { } y)
        {
            return cat;
        }
        var appearance = Nexus2Y70Translator.TranslateAppearance(y, File.Exists);
        cat.Available = appearance.Available;
        cat.AccentColor = appearance.AccentHex;
        cat.Background = appearance.Background switch
        {
            Nexus2Y70BackgroundSource.File => Path.GetFileName(appearance.BackgroundPath),
            Nexus2Y70BackgroundSource.BundledParticles => "particles",
            Nexus2Y70BackgroundSource.Gradient => "gradient",
            _ => null,
        };
        // Matched by file name, as the apply's library lookup does.
        var uploadNames = Y70Uploads(configDir).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shownIsUpload = appearance.BackgroundPath is { } shownPath && uploadNames.Contains(Path.GetFileName(shownPath));
        var shownImports = cat.Background is not null && !shownIsUpload
            && (appearance.Background != Nexus2Y70BackgroundSource.BundledParticles
                || AsarCandidates(configDir, _services.GetService<INexus2Detector>()?.Detect().InstallRoot).Any(File.Exists));
        cat.Count = uploadNames.Count + (shownImports ? 1 : 0);
        return cat;
    }

    private static Nexus2PreviewCategoryDto BuildY70LayoutCategory(JsonElement? y70)
    {
        var cat = new Nexus2PreviewCategoryDto { Id = "y70Layout", DroppedTypes = new List<string>() };
        if (y70 is not { } y)
        {
            return cat;
        }
        var layout = Nexus2Y70Translator.TranslateLayout(y);
        cat.Available = true;
        cat.Pages = layout.Pages;
        cat.Widgets = layout.Widgets;
        cat.MappedWidgets = layout.MappedWidgets;
        cat.DroppedTypes = layout.DroppedTypes;
        return cat;
    }

    private static Nexus2PreviewCategoryDto BuildQ60FaceCategory(JsonElement? q60Software)
    {
        var cat = new Nexus2PreviewCategoryDto { Id = "q60Face" };
        if (q60Software is not { } qs)
        {
            return cat;
        }
        var face = Nexus2Q60Translator.TranslateFace(qs);
        cat.Available = face.Available;
        cat.Face = face.ActiveWidgetType;
        cat.StashedFaces = face.StashedConfigs.Count;
        return cat;
    }

    private static Nexus2PreviewCategoryDto BuildWallpapersCategory(JsonElement? q60Software, string configDir)
    {
        var cat = new Nexus2PreviewCategoryDto { Id = "wallpapers" };
        if (q60Software is not { } qs)
        {
            return cat;
        }
        var wallpaper = Nexus2Q60Translator.TranslateWallpaper(qs, configDir, File.Exists);
        var uploads = Q60Uploads(configDir);
        var slideshow = wallpaper.Playlist && uploads.Count > 0;
        // A playlist leaves the shown wallpaper out, and an upload of the same name is reused, as the apply does.
        var shownIsUpload = wallpaper.Available && uploads.Exists(u => string.Equals(Path.GetFileName(u), wallpaper.FileName, StringComparison.OrdinalIgnoreCase));
        cat.Count = uploads.Count + (wallpaper.Available && !slideshow && !shownIsUpload ? 1 : 0);
        cat.Available = cat.Count > 0;
        cat.SlideshowIntervalSec = slideshow ? wallpaper.PlaylistIntervalSec : null;
        return cat;
    }

    private static Nexus2PreviewCategoryDto BuildGallerySourcesCategory(JsonElement? y70)
    {
        var cat = new Nexus2PreviewCategoryDto { Id = "gallerySources" };
        if (y70 is not { } y)
        {
            return cat;
        }
        var sources = Nexus2Y70Translator.TranslateGallerySources(y, File.Exists);
        cat.Available = sources.Available;
        cat.Count = sources.ExistingPaths.Count;
        cat.Missing = sources.Missing;
        return cat;
    }

    private static Nexus2PreviewCategoryDto BuildRotationCategory(JsonElement root)
    {
        var value = Nexus2Translator.TranslateRotation(root);
        return new Nexus2PreviewCategoryDto { Id = "rotation", Available = value is not null, Value = value };
    }

    private static Nexus2PreviewCategoryDto BuildLanguageCategory(JsonElement root)
    {
        var value = Nexus2Translator.TranslateLanguage(root);
        return new Nexus2PreviewCategoryDto { Id = "language", Available = value is not null, Value = value };
    }

    public async Task<Nexus2ApplyResponse> ApplyAsync(IReadOnlyList<string> categories, bool replaceCustomizedLayout)
    {
        var response = new Nexus2ApplyResponse();
        using var read = _reader.Read();
        if (read is null)
        {
            foreach (var id in categories)
            {
                response.Results.Add(Fail(id, "no-config"));
            }
            return response;
        }

        var root = read.Document.RootElement;
        var profile = Nexus2Translator.FindActiveProfile(root);
        var y70 = profile is { } p ? GetY70(p) : null;
        var q60Software = profile is { } p2 ? GetQ60Software(p2) : null;
        var y70Record = FindSingleInstanceRecord(PanelSurfaces.Y70);
        var q60Record = FindSingleInstanceRecord(PanelSurfaces.Q60);

        var appliedAny = false;
        foreach (var id in categories)
        {
            var result = id switch
            {
                "appearance" => await ApplyAppearanceAsync(y70, read.ConfigDir, y70Record),
                "y70Layout" => ApplyY70Layout(y70, y70Record, replaceCustomizedLayout),
                "q60Face" => ApplyQ60Face(q60Software, q60Record, replaceCustomizedLayout),
                "wallpapers" => await ApplyWallpaperAsync(q60Software, read.ConfigDir, q60Record),
                "gallerySources" => ApplyGallerySources(y70),
                "rotation" => ApplyRotation(root),
                "language" => ApplyLanguage(root),
                _ => Skip(id, "unknown-category"),
            };
            if (result.Status == "applied")
            {
                appliedAny = true;
            }
            response.Results.Add(result);
        }

        if (appliedAny)
        {
            // Double-check-under-lock, same shape as FanProfiles.SeedDefaultPresetCurves:
            // avoids a redundant write when two apply calls race.
            if (!_store.Load().Nexus2MigrationCompleted)
            {
                _store.Update(s =>
                {
                    if (!s.Nexus2MigrationCompleted)
                    {
                        s.Nexus2MigrationCompleted = true;
                    }
                });
            }
            PanelTopics.BroadcastPrefs(_hub);
            PanelTopics.BroadcastLighting(_hub);
            PanelTopics.BroadcastCooling(_hub);
            // Registry patches do not notify; an open panel page refetches its record on this topic.
            foreach (var record in new[] { y70Record, q60Record })
            {
                if (record is not null)
                {
                    PanelTopics.BroadcastPanelDevice(_hub, record.Id);
                }
            }
        }
        return response;
    }

    private async Task<Nexus2ApplyResultDto> ApplyAppearanceAsync(JsonElement? y70, string configDir, PanelDeviceRecord? y70Record)
    {
        const string id = "appearance";
        if (y70 is not { } y)
        {
            return Fail(id, "no-y70-data");
        }
        if (y70Record is null)
        {
            return Skip(id, "no-y70-record");
        }
        var appearance = Nexus2Y70Translator.TranslateAppearance(y, File.Exists);
        if (!appearance.Available)
        {
            return Skip(id, "no-theme-data");
        }

        // A panel left on "sync with desktop" (the unset default) ignores its own accent.
        var patch = new PanelDevicePatch
        {
            AccentColor = appearance.AccentHex,
            AccentSyncWithDesktop = appearance.AccentHex is null ? null : false,
            WidgetOpacity = appearance.WidgetOpacity,
        };
        string? detail = null;
        // Uploads first, so the shown one lands at its name position in the library and is then reused.
        var (uploadsImported, uploadsComplete) = await ImportY70UploadsAsync(configDir, y70Record.Id);
        if (!uploadsComplete)
        {
            detail = "background-not-imported";
        }
        if (appearance.Background != Nexus2Y70BackgroundSource.None)
        {
            var item = await ImportY70BackgroundAsync(appearance, configDir, y70Record);
            if (item is null)
            {
                detail = "background-not-imported";
            }
            else
            {
                // The wallpaper and desktop backdrops both hide the panel's own background layer.
                patch.Backdrop = "theme";
                patch.BackgroundMode = "media";
                patch.BackgroundMediaId = item.Id;
                patch.BackgroundMediaType = item.Type;
                patch.BackgroundMediaAlpha = item.Alpha;
                patch.BackgroundMediaSlideshow = false;
                patch.BackgroundOpacity = 1;
            }
        }
        if (appearance.Transparent)
        {
            patch.Backdrop = "desktop";
        }
        if (patch.AccentColor is null && patch.WidgetOpacity is null && patch.BackgroundMediaId is null && patch.Backdrop is null && uploadsImported == 0)
        {
            return Skip(id, detail ?? "nothing-to-apply");
        }
        return _panels.Patch(y70Record.Id, patch) is not null
            ? new Nexus2ApplyResultDto { Id = id, Status = "applied", Detail = detail }
            : Fail(id, "patch-failed");
    }

    /// <summary>A library item holding exactly what Nexus 2 shows on the Y70, reusing one an earlier import copied.</summary>
    private async Task<PanelBgItem?> ImportY70BackgroundAsync(Nexus2AppearanceResult appearance, string configDir, PanelDeviceRecord record)
    {
        var deviceId = record.Id;
        if (FfmpegResolver.Path is null)
        {
            return null;
        }

        var still = appearance.BackgroundStill;
        string? tempPath = null;
        try
        {
            string name;
            if (appearance.Background == Nexus2Y70BackgroundSource.Gradient)
            {
                var gradient = appearance.BackgroundGradient!;
                var w = record.Capabilities?.CssWidth is > 0 and var cw ? cw : Y70CssWidth;
                var h = record.Capabilities?.CssHeight is > 0 and var ch ? ch : Y70CssHeight;
                name = $"nexus2-gradient-{GradientKey(gradient, w, h)}.jpg";
                if (FindImported(deviceId, name, still: true, sourceLength: 0) is { } existingGradient)
                {
                    return existingGradient;
                }
                tempPath = Path.Combine(Path.GetTempPath(), $"nexus2-bg-{Guid.NewGuid():N}.bmp");
                gradient.WriteBmp(tempPath, w, h);
                return (await PanelBgImporter.ImportStillAsync(_bgLibrary, deviceId, tempPath, name)).Item;
            }
            if (appearance.Background == Nexus2Y70BackgroundSource.File)
            {
                var source = appearance.BackgroundPath!;
                name = Path.GetFileName(source);
                if (FindImported(deviceId, name, still, new FileInfo(source).Length) is { } existing)
                {
                    return existing;
                }
                tempPath = Path.Combine(Path.GetTempPath(), $"nexus2-bg-{Guid.NewGuid():N}{Path.GetExtension(name)}");
                Nexus2ReadOnlyIo.CopyTo(source, tempPath);
            }
            else
            {
                var installRoot = _services.GetService<INexus2Detector>()?.Detect().InstallRoot;
                if (FindParticlesEntries(configDir, installRoot) is not { } found)
                {
                    return null;
                }
                foreach (var entry in found.Entries)
                {
                    if (FindImported(deviceId, Path.GetFileName(entry.Path), still, entry.Size) is { } existing)
                    {
                        return existing;
                    }
                }
                if (await ExtractPortraitAsync(found.Asar, found.Entries) is not { } particles)
                {
                    return null;
                }
                (tempPath, name) = particles;
            }

            var result = still
                ? await PanelBgImporter.ImportStillAsync(_bgLibrary, deviceId, tempPath, name)
                : await PanelBgImporter.ImportAsIsAsync(_bgLibrary, deviceId, tempPath, name);
            return result.Item;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[nexus2] Y70 background import failed: {ex.Message}");
            return null;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>Adds every upload Nexus 2's Y70 background picker lists, not only the shown one.</summary>
    private async Task<(int Imported, bool Complete)> ImportY70UploadsAsync(string configDir, string deviceId)
    {
        var uploads = Y70Uploads(configDir);
        if (uploads.Count == 0)
        {
            return (0, true);
        }
        if (FfmpegResolver.Path is null)
        {
            return (0, false);
        }
        var imported = 0;
        var complete = true;
        foreach (var source in uploads)
        {
            var name = Path.GetFileName(source);
            if (FindImported(deviceId, name, still: false, FileLength(source)) is not null)
            {
                continue;
            }
            var tempPath = Path.Combine(Path.GetTempPath(), $"nexus2-bg-{Guid.NewGuid():N}{Path.GetExtension(name)}");
            try
            {
                Nexus2ReadOnlyIo.CopyTo(source, tempPath);
                if ((await PanelBgImporter.ImportAsIsAsync(_bgLibrary, deviceId, tempPath, name)).Item is not null)
                {
                    imported++;
                }
                else
                {
                    complete = false;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[nexus2] Y70 upload import failed: {ex.Message}");
                complete = false;
            }
            finally
            {
                TryDelete(tempPath);
            }
        }
        return (imported, complete);
    }

    /// <summary>The folders Nexus 2's Y70 background picker reads, minus the *.static.webp stills it keeps beside videos.</summary>
    private static List<string> Y70Uploads(string configDir)
    {
        var files = new List<string>();
        foreach (var kind in new[] { "media", "images" })
        {
            var dir = Path.Combine(configDir, "user-media", kind, "custom-Y70-bg");
            try
            {
                if (Directory.Exists(dir))
                {
                    files.AddRange(Directory.EnumerateFiles(dir)
                        .Where(f => !f.EndsWith(".static.webp", StringComparison.OrdinalIgnoreCase) && Nexus2Y70Translator.IsShowableFile(f)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[nexus2] Y70 uploads unreadable: {ex.Message}");
            }
        }
        // Name-descending, as Nexus 2's picker sorts them (its names end in a Date.now() stamp, so newest first): the grid shows import order.
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(b), Path.GetFileName(a)));
        return files;
    }

    // The Y70 kiosk's CSS box, used when the record has not reported its own yet.
    private const int Y70CssWidth = 734;
    private const int Y70CssHeight = 2560;

    /// <summary>Stable across runs (unlike string.GetHashCode), so a re-import finds the drawn image.</summary>
    private static string GradientKey(Nexus2Gradient gradient, int w, int h)
    {
        var text = new System.Text.StringBuilder(FormattableString.Invariant($"{gradient.AngleDeg}|{w}x{h}"));
        foreach (var stop in gradient.Stops)
        {
            text.Append(FormattableString.Invariant($"|{stop.R},{stop.G},{stop.B}@{stop.At}"));
        }
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>An item an earlier import made from the same file; an unconverted copy must also match its byte size.</summary>
    private PanelBgItem? FindImported(string deviceId, string name, bool still, long sourceLength) =>
        _bgLibrary.ListItems(deviceId).Find(i => i.Name == name && (still
            ? i.Type == "static" && i.MediaExt is null
            : i.MediaExt is { } ext && FileLength(_bgLibrary.GetMediaPath(deviceId, i.Id, ext)) == sourceLength));

    private static long FileLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return -1; }
    }

    private static (string Asar, List<Nexus2AsarEntry> Entries)? FindParticlesEntries(string configDir, string? installRoot)
    {
        foreach (var asar in AsarCandidates(configDir, installRoot))
        {
            if (!File.Exists(asar))
            {
                continue;
            }
            var entries = Nexus2AsarReader.Find(asar,
                n => n.StartsWith("particles-", StringComparison.Ordinal) && n.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
            if (entries.Count > 0)
            {
                return (asar, entries);
            }
        }
        return null;
    }

    /// <summary>Extracts the Y70 particles video to a temp file: of the particles-*.mp4 entries it is
    /// the portrait one (the other is the desktop-app background).</summary>
    private static async Task<(string Path, string Name)?> ExtractPortraitAsync(string asar, List<Nexus2AsarEntry> entries)
    {
        foreach (var entry in entries)
        {
            var temp = Path.Combine(Path.GetTempPath(), $"nexus2-bg-{Guid.NewGuid():N}.mp4");
            try
            {
                Nexus2AsarReader.Extract(asar, entry, temp);
                var probe = await PanelBgImporter.ProbeAsync(temp);
                if (probe.Height > probe.Width)
                {
                    return (temp, Path.GetFileName(entry.Path));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[nexus2] particles extract failed: {ex.Message}");
            }
            TryDelete(temp);
        }
        return null;
    }

    /// <summary>The uninstaller's folder first (wherever the user installed), then the per-user default
    /// beside &lt;profile&gt;\AppData\Roaming\HYTE Nexus (configDir), then the all-users default.</summary>
    internal static IEnumerable<string> AsarCandidates(string configDir, string? installRoot)
    {
        if (!string.IsNullOrEmpty(installRoot))
        {
            yield return Path.Combine(installRoot, "resources", "app.asar");
        }
        var appData = Path.GetDirectoryName(Path.GetDirectoryName(configDir));
        if (!string.IsNullOrEmpty(appData))
        {
            yield return Path.Combine(appData, "Local", "Programs", Nexus2UninstallRules.InstallDirName, "resources", "app.asar");
        }
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            yield return Path.Combine(programFiles, Nexus2UninstallRules.InstallDirName, "resources", "app.asar");
        }
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }
        try { File.Delete(path); } catch { /* temp file */ }
    }

    private Nexus2ApplyResultDto ApplyY70Layout(JsonElement? y70, PanelDeviceRecord? y70Record, bool replaceCustomizedLayout)
    {
        const string id = "y70Layout";
        if (y70 is not { } y)
        {
            return Fail(id, "no-y70-data");
        }
        if (y70Record is null)
        {
            return Skip(id, "no-y70-record");
        }
        var translated = Nexus2Y70Translator.TranslateLayout(y);
        if (translated.Layout is null)
        {
            return Skip(id, "no-widgets");
        }

        // A null Layout is the only state PanelLayoutDefaults seeds from -
        // any non-null Layout was written by a user edit (or an earlier
        // import), so presence alone is the customization signal.
        if (y70Record.Layout is not null && !replaceCustomizedLayout)
        {
            return new Nexus2ApplyResultDto { Id = id, Status = "needsConfirm", Detail = "layout-customized" };
        }

        return _panels.Patch(y70Record.Id, new PanelDevicePatch { Layout = translated.Layout }) is not null
            ? Applied(id)
            : Fail(id, "patch-failed");
    }

    private Nexus2ApplyResultDto ApplyQ60Face(JsonElement? q60Software, PanelDeviceRecord? q60Record, bool replaceCustomizedLayout)
    {
        const string id = "q60Face";
        if (q60Software is not { } qs)
        {
            return Fail(id, "no-q60-data");
        }
        if (q60Record is null)
        {
            return Skip(id, "no-q60-record");
        }
        var face = Nexus2Q60Translator.TranslateFace(qs);
        if (!face.Available)
        {
            return Skip(id, "no-pages");
        }

        // Same customization signal as ApplyY70Layout: PanelLayoutDefaults
        // never writes its seed back to the record, so a non-null Layout only
        // happens via a real user edit or an earlier import.
        if (q60Record.Layout is not null && !replaceCustomizedLayout)
        {
            return new Nexus2ApplyResultDto { Id = id, Status = "needsConfirm", Detail = "layout-customized" };
        }

        var layout = q60Record.Layout ?? PanelLayoutDefaults.ForSurface(PanelSurfaces.Q60);
        if (face.ActiveWidgetType is not null)
        {
            if (layout.Pages.Count == 0)
            {
                layout.Pages.Add(new PanelPageDto { Id = Guid.NewGuid().ToString() });
            }
            var page = layout.Pages[0];
            var existingId = page.Widgets.Count > 0 ? page.Widgets[0].Id : Guid.NewGuid().ToString();
            var newWidget = new PanelWidgetDto { Id = existingId, Type = face.ActiveWidgetType, Size = "2x4", Config = face.ActiveConfig };
            if (page.Widgets.Count > 0)
            {
                page.Widgets[0] = newWidget;
            }
            else
            {
                page.Widgets.Add(newWidget);
            }
        }
        if (face.StashedConfigs.Count > 0)
        {
            layout.SingleWidgetConfigs ??= new Dictionary<string, Dictionary<string, JsonElement>>();
            foreach (var kv in face.StashedConfigs)
            {
                layout.SingleWidgetConfigs[kv.Key] = kv.Value;
            }
        }

        var patch = new PanelDevicePatch { Layout = layout };
        if (face.AccentHex is not null)
        {
            patch.AccentColor = face.AccentHex;
            patch.AccentSyncWithDesktop = false;
        }
        return _panels.Patch(q60Record.Id, patch) is not null ? Applied(id) : Fail(id, "patch-failed");
    }

    private async Task<Nexus2ApplyResultDto> ApplyWallpaperAsync(JsonElement? q60Software, string configDir, PanelDeviceRecord? q60Record)
    {
        const string id = "wallpapers";
        if (q60Software is not { } qs)
        {
            return Fail(id, "no-q60-data");
        }
        if (q60Record is null)
        {
            return Skip(id, "no-q60-record");
        }
        var wallpaper = Nexus2Q60Translator.TranslateWallpaper(qs, configDir, File.Exists);
        var uploads = Q60Uploads(configDir);
        if (!wallpaper.Available && uploads.Count == 0)
        {
            return Skip(id, "no-wallpaper");
        }
        if (FfmpegResolver.Path is null)
        {
            return Fail(id, "ffmpeg-missing");
        }

        // Nexus 2's Q-Series picker and playlist both list every upload, not only the shown one.
        string? firstError = null;
        var uploaded = new List<PanelBgItem>();
        foreach (var path in uploads)
        {
            var (item, error) = await ImportQ60WallpaperAsync(path, q60Record.Id);
            if (item is not null)
            {
                uploaded.Add(item);
            }
            firstError ??= error;
        }
        // The playlist cycles the folder oldest first, the reverse of the picker's order.
        uploaded.Reverse();
        // A shown stock preset is not one of the files Nexus 2's playlist cycles.
        var slideshow = wallpaper.Playlist && uploaded.Count > 0;
        PanelBgItem? shown = null;
        if (wallpaper.Available && !slideshow)
        {
            var (item, error) = await ImportQ60WallpaperAsync(wallpaper.AbsolutePath!, q60Record.Id);
            shown = item;
            firstError ??= error;
        }

        var active = slideshow ? uploaded[0] : shown;
        if (active is null && uploaded.Count == 0)
        {
            return Fail(id, firstError ?? "commit-failed");
        }
        if (active is not null)
        {
            _panels.Patch(q60Record.Id, new PanelDevicePatch
            {
                BackgroundMode = "media",
                BackgroundMediaId = active.Id,
                BackgroundMediaType = active.Type,
                BackgroundMediaAlpha = active.Alpha,
                BackgroundMediaSlideshow = slideshow,
                BackgroundMediaInterval = slideshow ? wallpaper.PlaylistIntervalSec : null,
                // Plays in Nexus 2's folder order over any shuffle or saved order on the panel.
                BackgroundMediaShuffle = slideshow ? false : null,
                BackgroundMediaOrder = slideshow ? uploaded.Select(i => i.Id).ToList() : null,
                // Nexus 2 switches slides on the interval, cutting a longer video.
                BackgroundMediaFinishVideos = slideshow ? false : null,
            });
        }
        var detail = firstError is null ? null : "background-not-imported";
        return new Nexus2ApplyResultDto { Id = id, Status = "applied", Detail = detail };
    }

    /// <summary>Stages and commits one Q-Series wallpaper at the panel's size, reusing one an earlier import committed.</summary>
    private async Task<(PanelBgItem? Item, string? Error)> ImportQ60WallpaperAsync(string sourcePath, string deviceId)
    {
        var name = Path.GetFileName(sourcePath);
        if (_bgLibrary.ListItems(deviceId).Find(i => i.Name == name || IsStagedNameOf(i.Name, name)) is { } existing)
        {
            return (existing, null);
        }
        var tempPath = Path.Combine(Path.GetTempPath(), $"nexus2-wallpaper-{Guid.NewGuid():N}{Path.GetExtension(name)}");
        try
        {
            // StageAsync moves whatever path it is given into its staging area.
            Nexus2ReadOnlyIo.CopyTo(sourcePath, tempPath);
            var stage = await PanelBgImporter.StageAsync(_bgLibrary, deviceId, tempPath, name);
            if (!stage.Ok || stage.StageId is null)
            {
                return (null, "stage-failed");
            }

            var commit = await PanelBgImporter.CommitAsync(_bgLibrary, deviceId, stage.StageId, new CropRect(0, 0, 1, 1), 720, 1280);
            if (!commit.Ok || commit.Item is null)
            {
                return (null, "commit-failed");
            }
            // A commit names the item after its staged file; the source name is what a re-import looks up.
            commit.Item.Name = name;
            _bgLibrary.SaveMeta(deviceId, commit.Item);
            return (commit.Item, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "copy-failed");
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>An unrenamed commit's name, SanitizeId("&lt;ms hex&gt;-&lt;base&gt;") plus the lowercased extension, which earlier imports left on the Q-Series.</summary>
    private static bool IsStagedNameOf(string itemName, string sourceName)
    {
        var ext = Path.GetExtension(sourceName).ToLowerInvariant();
        var dash = itemName.IndexOf('-');
        if (dash <= 0 || !itemName.EndsWith(ext, StringComparison.Ordinal) || !itemName[..dash].All(Uri.IsHexDigit))
        {
            return false;
        }
        return itemName[..^ext.Length] == MediaImporter.SanitizeId(itemName[..(dash + 1)] + Path.GetFileNameWithoutExtension(sourceName));
    }

    /// <summary>Every file the Q-Series picker lists from q60\web\user-media, name-descending: newest first for Nexus 2's Date.now()-stamped upload names.</summary>
    private static List<string> Q60Uploads(string configDir)
    {
        var dir = Path.Combine(configDir, Path.Combine(Nexus2Q60Translator.Q60UserMediaRelative));
        try
        {
            if (!Directory.Exists(dir))
            {
                return new List<string>();
            }
            var files = Directory.EnumerateFiles(dir).Where(Nexus2Y70Translator.IsShowableFile).ToList();
            files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(Path.GetFileName(b), Path.GetFileName(a)));
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[nexus2] Q-Series uploads unreadable: {ex.Message}");
            return new List<string>();
        }
    }

    private Nexus2ApplyResultDto ApplyGallerySources(JsonElement? y70)
    {
        const string id = "gallerySources";
        if (y70 is not { } y)
        {
            return Fail(id, "no-y70-data");
        }
        var sources = Nexus2Y70Translator.TranslateGallerySources(y, File.Exists);
        if (!sources.Available)
        {
            return Skip(id, "no-gallery-widget");
        }
        if (sources.ExistingPaths.Count == 0)
        {
            return Skip(id, "no-files-found");
        }

        var added = 0;
        foreach (var path in sources.ExistingPaths)
        {
            var result = _gallery.AddReference(path, GallerySourceKinds.File);
            if (result.Source is not null)
            {
                added++;
            }
        }
        return added > 0 ? Applied(id) : Skip(id, "already-added");
    }

    private Nexus2ApplyResultDto ApplyRotation(JsonElement root)
    {
        const string id = "rotation";
        var value = Nexus2Translator.TranslateRotation(root);
        if (value is null)
        {
            return Skip(id, "no-rotation-data");
        }
        _store.Update(s => s.QSeries.Orientation = value);
        _services.GetService<QSeries.QSeriesPortWatcher>()?.AnnounceDisplayChange();
        return Applied(id);
    }

    private Nexus2ApplyResultDto ApplyLanguage(JsonElement root)
    {
        const string id = "language";
        var value = Nexus2Translator.TranslateLanguage(root);
        if (value is null)
        {
            return Skip(id, "no-supported-language");
        }
        _store.Update(s => s.Theme.Language = value);
        return Applied(id);
    }

    private PanelDeviceRecord? FindSingleInstanceRecord(string surface)
    {
        PanelDeviceRecord? best = null;
        foreach (var record in _panels.List())
        {
            if (!string.IsNullOrEmpty(record.DisplayId) || record.Capabilities?.Surface != surface)
            {
                continue;
            }
            if (best is null || record.LastSeenAt > best.LastSeenAt)
            {
                best = record;
            }
        }
        return best;
    }

    private static JsonElement? GetY70(JsonElement profile)
    {
        var widgets = Nexus2Json.GetObject(profile, "widgets");
        if (widgets is not { } w)
        {
            return null;
        }
        var faces = Nexus2Json.GetObject(w, "faces");
        return faces is { } f ? Nexus2Json.GetObject(f, "y70") : null;
    }

    private static JsonElement? GetQ60Software(JsonElement profile)
    {
        var q60 = Nexus2Json.GetObject(profile, "q60");
        return q60 is { } q ? Nexus2Json.GetObject(q, "software") : null;
    }

    private static Nexus2ApplyResultDto Applied(string id) => new() { Id = id, Status = "applied" };
    private static Nexus2ApplyResultDto Skip(string id, string detail) => new() { Id = id, Status = "skipped", Detail = detail };
    private static Nexus2ApplyResultDto Fail(string id, string detail) => new() { Id = id, Status = "failed", Detail = detail };
}
