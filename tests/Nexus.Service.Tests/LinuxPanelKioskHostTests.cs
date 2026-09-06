using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Platform.Linux;
using Xunit;

namespace Nexus.Service.Tests;

public class LinuxPanelKioskHostTests
{
    private static Dictionary<string, string> Running(params (string DisplayId, string DeviceId)[] entries)
    {
        var map = new Dictionary<string, string>();
        foreach (var (displayId, deviceId) in entries)
            map[displayId] = deviceId;
        return map;
    }

    [Fact]
    public void Diff_OpensDesiredNotRunning()
    {
        var (toClose, toOpen) = LinuxPanelKioskHost.Diff(
            Running(),
            new List<(string, string)> { ("DELA0B8-1", "dev1") });

        Assert.Empty(toClose);
        Assert.Equal(new[] { ("DELA0B8-1", "dev1") }, toOpen);
    }

    [Fact]
    public void Diff_ClosesRunningNotDesired()
    {
        var (toClose, toOpen) = LinuxPanelKioskHost.Diff(
            Running(("DELA0B8-1", "dev1")),
            new List<(string, string)>());

        Assert.Equal(new[] { "DELA0B8-1" }, toClose);
        Assert.Empty(toOpen);
    }

    [Fact]
    public void Diff_DeviceSwapClosesAndReopens()
    {
        var (toClose, toOpen) = LinuxPanelKioskHost.Diff(
            Running(("DELA0B8-1", "dev1")),
            new List<(string, string)> { ("DELA0B8-1", "dev2") });

        Assert.Equal(new[] { "DELA0B8-1" }, toClose);
        Assert.Equal(new[] { ("DELA0B8-1", "dev2") }, toOpen);
    }

    [Fact]
    public void Diff_UnchangedUntouched()
    {
        var (toClose, toOpen) = LinuxPanelKioskHost.Diff(
            Running(("DELA0B8-1", "dev1"), ("GSM5C1D-2", "dev2")),
            new List<(string, string)> { ("DELA0B8-1", "dev1"), ("GSM5C1D-2", "dev2") });

        Assert.Empty(toClose);
        Assert.Empty(toOpen);
    }

    [Fact]
    public void Diff_MixedAddRemoveKeep()
    {
        var (toClose, toOpen) = LinuxPanelKioskHost.Diff(
            Running(("keep", "dev1"), ("gone", "dev2")),
            new List<(string, string)> { ("keep", "dev1"), ("new", "dev3") });

        Assert.Equal(new[] { "gone" }, toClose);
        Assert.Equal(new[] { ("new", "dev3") }, toOpen);
    }

    // Snap confinement grants only non-hidden $HOME paths and the browser exits
    // on "Failed To Create Data Directory" anywhere else (bench-verified on
    // Ubuntu 24.04). Every segment below the home must therefore stay visible.
    [Fact]
    public void ProfileDir_PutsEverySegmentUnderHomeAndNoneHidden()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(home), "test needs a resolvable home");

        var dir = LinuxPanelKioskHost.ProfileDir("dev1");

        Assert.True(Path.IsPathRooted(dir));
        Assert.StartsWith(home + Path.DirectorySeparatorChar, dir);
        var segments = dir[(home.Length + 1)..].Split(Path.DirectorySeparatorChar);
        Assert.NotEmpty(segments);
        Assert.DoesNotContain(segments, s => s.StartsWith('.'));
    }

    // The root daemon adopts the session user's HOME after start, so a value
    // cached at type-init would send every kiosk profile to /root.
    [Fact]
    public void ProfileDir_ReadsHomeAtCallTime()
    {
        var prev = Environment.GetEnvironmentVariable("HOME");
        Assert.False(string.IsNullOrEmpty(prev), "test needs HOME set");
        // Must exist: GetFolderPath verifies the directory and yields "" otherwise.
        var adopted = Directory.CreateTempSubdirectory("adopted-home").FullName;
        try
        {
            Environment.SetEnvironmentVariable("HOME", adopted);
            Assert.StartsWith(adopted + Path.DirectorySeparatorChar, LinuxPanelKioskHost.ProfileDir("dev1"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", prev);
            try { Directory.Delete(adopted, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ProfileDir_IsPerDevice()
    {
        Assert.NotEqual(LinuxPanelKioskHost.ProfileDir("dev1"), LinuxPanelKioskHost.ProfileDir("dev2"));
    }

    // Ubuntu ships Chromium only as a snap with no /usr/bin symlink, so the
    // probe list must carry the snap path or FindChromium returns null there.
    [Fact]
    public void ChromiumFamily_ProbesSnapAfterNativePaths()
    {
        var family = LinuxBrowsers.ChromiumFamily();

        foreach (var native in new[] { "/usr/bin/chromium", "/usr/bin/brave-browser" })
        {
            var nativeIndex = Array.IndexOf(family, native);
            Assert.True(nativeIndex >= 0, $"{native} missing from the probe list");
            foreach (var snap in new[] { "/snap/bin/chromium", "/snap/bin/brave" })
            {
                var snapIndex = Array.IndexOf(family, snap);
                Assert.True(snapIndex >= 0, $"{snap} missing from the probe list");
                Assert.True(snapIndex > nativeIndex, $"{snap} must rank below {native}");
            }
        }
    }

    /// <summary>The Y70 slot self-registers: no device id in the URL, so the
    /// kiosk allocates a y70 record from its own (compositor-shaped) viewport.</summary>
    [Fact]
    public void KioskUrl_Y70SlotLoadsPanelRootAndMonitorsLoadTheirRecord()
    {
        Assert.Equal("http://localhost:9400/panel?token=t%20k",
            LinuxPanelKioskHost.KioskUrl(9400, LinuxPanelKioskHost.Y70Slot, "t k"));
        Assert.Equal("http://localhost:9400/panel/abc%2F1?token=tok",
            LinuxPanelKioskHost.KioskUrl(9400, "abc/1", "tok"));
    }

    [Fact]
    public void Diff_TreatsTheY70SlotLikeAnyOtherAssignment()
    {
        var running = new Dictionary<string, string>();
        var desired = new List<(string, string)> { (LinuxPanelKioskHost.Y70Slot, LinuxPanelKioskHost.Y70Slot) };
        var (toClose, toOpen) = LinuxPanelKioskHost.Diff(running, desired);
        Assert.Empty(toClose);
        Assert.Equal((LinuxPanelKioskHost.Y70Slot, LinuxPanelKioskHost.Y70Slot), Assert.Single(toOpen));

        running[LinuxPanelKioskHost.Y70Slot] = LinuxPanelKioskHost.Y70Slot;
        (toClose, toOpen) = LinuxPanelKioskHost.Diff(running, new List<(string, string)>());
        Assert.Equal(LinuxPanelKioskHost.Y70Slot, Assert.Single(toClose));
        Assert.Empty(toOpen);
    }

    [Fact]
    public void FlatpakAppId_RecognisesExportPathsOnly()
    {
        Assert.Equal("org.chromium.Chromium",
            LinuxBrowsers.FlatpakAppId("/home/u/.local/share/flatpak/exports/bin/org.chromium.Chromium"));
        Assert.Equal("com.brave.Browser",
            LinuxBrowsers.FlatpakAppId("/var/lib/flatpak/exports/bin/com.brave.Browser"));
        Assert.Null(LinuxBrowsers.FlatpakAppId("/usr/bin/chromium"));
        Assert.Null(LinuxBrowsers.FlatpakAppId("/snap/bin/chromium"));
    }
}
