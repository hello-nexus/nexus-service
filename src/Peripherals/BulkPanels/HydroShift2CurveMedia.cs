using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>One video in the HydroShift II OLED Curved library; not ready until encoded for the head's current mount.</summary>
public sealed record HydroShift2CurveMediaItem(string Name, string Label, double? DurationSec, bool Ready);

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

    /// <summary>Longest clip kept; the glass loops whatever it gets.</summary>
    private const int MaxSeconds = 300;
    private const int EncodeTimeoutSeconds = 30 * 60;
    private const int ThumbTimeoutSeconds = 60;
    private const int FrameRate = 30;

    private readonly string _root;
    private readonly SemaphoreSlim _encode = new(1, 1);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    public HydroShift2CurveMedia(string? root = null)
    {
        _root = root ?? Path.Combine(MediaLibrary.DeviceStoreDir(HydroShift2CurveLcdDriver.Id), "media");
    }

    public static bool IsSafeName(string? name) => !string.IsNullOrEmpty(name) && SafeName().IsMatch(name);

    public IReadOnlyList<HydroShift2CurveMediaItem> List(bool flip180, bool mirror)
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
            items.Add(new HydroShift2CurveMediaItem(name, label, duration, File.Exists(VariantPath(name, flip180, mirror))));
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
        var id = MediaImporter.SanitizeId(label);
        var name = $"{(id.Length > 24 ? id[..24] : id)}-{Guid.NewGuid():N}";
        var dir = ItemDir(name);
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, SourcePrefix + Path.GetExtension(stagedPath).ToLowerInvariant());
        File.Move(stagedPath, source);
        try
        {
            var stderr = await MediaImporter.RunFfmpeg(ThumbTimeoutSeconds, ct, new[]
            {
                "-y", "-i", source,
                "-vf", $"{crop.ToFfmpegCrop()},scale=320:151",
                "-frames:v", "1", "-q:v", "5",
                Path.Combine(dir, ThumbFile),
            });
            WriteMeta(name, label, crop, ParseDuration(stderr));
            return name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] could not read upload '{label}': {ex.Message}");
            Delete(name);
            return null;
        }
    }

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
            if (_failed.Contains(target))
            {
                return null;
            }
            var (_, crop, _) = ReadMeta(name);
            var temp = target + ".tmp";
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
            return target;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] encoding '{name}' failed: {ex.Message}");
            _failed.Add(target);
            return null;
        }
        finally
        {
            _encode.Release();
        }
    }

    public bool Delete(string name)
    {
        if (!IsSafeName(name) || !Directory.Exists(ItemDir(name)))
        {
            return false;
        }
        try
        {
            Directory.Delete(ItemDir(name), recursive: true);
            return true;
        }
        catch (IOException ex)
        {
            ServiceLog.Warn($"[{HydroShift2CurveLcdDriver.Id}] could not delete '{name}': {ex.Message}");
            return false;
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
