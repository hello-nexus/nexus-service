using System;
using System.Linq;
using Nexus.Service.Platform.Linux;
using Xunit;

namespace Nexus.Service.Tests.Platform;

/// <summary>
/// Chromium probe order. The curated absolute paths have to keep their
/// positions - they are the mainstream four, in preference order - while the
/// name-times-directory sweep that follows is what finds a fork the old
/// hardcoded list could not see at all.
/// </summary>
public class LinuxBrowsersTests
{
    private static readonly string[] Curated = { "/usr/bin/chromium", "/snap/bin/brave" };
    private static readonly string[] Dirs = { "/usr/bin", "/usr/local/bin" };
    private static readonly string[] Names = { "chromium", "helium" };

    [Fact]
    public void Candidates_KeepsCuratedPathsFirstAndInOrder()
    {
        var candidates = LinuxBrowsers.Candidates(Curated, Dirs, Names);
        Assert.Equal("/usr/bin/chromium", candidates[0]);
        Assert.Equal("/snap/bin/brave", candidates[1]);
    }

    [Fact]
    public void Candidates_SweepsEveryNameInEveryDirectory()
    {
        var candidates = LinuxBrowsers.Candidates(Curated, Dirs, Names);
        Assert.Contains("/usr/bin/helium", candidates);
        Assert.Contains("/usr/local/bin/chromium", candidates);
        Assert.Contains("/usr/local/bin/helium", candidates);
    }

    // A curated path that the sweep would regenerate must not be probed twice,
    // and must not lose its curated position to the duplicate.
    [Fact]
    public void Candidates_DeduplicatesWithoutReordering()
    {
        var candidates = LinuxBrowsers.Candidates(Curated, Dirs, Names);
        Assert.Equal(candidates.Length, candidates.Distinct(StringComparer.Ordinal).Count());
        Assert.Single(candidates, c => c == "/usr/bin/chromium");
    }

    [Fact]
    public void SearchDirs_CoversTheUserAndPackageManagerPrefixes()
    {
        var dirs = LinuxBrowsers.SearchDirs();
        Assert.Contains("/usr/bin", dirs);
        Assert.Contains("/usr/local/bin", dirs);
        Assert.Contains("/var/lib/flatpak/exports/bin", dirs);
        Assert.Contains("/snap/bin", dirs);
    }

    // The real list has to still name the four the README promises.
    [Fact]
    public void ChromiumFamily_ProbesTheDocumentedBrowsers()
    {
        var family = LinuxBrowsers.ChromiumFamily();
        Assert.Contains("/usr/bin/chromium", family);
        Assert.Contains("/usr/bin/google-chrome", family);
        Assert.Contains("/usr/bin/brave-browser", family);
        Assert.Contains("/var/lib/flatpak/exports/bin/com.microsoft.Edge", family);
    }
}
