using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>One video in the HydroShift II OLED Curved library; not ready while its upload is still being encoded.</summary>
public sealed record HydroShift2CurveMediaItem(string Name, string Label, double? DurationSec, bool Ready);

public enum HydroShift2CurveMediaDelete { Deleted, Missing, Busy }

/// <summary>
/// Videos the HydroShift II OLED Curved plays on its own decoder. Each keeps its upload and
/// crop, and is encoded per mount (the glass's portrait framebuffer turns the opposite way
/// when the head is flipped) the first time that mount needs it.
/// </summary>
public sealed partial class HydroShift2CurveMedia
{
    private const string SourcePrefix = "source";
    private const string MetaFile = "meta.txt";
    private const string ThumbFile = "thumb.jpg";
    private const string PreviewFile = "preview.mp4";
    /// <summary>Preview size: half the glass, landscape, which plays in any browser.</summary>
    private const int PreviewWidth = HydroShift2CurveProtocol.Width / 2;
    private const int PreviewHeight = HydroShift2CurveProtocol.Height / 2;
    private const string TrashPrefix = ".deleted-";

    /// <summary>Longest clip kept; the glass loops whatever it gets.</summary>
    private const int MaxSeconds = 300;
    private const int EncodeTimeoutSeconds = 30 * 60;
    private const int ThumbTimeoutSeconds = 60;
    private const int FrameRate = 30;
    /// <summary>A failed encode is not retried for this long; a broken source would otherwise rerun ffmpeg on every play attempt.</summary>
    private const long FailedRetryMs = 10 * 60_000;

    private static readonly string[] VideoExtensions = { ".mp4", ".webm", ".mov", ".avi", ".mkv", ".wmv", ".m4v", ".mpg", ".mpeg", ".gif" };

    private readonly string _root;
    private readonly SemaphoreSlim _encode = new(1, 1);
    private readonly Dictionary<string, long> _failedAt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _importing = new(StringComparer.Ordinal);
    private volatile string? _encoding;

    public HydroShift2CurveMedia(string? root = null)
    {
        _root = root ?? Path.Combine(MediaLibrary.DeviceStoreDir(HydroShift2CurveLcdDriver.Id), "media");
    }

    public static bool IsSafeName(string? name) => !string.IsNullOrEmpty(name) && SafeName().IsMatch(name);

