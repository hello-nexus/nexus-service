using System;
using System.IO;
using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class LinuxIconThemeResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-icon-theme-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string ThemeBase => Path.Combine(_root, "hicolor");
    private string PixmapDir => Path.Combine(_root, "pixmaps");

    private static void WritePng(string dir, string fileName, byte[] content)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, fileName), content);
    }

    [Fact]
    public void ResolvePng_PrefersTheFirstSizeInIconSizesOrder()
    {
        // The first listed size in IconSizes wins.
        WritePng(Path.Combine(ThemeBase, "128x128", "apps"), "app.png", new byte[] { 1, 2, 3 });
        WritePng(Path.Combine(ThemeBase, "48x48", "apps"), "app.png", new byte[] { 9, 9, 9 });

        var bytes = LinuxIconThemeResolver.ResolvePng("app", new[] { ThemeBase }, new[] { PixmapDir });

        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    [Fact]
    public void ResolvePng_FallsBackToPixmapsWhenNoThemeSizeMatches()
    {
        Directory.CreateDirectory(PixmapDir);
        File.WriteAllBytes(Path.Combine(PixmapDir, "app.png"), new byte[] { 7 });

        var bytes = LinuxIconThemeResolver.ResolvePng("app", new[] { ThemeBase }, new[] { PixmapDir });

        Assert.Equal(new byte[] { 7 }, bytes);
    }

    [Fact]
    public void ResolvePng_ReturnsEmpty_ForAnSvgOnlyIcon()
    {
        var dir = Path.Combine(ThemeBase, "scalable", "apps");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "app.svg"), "<svg/>");

        var bytes = LinuxIconThemeResolver.ResolvePng("app", new[] { ThemeBase }, new[] { PixmapDir });

        Assert.Empty(bytes);
    }

    [Fact]
    public void ResolvePng_ReturnsEmpty_WhenNothingMatchesAnywhere()
    {
        var bytes = LinuxIconThemeResolver.ResolvePng("missing", new[] { ThemeBase }, new[] { PixmapDir });

        Assert.Empty(bytes);
    }

    [NonWindowsFact]
    public void ResolvePng_AbsolutePngPath_ReadsItDirectly()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "direct.png");
        File.WriteAllBytes(path, new byte[] { 5, 5 });

        var bytes = LinuxIconThemeResolver.ResolvePng(path, new[] { ThemeBase }, new[] { PixmapDir });

        Assert.Equal(new byte[] { 5, 5 }, bytes);
    }

    [Fact]
    public void ResolvePng_AbsoluteSvgPath_ReturnsEmpty_NoRasterizationAttempted()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "direct.svg");
        File.WriteAllText(path, "<svg/>");

        var bytes = LinuxIconThemeResolver.ResolvePng(path, new[] { ThemeBase }, new[] { PixmapDir });

        Assert.Empty(bytes);
    }

    [Fact]
    public void ResolvePng_TriesEveryThemeBaseInOrder()
    {
        var secondBase = Path.Combine(_root, "hicolor2");
        Directory.CreateDirectory(Path.Combine(secondBase, "48x48", "apps"));
        File.WriteAllBytes(Path.Combine(secondBase, "48x48", "apps", "app.png"), new byte[] { 4 });

        var bytes = LinuxIconThemeResolver.ResolvePng("app", new[] { ThemeBase, secondBase }, new[] { PixmapDir });

        Assert.Equal(new byte[] { 4 }, bytes);
    }
}
