using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>Kanali's panel encode (MediaX.dll MX_ConvertToH264Raw), read from its x264 SEI and
/// disassembly: libx264 fast, Main@4.1, no B-frames, GOP = fps, ABR, SPS/PPS + AUD in-band,
/// BT.709 full-range VUI.</summary>
public static partial class TryxKanaliEncode
{
    public const string X264Params =
        "repeat-headers=1:annexb=1:aud=1:b-pyramid=0:weightp=0:ref=3:range=pc:colorprim=bt709:transfer=bt709:colormatrix=bt709";

    // MediaX encodes GIF and still-image sources at a fixed quality instead of a bitrate.
    private const int ImageSourceCrf = 18;

    /// <summary>ffmpeg output options for <paramref name="src"/> encoded at <paramref name="outWidth"/> x
    /// <paramref name="outHeight"/>, <paramref name="fps"/>.</summary>
    public static string X264Options(SourceInfo src, int outWidth, int outHeight, int fps)
    {
        var rate = src.IsImage
            ? $"-crf {ImageSourceCrf}"
            : $"-b:v {BitrateKbps(src, outWidth, outHeight, fps)}k";
        return $"-preset fast -profile:v main -level 4.1 -bf 0 -g {fps} {rate} -x264-params {X264Params}";
    }

    /// <summary>The source's video stream as MediaX probes it; zero for anything unknown. IsImage marks
    /// a GIF, PNG or JPEG source, by codec or by file extension as MediaX checks it.</summary>
    public readonly record struct SourceInfo(int Width, int Height, double Fps, int Kbps, bool IsImage = false);

    // MediaX.dll's bitrate constants.
    private const int UnknownSourceKbps = 4000;
    private const double MinFpsFactor = 0.75, MaxFpsFactor = 2.0, MinPixelFactor = 0.35, Headroom = 1.15;
    private const int MinKbps = 500, MaxKbps = 12000;
    private const int ProbeTimeoutMs = 10_000;

    /// <summary>MediaX's target bitrate (kb/s): the source bitrate scaled by the clamped frame-rate
    /// ratio and, when downscaling, the clamped pixel ratio, plus headroom, within MediaX's bounds.</summary>
    public static int BitrateKbps(SourceInfo src, int outWidth, int outHeight, int outFps)
    {
        if (src.Kbps <= 0) return UnknownSourceKbps;
        var fpsFactor = 1.0;
        if (src.Fps > 0 && outFps > 0) fpsFactor = Math.Clamp(outFps / src.Fps, MinFpsFactor, MaxFpsFactor);
        var pixFactor = 1.0;
        if (src.Width > 0 && src.Height > 0 && outWidth > 0 && outHeight > 0)
        {
            var ratio = (double)outWidth * outHeight / ((double)src.Width * src.Height);
            if (ratio < 1.0) pixFactor = Math.Max(ratio, MinPixelFactor);
        }
        var kbps = (int)(src.Kbps * fpsFactor * pixFactor * Headroom + 0.5);
        return kbps < MinKbps ? MinKbps : Math.Min(kbps, MaxKbps);
    }

    /// <summary>Reads the first video stream's size, fps and bitrate from `ffmpeg -i` stderr. The
    /// bitrate falls back to the container's, as MediaX does; ffmpeg prints whole kb/s.</summary>
    public static SourceInfo ParseSourceInfo(string ffmpegStderr)
    {
        int width = 0, height = 0, kbps = 0;
        double fps = 0;
        var isImage = false;
        var video = VideoStreamRegex().Match(ffmpegStderr);
        if (video.Success)
        {
            var rest = video.Groups["rest"].Value;
            isImage = ImageCodecRegex().IsMatch(rest);
            var size = SizeRegex().Match(rest);
            if (size.Success)
            {
                width = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
                height = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            var rate = StreamKbpsRegex().Match(rest);
            if (rate.Success) kbps = int.Parse(rate.Groups[1].Value, CultureInfo.InvariantCulture);
            var f = FpsRegex().Match(rest);
            if (f.Success) fps = double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        if (kbps <= 0)
        {
            var container = ContainerKbpsRegex().Match(ffmpegStderr);
            if (container.Success) kbps = int.Parse(container.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return new SourceInfo(width, height, fps, kbps, isImage);
    }

    /// <summary>Runs `ffmpeg -i` on <paramref name="path"/> and parses its stream info.</summary>
    public static SourceInfo ProbeSource(string ffmpegPath, string path)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-hide_banner -i \"{path}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return default;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(ProbeTimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return default;
            }
            outTask.GetAwaiter().GetResult();
            var info = ParseSourceInfo(errTask.GetAwaiter().GetResult());
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".gif" or ".png" or ".jpg" or ".jpeg" ? info with { IsImage = true } : info;
        }
        catch { return default; }
    }

    [GeneratedRegex(@"Stream #\d+:\d+[^\n]*?: Video: (?<rest>[^\n]*)")]
    private static partial Regex VideoStreamRegex();

    [GeneratedRegex(@"^(gif|png|mjpeg)\b")]
    private static partial Regex ImageCodecRegex();

    [GeneratedRegex(@"\b(\d{2,5})x(\d{2,5})\b")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"(\d+) kb/s")]
    private static partial Regex StreamKbpsRegex();

    [GeneratedRegex(@"(\d+(?:\.\d+)?) fps")]
    private static partial Regex FpsRegex();

    [GeneratedRegex(@"Duration: [^\n]*bitrate: (\d+) kb/s")]
    private static partial Regex ContainerKbpsRegex();
}