    /// <summary>The upload's extension when it is a video container ffmpeg should read, else null.</summary>
    public static string? VideoExtension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return Array.IndexOf(VideoExtensions, ext) >= 0 ? ext : null;
    }

    public IReadOnlyList<HydroShift2CurveMediaItem> List()
    {
        var items = new List<HydroShift2CurveMediaItem>();
        if (!Directory.Exists(_root))
        {
            return items;
        }
        foreach (var dir in Directory.GetDirectories(_root))
        {
            var name = Path.GetFileName(dir);
            if (!IsSafeName(name) || SourcePath(name) is null)
            {
                continue;
            }
            var (label, _, duration) = ReadMeta(name);
            items.Add(new HydroShift2CurveMediaItem(name, label, duration, !_importing.ContainsKey(name)));
        }
        items.Sort((a, b) => Directory.GetCreationTimeUtc(ItemDir(b.Name)).CompareTo(Directory.GetCreationTimeUtc(ItemDir(a.Name))));
        return items;
    }

    public bool Exists(string name) => IsSafeName(name) && SourcePath(name) is not null;

    public byte[]? Thumbnail(string name)
    {
        var path = Path.Combine(ItemDir(name), ThumbFile);
        return IsSafeName(name) && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    public double? Duration(string name) => IsSafeName(name) ? ReadMeta(name).DurationSec : null;

    /// <summary>
    /// Takes ownership of an uploaded file: stores it with its crop, reads its duration and
    /// grabs a thumbnail. Returns the new item's name, or null when ffmpeg cannot read it.
    /// </summary>
    public async Task<string?> ImportAsync(string stagedPath, string label, CropRect crop, CancellationToken ct)
    {
        if (VideoExtension(stagedPath) is not { } ext)
        {
            return null;
        }
        var id = MediaImporter.SanitizeId(label);
        var name = $"{(id.Length > 24 ? id[..24] : id)}-{Guid.NewGuid():N}";
        var dir = ItemDir(name);
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, SourcePrefix + ext);
        File.Move(stagedPath, source);
        _importing[name] = 0;
        try
        {
            var stderr = await MediaImporter.RunFfmpeg(ThumbTimeoutSeconds, ct, new[]
            {
                "-y", "-i", source,
                "-vf", $"{crop.ToFfmpegCrop()},scale=320:151",
                "-frames:v", "1", "-q:v", "5",
                Path.Combine(dir, ThumbFile),
            });
            WriteMeta(name, label, crop, ParseDuration(stderr) is { } seconds ? Math.Min(seconds, MaxSeconds) : null);
            return name;
        }
        catch (OperationCanceledException)
        {
            _importing.TryRemove(name, out _);
            Delete(name);
            throw;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] could not read upload '{label}': {ex.Message}");
            _importing.TryRemove(name, out _);
            Delete(name);
            return null;
        }
    }

    /// <summary>Encodes a fresh upload for the head's mount off the request; the item lists as not ready until it is done, and is dropped if it cannot be encoded.</summary>
    public void EncodeImport(string name, bool flip180, bool mirror, CancellationToken ct) =>
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await EnsureVariantAsync(name, flip180, mirror, ct).ConfigureAwait(false) is not null;
                _importing.TryRemove(name, out _);
                if (!ok)
                {
                    Delete(name);
                    return;
                }
                await EnsurePreviewAsync(name, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _importing.TryRemove(name, out _);
                if (ex is not OperationCanceledException)
                {
                    ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] encoding upload '{name}' failed: {ex.Message}");
                }
            }
        }, CancellationToken.None);

    /// <summary>The glass-ready stream for this mount, encoding it first if needed; null when that fails.</summary>
    public async Task<string?> EnsureVariantAsync(string name, bool flip180, bool mirror, CancellationToken ct)
    {
        var target = VariantPath(name, flip180, mirror);
        if (File.Exists(target))
        {
            return target;
        }
        if (SourcePath(name) is not { } source)
        {
            return null;
        }
        await _encode.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(target))
            {
                return target;
            }
            if (_failedAt.TryGetValue(target, out var failedAt) && Environment.TickCount64 - failedAt < FailedRetryMs)
            {
                return null;
            }
            var (_, crop, _) = ReadMeta(name);
            var temp = target + ".tmp";
            _encoding = name;
            ServiceLog.Info($"[{HydroShift2CurveLcdDriver.Id}] encoding '{name}' for the glass (flip={flip180}, mirror={mirror})");
            // Raw Annex-B: the bundled ffmpeg has no h264 muxer, and rawvideo writes the
            // packets exactly as libx264 emits them. No B-frames, as the glass's decoder needs.
            await MediaImporter.RunFfmpeg(EncodeTimeoutSeconds, ct, new[]
            {
                "-y", "-i", source,
                "-vf", $"{crop.ToFfmpegCrop()},{HydroShift2CurveProtocol.MountFilter(flip180, mirror)}",
                "-r", FrameRate.ToString(CultureInfo.InvariantCulture),
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
                "-bf", "0", "-g", FrameRate.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p",
                "-an", "-t", MaxSeconds.ToString(CultureInfo.InvariantCulture),
                "-f", "rawvideo", temp,
            }).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
            _failedAt.Remove(target);
            return target;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(target + ".tmp");
            throw;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] encoding '{name}' failed: {ex.Message}");
            TryDeleteFile(target + ".tmp");
            _failedAt[target] = Environment.TickCount64;
            return null;
        }
        finally
        {
            _encoding = null;
            _encode.Release();
        }
    }

    /// <summary>A browser-playable landscape mp4 of the cropped clip for the dashboard, made on first use; null when that fails.</summary>
    public async Task<string?> EnsurePreviewAsync(string name, CancellationToken ct)
    {
        var target = Path.Combine(ItemDir(name), PreviewFile);
        if (File.Exists(target))
        {
            return target;
        }
        if (SourcePath(name) is not { } source)
        {
            return null;
        }
        await _encode.WaitAsync(ct).ConfigureAwait(false);
        var temp = target + ".tmp.mp4";
        try
        {
            if (File.Exists(target))
            {
                return target;
            }
            var (_, crop, _) = ReadMeta(name);
            _encoding = name;
            await MediaImporter.RunFfmpeg(EncodeTimeoutSeconds, ct, new[]
            {
                "-y", "-i", source,
                "-vf", $"{crop.ToFfmpegCrop()},scale={PreviewWidth}:{PreviewHeight}:flags=lanczos",
                "-r", FrameRate.ToString(CultureInfo.InvariantCulture),
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "26", "-pix_fmt", "yuv420p",
                "-an", "-movflags", "+faststart", "-t", MaxSeconds.ToString(CultureInfo.InvariantCulture),
                temp,
            }).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(temp);
            throw;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] preview for '{name}' failed: {ex.Message}");
            TryDeleteFile(temp);
            return null;
        }
        finally
        {
            _encoding = null;
            _encode.Release();
        }
    }

    /// <summary>Removes a clip; Busy while ffmpeg is still writing it or a file is held open.</summary>
    public HydroShift2CurveMediaDelete Delete(string name)
    {
        if (!IsSafeName(name) || !Directory.Exists(ItemDir(name)))
        {
            return HydroShift2CurveMediaDelete.Missing;
        }
        if (_encoding == name)
        {
            return HydroShift2CurveMediaDelete.Busy;
        }
        // Renamed out of the library first: Windows refuses the rename while any file in it is
        // open, so a clip in use stays whole instead of losing some files to a recursive delete.
        var trash = Path.Combine(_root, TrashPrefix + name);
        try
        {
            Directory.Move(ItemDir(name), trash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] could not delete '{name}': {ex.Message}");
            return HydroShift2CurveMediaDelete.Busy;
        }
        SweepTrash();
        return HydroShift2CurveMediaDelete.Deleted;
    }

    /// <summary>Removes deleted clips whose files could not all go at once; their names never list.</summary>
    private void SweepTrash()
    {
        foreach (var dir in Directory.GetDirectories(_root, TrashPrefix + "*"))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static double? ParseDuration(string stderr)
    {
        var m = DurationLine().Match(stderr);
        if (!m.Success)
        {
            return null;
        }
        return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600)
            + (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60)
            + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    private string ItemDir(string name) => Path.Combine(_root, name);

    private string? SourcePath(string name)
    {
        var dir = ItemDir(name);
        if (!Directory.Exists(dir))
        {
            return null;
        }
        foreach (var file in Directory.GetFiles(dir, SourcePrefix + ".*"))
        {
            return file;
        }
        return null;
    }

    private string VariantPath(string name, bool flip180, bool mirror) =>
        Path.Combine(ItemDir(name), $"glass-{(flip180 ? "flip" : "up")}{(mirror ? "-mirror" : "")}.h264");

    /// <summary>Three lines: label, crop wire form, duration in seconds (blank when unknown).</summary>
    private void WriteMeta(string name, string label, CropRect crop, double? duration)
    {
        var cropText = string.Join(',',
            crop.X.ToString(CultureInfo.InvariantCulture), crop.Y.ToString(CultureInfo.InvariantCulture),
            crop.W.ToString(CultureInfo.InvariantCulture), crop.H.ToString(CultureInfo.InvariantCulture),
            crop.Rotate.ToString(CultureInfo.InvariantCulture), crop.Mirror ? "1" : "0");
        File.WriteAllLines(Path.Combine(ItemDir(name), MetaFile), new[]
        {
            label.ReplaceLineEndings(" "),
            cropText,
            duration?.ToString(CultureInfo.InvariantCulture) ?? "",
        });
    }

    private (string Label, CropRect Crop, double? DurationSec) ReadMeta(string name)
    {
        var path = Path.Combine(ItemDir(name), MetaFile);
        var lines = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
        var label = lines.Length > 0 && lines[0].Length > 0 ? lines[0] : name;
        var crop = lines.Length > 1 && CropRect.TryParse(lines[1], out var c) ? c : new CropRect(0, 0, 1, 1);
        double? duration = lines.Length > 2 && double.TryParse(lines[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        return (label, crop, duration);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"Duration: (\d+):(\d{2}):(\d{2}(?:\.\d+)?)")]
    private static partial Regex DurationLine();
}
