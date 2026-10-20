using System;
using System.IO;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Panel;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Fit entire image pads inside a packed RGB format. The bundled ffmpeg rounds
/// the pad canvas down to the chroma multiple on a yuvj420p/yuvj422p frame, so
/// an odd target left the canvas smaller than the scaled frame (error -22).
/// </summary>
public sealed class PanelBgFitWholeTests : IDisposable
{
    // Chroma-subsampled jpeg: the pixel format whose pad canvas ffmpeg rounds down.
    private const string JpegBase64 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAA0JCgsKCA0LCgsODg0PEyAVExISEyccHhcgLikxMC4pLSwzOko+MzZGNywtQFdBRkxOUlNSMj5aYVpQYEpRUk//2wBDAQ4ODhMREyYVFSZPNS01T09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT09PT0//wAARCABgAEADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDGooorgPrgooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKAM2iiivvT4oKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigAooooAKKKKACiiigD/2Q==";

    // Left half opaque red, right half transparent with white RGB hiding under
    // the alpha, so a matte that drops alpha instead of blending shows white.
    private const string TransparentWhitePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAYAAAAf8/9hAAAAJ0lEQVR4nGP8z8DwnwEf+P+fEZ80EwOFgGnUAIbRMGAYDQOGwRAGAC/2BB36FS13AAAAAElFTkSuQmCC";

    private const string DeviceId = "test-device";
    private static readonly CropRect FullFrame = new(0, 0, 1, 1);

    private readonly string _root;
    private readonly PanelBgLibrary _library;

    public PanelBgFitWholeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "nexus-bg-fit-" + Guid.NewGuid().ToString("N"));
        _library = new PanelBgLibrary(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }

    [Theory]
    [InlineData(false, "format=rgb24,pad=393:851:-1:-1:color=black")]
    [InlineData(true, "format=rgba,pad=393:851:-1:-1:color=black@0")]
    public void FitWholePadsInPackedRgb(bool alpha, string expectedTail)
    {
        var filter = PanelBgImporter.ScaleFilter(FullFrame, 393, 851, fitWhole: true, alpha);

        Assert.Contains("ih)))'," + expectedTail, filter, StringComparison.Ordinal);
    }

    [Fact]
    public void FillModeDoesNotPad()
    {
        var filter = PanelBgImporter.ScaleFilter(FullFrame, 393, 851, fitWhole: false, alpha: false);

        Assert.DoesNotContain("pad=", filter, StringComparison.Ordinal);
    }

    [FfmpegFact]
    public async Task FitWholeOddTargetKeepsExactSize()
    {
        foreach (var (w, h) in new[] { (393, 851), (393, 852), (119, 257), (1179, 2556), (1, 1), (1, 7), (7, 1) })
        {
            var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jpg");
            File.WriteAllBytes(temp, Convert.FromBase64String(JpegBase64));
            var staged = await PanelBgImporter.StageAsync(_library, DeviceId, temp, "fit.jpg");
            Assert.True(staged.Ok, staged.Error);

            var result = await PanelBgImporter.CommitAsync(
                _library, DeviceId, staged.StageId!, FullFrame, w, h, fitWhole: true);

            Assert.True(result.Ok, $"{w}x{h}: {result.Error}");

            // ProbeAsync needs two-digit sides, so measure the decoded raster.
            var raw = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".raw");
            try
            {
                await MediaImporter.RunFfmpeg("-y", "-i", _library.GetMediaPath(DeviceId, result.Item!.Id, ".jpg"),
                    "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", raw);
                Assert.Equal(w * h * 3L, new FileInfo(raw).Length);
            }
            finally
            {
                try { File.Delete(raw); }
                catch { }
            }
        }
    }

    [FfmpegFact]
    public async Task MatteFitWholeFlattensTransparencyAndBarsToBlack()
    {
        const int w = 393, h = 851;
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(temp, Convert.FromBase64String(TransparentWhitePngBase64));
        var staged = await PanelBgImporter.StageAsync(_library, DeviceId, temp, "matte.png");
        Assert.True(staged.Ok, staged.Error);

        var result = await PanelBgImporter.CommitAsync(
            _library, DeviceId, staged.StageId!, FullFrame, w, h, keepTransparency: false, fitWhole: true);

        Assert.True(result.Ok, result.Error);
        var raw = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".raw");
        try
        {
            await MediaImporter.RunFfmpeg("-y", "-i", _library.GetMediaPath(DeviceId, result.Item!.Id, ".jpg"),
                "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", raw);
            var bytes = File.ReadAllBytes(raw);
            Assert.Equal(w * h * 3, bytes.Length);

            // Square source: bars above and below, the transparent half on the right of the middle row.
            byte[] Pixel(int x, int y) { var o = ((y * w) + x) * 3; return bytes[o..(o + 3)]; }
            foreach (var (x, y) in new[] { (w - 2, h / 2), (w / 2, 2), (w / 2, h - 3) })
            {
                var p = Pixel(x, y);
                Assert.True(p[0] < 24 && p[1] < 24 && p[2] < 24, $"({x},{y}) expected black, got ({p[0]},{p[1]},{p[2]})");
            }
        }
        finally
        {
            try { File.Delete(raw); }
            catch { }
        }
    }
}
