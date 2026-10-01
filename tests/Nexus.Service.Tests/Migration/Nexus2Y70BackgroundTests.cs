using System;
using System.IO;
using System.Text.Json;
using Nexus.Service.Migration;
using Nexus.Service.Panel;
using Xunit;

namespace Nexus.Service.Tests.Migration;

/// <summary>Which Y70 background Nexus 2 is showing, the app.asar read that recovers its bundled
/// particles video, and the ffmpeg banner parse that tells the portrait copy from the landscape one.</summary>
public sealed class Nexus2Y70BackgroundTests : IDisposable
{
    private const string CustomVideo = @"C:\Users\u\AppData\Roaming\HYTE Nexus\user-media\media\custom-Y70-bg\custom-Y70-bg-1.mp4";
    private const string CustomImage = @"C:\Users\u\AppData\Roaming\HYTE Nexus\user-media\images\custom-Y70-bg\custom-Y70-bg-2.png";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus2-bg-test-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static Nexus2AppearanceResult Translate(string bg, bool bgDisabled = false, Func<string, bool>? exists = null, bool transparent = false)
    {
        using var doc = JsonDocument.Parse($$"""
        {
          "bgName": "particles",
          "bg": {{JsonSerializer.Serialize(bg)}},
          "bgDisabled": {{(bgDisabled ? "true" : "false")}},
          "theme": {
            "opacity": 0.7,
            "transparent": {{(transparent ? "true" : "false")}},
            "primary": { "main": "211, 144, 30" },
            "accent": { "main": "46, 197, 146" }
          }
        }
        """);
        return Nexus2Y70Translator.TranslateAppearance(doc.RootElement, exists ?? (_ => true));
    }

    [Fact]
    public void Appearance_AccentComesFromThePrimaryColor()
    {
        Assert.Equal("#d3901e", Translate("").AccentHex);
    }

    [Fact]
    public void Appearance_CarriesTheWidgetOpacity()
    {
        Assert.Equal(0.7, Translate("").WidgetOpacity);
    }

    [Fact]
    public void Appearance_ReadsTheTransparentBackgroundToggle()
    {
        Assert.True(Translate("", transparent: true).Transparent);
        Assert.False(Translate("").Transparent);
    }

    [Fact]
    public void Appearance_EmptyBgIsTheBundledParticles()
    {
        var result = Translate("");
        Assert.Equal(Nexus2Y70BackgroundSource.BundledParticles, result.Background);
        Assert.Null(result.BackgroundPath);
    }

    [Fact]
    public void Appearance_ACustomVideoOnDiskIsCopiedFromItsPath()
    {
        var result = Translate(CustomVideo);
        Assert.Equal(Nexus2Y70BackgroundSource.File, result.Background);
        Assert.Equal(CustomVideo, result.BackgroundPath);
        Assert.False(result.BackgroundStill);
    }

    [Fact]
    public void Appearance_AMissingFileFallsBackToParticlesLikeNexus2Does()
    {
        var result = Translate(CustomVideo, exists: _ => false);
        Assert.Equal(Nexus2Y70BackgroundSource.BundledParticles, result.Background);
    }

    [Fact]
    public void Appearance_AVideoChromiumCannotPlayFallsBackToParticlesLikeNexus2Does()
    {
        var result = Translate(CustomVideo.Replace(".mp4", ".mkv"));
        Assert.Equal(Nexus2Y70BackgroundSource.BundledParticles, result.Background);
    }

    [Fact]
    public void Appearance_AQueryStringIsStrippedBeforeTheFileLookup()
    {
        var result = Translate(CustomVideo + "?v=2", exists: p => p == CustomVideo);
        Assert.Equal(Nexus2Y70BackgroundSource.File, result.Background);
        Assert.Equal(CustomVideo, result.BackgroundPath);
    }

    [Fact]
    public void Appearance_AGradientIsParsedForDrawing()
    {
        var result = Translate("linear-gradient(180deg, #0b68e2 0%, #b707a3 60%, #ad074a 100%)");
        Assert.Equal(Nexus2Y70BackgroundSource.Gradient, result.Background);
        Assert.Equal(3, result.BackgroundGradient!.Stops.Count);
    }

    [Fact]
    public void Appearance_AnyOtherCssBackgroundHasNothingToBringOver()
    {
        Assert.Equal(Nexus2Y70BackgroundSource.None, Translate("radial-gradient(#000, #fff)").Background);
    }

