using System;
using System.IO;
using System.Linq;
using Nexus.Service.Conflicts;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

/// <summary>
/// Covers the pure halves of the autostart recipe - command-line parsing and
/// executable matching - plus the catalog invariants that keep the action off
/// apps nobody has verified. The registry halves are Windows-only and are
/// verified on a lab box, not here.
/// </summary>
public class ConflictAutostartTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Corsair\\Corsair iCUE5 Software\\iCUE Launcher.exe\" --autorun", "C:\\Program Files\\Corsair\\Corsair iCUE5 Software\\iCUE Launcher.exe")]
    [InlineData("C:\\Program Files\\NZXT CAM\\NZXT CAM.exe -s", "C:\\Program Files\\NZXT CAM\\NZXT CAM.exe")]
    [InlineData("\"C:/Users/nicol/AppData/Local/WhirlwindFX/SignalRgb/SignalRgbLauncher.exe\"", "C:\\Users\\nicol\\AppData\\Local\\WhirlwindFX\\SignalRgb\\SignalRgbLauncher.exe")]
    public void ExecutablePathDropsQuotesAndArguments(string command, string expected)
    {
        Assert.Equal(expected, ConflictAutostart.ExecutablePath(command));
    }

    [Fact]
    public void MatchesExecutableComparesTheFileNameOnly()
    {
        var names = new[] { "iCUE Launcher.exe" };
        // The value name and the install directory both vary by version; the
        // executable is what is actually matched.
        Assert.True(ConflictAutostart.MatchesExecutable(
            "\"C:\\Program Files\\Corsair\\Corsair iCUE5 Software\\iCUE Launcher.exe\" --autorun", names));
        Assert.True(ConflictAutostart.MatchesExecutable(
            "\"D:\\Games\\Corsair\\iCUE4\\icue launcher.exe\"", names));
        Assert.False(ConflictAutostart.MatchesExecutable(
            "\"C:\\Program Files\\Corsair\\Corsair iCUE5 Software\\iCUEUpdateService.exe\"", names));
    }

    [Fact]
    public void AnEmptyOrUnparseableCommandMatchesNothing()
    {
        var names = new[] { "iCUE Launcher.exe" };
        Assert.False(ConflictAutostart.MatchesExecutable("", names));
        Assert.False(ConflictAutostart.MatchesExecutable("   ", names));
        Assert.False(ConflictAutostart.MatchesExecutable("rundll32", names));
    }

    [Theory]
    [InlineData("icue")]
    [InlineData("nzxt-cam")]
    [InlineData("signalrgb")]
    [InlineData("razer-synapse")]
    [InlineData("hyte-nexus-2")]
    public void TheVerifiedAppsKeepTheirRecipe(string id)
    {
        var def = ConflictWatcher.FindById(id);
        Assert.NotNull(def);
        Assert.NotEmpty(def!.Autostart);
    }

    [Fact]
    public void NoOtherAppCarriesARecipe()
    {
        // The action exists only for apps verified end to end on a lab box.
        // Adding a recipe means having watched the app stay down over a
        // reboot; this fails until that list is updated deliberately.
        var verified = new[] { "icue", "nzxt-cam", "signalrgb", "razer-synapse", "hyte-nexus-2" };
        var withRecipe = ConflictAppCatalog.All
            .Where(d => d.Autostart.Count > 0)
            .Select(d => d.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(verified.OrderBy(id => id, StringComparer.Ordinal).ToArray(), withRecipe);
    }

    [Fact]
    public void EveryRecipeEntryIsWellFormed()
    {
        var kinds = new[]
        {
            ConflictAutostart.KindRunKeyMachine,
            ConflictAutostart.KindRunKeyUser,
            ConflictAutostart.KindService,
            ConflictAutostart.KindScheduledTask,
        };
        foreach (var def in ConflictAppCatalog.All)
        {
            foreach (var target in def.Autostart)
            {
                Assert.True(Array.IndexOf(kinds, target.Kind) >= 0, $"unknown kind {target.Kind}");
                if (target.Kind == ConflictAutostart.KindScheduledTask)
                {
                    // Names a task and nothing else; the name is joined onto
                    // System32\\Tasks, so it must stay a bare relative name.
                    Assert.NotEmpty(target.Name);
                    Assert.Empty(target.ExeNames);
                    Assert.DoesNotContain("..", target.Name);
                    Assert.False(Path.IsPathRooted(target.Name));
                    continue;
                }
                if (target.Kind == ConflictAutostart.KindService)
                {
                    // A service entry names the service, and only ever one the
                    // catalog already vets for End task.
                    Assert.False(string.IsNullOrWhiteSpace(target.Name));
                    Assert.Contains(target.Name, def.WindowsServiceNames, StringComparer.OrdinalIgnoreCase);
                    Assert.Empty(target.ExeNames);
                }
                else
                {
                    // A Run entry matches on executables, never on a name.
                    Assert.NotEmpty(target.ExeNames);
                    Assert.All(target.ExeNames, n => Assert.EndsWith(".exe", n, StringComparison.OrdinalIgnoreCase));
                    Assert.Equal("", target.Name);
                }
            }
        }
    }

    [Fact]
    public void NoRecipeClaimsWindowsOwnCameraService()
    {
        foreach (var def in ConflictAppCatalog.All)
        {
            Assert.DoesNotContain("camsvc", def.Autostart.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SynapseFourIsDetectable()
    {
        // A 4.x install carries none of the Synapse 3 process names, so without
        // this the app is invisible to the watcher and the action never shows.
        var def = ConflictWatcher.FindById("razer-synapse");
        Assert.NotNull(def);
        Assert.Contains("RazerAppEngine", def!.ProcessNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlyICueGatesOnAVendorSetting()
    {
        // The gate exists because iCUE's Run value is armed on boxes where iCUE
        // does not start. Any app gaining one needs the same two-box evidence.
        foreach (var def in ConflictAppCatalog.All)
        {
            foreach (var target in def.Autostart)
            {
                // The XML gate specifically; the appConfigJson kind also names a
                // file but is checked by its own test.
                if (target.VendorConfigXmlValue.Length == 0) continue;
                Assert.Equal("icue", def.Id);
                // Both halves or neither - one alone silently disables the gate.
                Assert.NotEmpty(target.VendorConfigAppDataPath);
                Assert.NotEmpty(target.VendorConfigXmlValue);
                Assert.Equal(ConflictAutostart.KindRunKeyMachine, target.Kind);
                // The path is joined onto the console user's profile, so it has
                // to stay relative and inside it.
                Assert.DoesNotContain("..", target.VendorConfigAppDataPath);
                Assert.False(Path.IsPathRooted(target.VendorConfigAppDataPath));
            }
        }
    }

    [Fact]
    public void XmlValueIsTrueReadsTheVendorFlag()
    {
        // Shape taken verbatim from config.cuecfg on the lab boxes.
        const string on = "<?xml version=\"1.0\"?><config><map name=\"CommonSettings\"><value name=\"StartOnStartup\">true</value></map></config>";
        const string off = "<?xml version=\"1.0\"?><config><map name=\"CommonSettings\"><value name=\"StartOnStartup\">false</value></map></config>";
        Assert.True(ConflictAutostart.XmlValueIsTrue(on, "StartOnStartup"));
        Assert.False(ConflictAutostart.XmlValueIsTrue(off, "StartOnStartup"));
    }

    [Fact]
    public void AnUnreadableVendorFlagCountsAsWillNotStart()
    {
        // Fail closed: a renamed value in a future iCUE withdraws the action
        // rather than offering one on a guess.
        Assert.False(ConflictAutostart.XmlValueIsTrue("<config></config>", "StartOnStartup"));
        Assert.False(ConflictAutostart.XmlValueIsTrue("", "StartOnStartup"));
        Assert.False(ConflictAutostart.XmlValueIsTrue("<value name=\"StartOnStartup\">", "StartOnStartup"));
        Assert.False(ConflictAutostart.XmlValueIsTrue("<value name=\"Other\">true</value>", "StartOnStartup"));
    }

    [Fact]
    public void Nexus2UsesTheSameTaskTheMigrationPathDeletes()
    {
        // Two surfaces act on Nexus 2's autostart; if they name different tasks
        // one of them silently leaves it armed.
        var def = ConflictWatcher.FindById("hyte-nexus-2");
        Assert.NotNull(def);
        var task = Assert.Single(def!.Autostart);
        Assert.Equal(ConflictAutostart.KindScheduledTask, task.Kind);
        Assert.Equal("HYTE Nexus", task.Name);
    }



}