    // Nexus 2's six gradient presets (getY70GradientBackgrounds), verbatim.
    [Theory]
    [InlineData("linear-gradient(180deg, #0b68e2 0%, #b707a3 60%, #ad074a 100%)", 180, 3, 0x0b, 0x68, 0xe2, 0.6)]
    [InlineData("linear-gradient(180deg, #B20088 0%, #4807af 60%, #923EAF 100%)", 180, 3, 0xb2, 0x00, 0x88, 0.6)]
    [InlineData("linear-gradient(180deg, #6dbdbe 0%, #cbc08b 60%, #d58aab 100%)", 180, 3, 0x6d, 0xbd, 0xbe, 0.6)]
    [InlineData("linear-gradient(181deg, rgba(255, 255, 0, 1) 0%, rgba(0, 188, 212, 1) 50%, rgba(238, 130, 238, 1) 100%)", 181, 3, 255, 255, 0, 0.5)]
    [InlineData("linear-gradient(150deg, rgba(255, 255, 0, 1) 0%, rgba(238, 130, 238, 1) 100%)", 150, 2, 255, 255, 0, 1.0)]
    [InlineData("linear-gradient(333deg, rgba(238, 130, 238, 1) 0%, rgba(0, 209, 255, 1) 100%)", 333, 2, 238, 130, 238, 1.0)]
    public void Gradient_ParsesEveryNexus2Preset(string css, double angle, int stops, int r, int g, int b, double secondAt)
    {
        var gradient = Nexus2Gradient.Parse(css)!;
        Assert.Equal(angle, gradient.AngleDeg);
        Assert.Equal(stops, gradient.Stops.Count);
        Assert.Equal(((byte)r, (byte)g, (byte)b), (gradient.Stops[0].R, gradient.Stops[0].G, gradient.Stops[0].B));
        Assert.Equal(secondAt, gradient.Stops[1].At, 3);
    }

    [Fact]
    public void Gradient_RaisesAnOutOfOrderStopToTheEarlierOne()
    {
        var gradient = Nexus2Gradient.Parse("linear-gradient(180deg, #ff0000 60%, #00ff00 20%, #0000ff 100%)")!;
        Assert.Equal(0.6, gradient.Stops[1].At, 3);
    }

    [Fact]
    public void Gradient_DrawsAlongTheCssGradientLine()
    {
        var down = Nexus2Gradient.Parse("linear-gradient(180deg, #ff0000 0%, #0000ff 100%)")!;
        Assert.Equal(((byte)255, (byte)0, (byte)0), down.ColorAt(5, 0, 10, 100));
        Assert.Equal(((byte)0, (byte)0, (byte)255), down.ColorAt(5, 100, 10, 100));
        Assert.Equal(((byte)128, (byte)0, (byte)128), down.ColorAt(5, 50, 10, 100));

        var right = Nexus2Gradient.Parse("linear-gradient(90deg, #000000, #ffffff)")!;
        Assert.Equal(((byte)0, (byte)0, (byte)0), right.ColorAt(0, 50, 10, 100));
        Assert.Equal(((byte)255, (byte)255, (byte)255), right.ColorAt(10, 50, 10, 100));
    }

    [Fact]
    public void Gradient_WritesABottomUpBgrBmp()
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "g.bmp");
        Nexus2Gradient.Parse("linear-gradient(180deg, #ff0000 0%, #0000ff 100%)")!.WriteBmp(path, 2, 4);

        var bytes = File.ReadAllBytes(path);
        Assert.Equal((byte)'B', bytes[0]);
        Assert.Equal(54 + 8 * 4, bytes.Length);
        // First stored row is the bottom one (blue), BGR order.
        Assert.True(bytes[54] > 200 && bytes[56] < 60);
        // Last stored row is the top one (red).
        Assert.True(bytes[54 + 8 * 3 + 2] > 200 && bytes[54 + 8 * 3] < 60);
    }

    [Fact]
    public void Appearance_DisabledFreezesAVideoButNotAnImage()
    {
        Assert.True(Translate(CustomVideo, bgDisabled: true).BackgroundStill);
        Assert.True(Translate("", bgDisabled: true).BackgroundStill);
        Assert.False(Translate(CustomImage, bgDisabled: true).BackgroundStill);
    }

    [Fact]
    public void Asar_FindsAndExtractsPackedEntriesByteForByte()
    {
        var asar = Path.Combine(_tempDir, "app.asar");
        var a = new byte[] { 1, 2, 3 };
        var b = new byte[] { 9, 8, 7, 6, 5 };
        AsarTestWriter.Write(asar, "assets", new[] { ("particles-A.mp4", a), ("other.js", new byte[] { 0 }), ("particles-B.mp4", b) });

        var entries = Nexus2AsarReader.Find(asar, n => n.StartsWith("particles-", StringComparison.Ordinal));
        Assert.Equal(new[] { "assets/particles-A.mp4", "assets/particles-B.mp4" }, entries.ConvertAll(e => e.Path));

        var dest = Path.Combine(_tempDir, "b.mp4");
        Nexus2AsarReader.Extract(asar, entries[1], dest);
        Assert.Equal(b, File.ReadAllBytes(dest));
    }

    [Fact]
    public void Asar_MalformedEntriesAreSkippedNotThrown()
    {
        Directory.CreateDirectory(_tempDir);
        var asar = Path.Combine(_tempDir, "odd.asar");
        var json = System.Text.Encoding.UTF8.GetBytes(
            "{\"files\":{\"a\":1,\"particles-num.mp4\":{\"size\":3,\"offset\":0},\"particles-ok.mp4\":{\"size\":3,\"offset\":\"0\"}}}");
        var padding = (4 - json.Length % 4) % 4;
        using (var writer = new BinaryWriter(File.Create(asar)))
        {
            writer.Write(4u);
            writer.Write((uint)(8 + json.Length + padding));
            writer.Write((uint)(4 + json.Length + padding));
            writer.Write((uint)json.Length);
            writer.Write(json);
            writer.Write(new byte[padding + 3]);
        }

        var entry = Assert.Single(Nexus2AsarReader.Find(asar, n => n.StartsWith("particles-", StringComparison.Ordinal)));
        Assert.Equal("particles-ok.mp4", entry.Path);
    }

    [Fact]
    public void Asar_AnUnreadableArchiveFindsNothing()
    {
        Directory.CreateDirectory(_tempDir);
        var asar = Path.Combine(_tempDir, "broken.asar");
        File.WriteAllBytes(asar, new byte[] { 4, 0, 0, 0 });
        Assert.Empty(Nexus2AsarReader.Find(asar, _ => true));
    }

    [Fact]
    public void Probe_ReadsDurationAndSizeFromTheBanner()
    {
        const string banner = """
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'particles.mp4':
              Duration: 00:00:19.97, start: 0.000000, bitrate: 775 kb/s
              Stream #0:0[0x1](und): Video: hevc (Main) (hvc1 / 0x31637668), yuv420p(tv, progressive), 682x2560 [SAR 1:1 DAR 341:1280], 773 kb/s, 30 fps
            """;
        var probe = PanelBgImporter.ParseProbe(banner);
        Assert.Equal(19.97, probe.DurationSec, 2);
        Assert.Equal(682, probe.Width);
        Assert.Equal(2560, probe.Height);
    }

    [Fact]
    public void AsarCandidates_TryTheUninstallersFolderThenThePerUserDefault()
    {
        var configDir = Path.Combine("C:", "Users", "u", "AppData", "Roaming", "HYTE Nexus");
        var custom = Path.Combine("D:", "Apps", "HYTE Nexus");
        var candidates = new System.Collections.Generic.List<string>(Nexus2MigrationService.AsarCandidates(configDir, custom));
        Assert.Equal(Path.Combine(custom, "resources", "app.asar"), candidates[0]);
        Assert.Equal(Path.Combine("C:", "Users", "u", "AppData", "Local", "Programs", "HYTE Nexus", "resources", "app.asar"), candidates[1]);
    }

    [Fact]
    public void InstallRoot_IsTheUninstallersFolderWhateverTheLocation()
    {
        var exe = Path.GetFullPath(Path.Combine("D:", "Apps", "HYTE Nexus", "Uninstall HYTE Nexus.exe"));
        Assert.Equal(Path.GetDirectoryName(exe), Nexus2UninstallRules.InstallRoot(null, $"\"{exe}\" /currentuser"));
        Assert.Null(Nexus2UninstallRules.InstallRoot(null, "  "));
    }
}
