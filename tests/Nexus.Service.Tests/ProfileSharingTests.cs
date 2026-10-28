using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

public class ProfileSharingTests
{
    [Fact]
    public void All_ListsSixCategoriesInDocumentedOrder()
    {
        Assert.Equal(
            new[] { ProfileSharing.Lighting, ProfileSharing.Cooling, ProfileSharing.Theme, ProfileSharing.Dashboard, ProfileSharing.Device, ProfileSharing.Panels },
            ProfileSharing.All);
    }

    [Theory]
    [InlineData("device")]
    [InlineData("DEVICE")]
    [InlineData(" Device ")]
    public void Normalize_AcceptsDeviceCaseAndWhitespaceInsensitive(string input)
    {
        Assert.Equal(ProfileSharing.Device, ProfileSharing.Normalize(input));
    }

    [Fact]
    public void WriteCategory_Theme_never_touches_the_live_hosts_theme()
    {
        var live = new NexusSettings();
        live.Theme.ResolvedThemeMode = "light";
        var file = new NexusSettings();

        ProfileSharing.WriteCategory(file, live, ProfileSharing.Theme);

        Assert.Equal("light", live.Theme.ResolvedThemeMode);
    }

    [Fact]
    public void Theme_apply_and_reset_keep_the_targets_host_theme()
    {
        var source = new NexusSettings();
        source.Theme.ThemeMode = "dark";
        source.Theme.ResolvedThemeMode = "dark";
        var target = new NexusSettings();
        target.Theme.ResolvedThemeMode = "light";

        ProfileSharing.ApplyCategory(target, source, ProfileSharing.Theme);
        Assert.Equal("dark", target.Theme.ThemeMode);
        Assert.Equal("light", target.Theme.ResolvedThemeMode);

        ProfileSharing.ResetCategory(target, ProfileSharing.Theme);
        Assert.Equal("system", target.Theme.ThemeMode);
        Assert.Equal("light", target.Theme.ResolvedThemeMode);
    }

    [Fact]
    public void ApplyCategory_Dashboard_CarriesAndResetsTheDashboardGaugeGradient()
    {
        var source = new NexusSettings();
        source.Panel.DashboardGaugeGradient = new List<Nexus.Service.Models.Panel.PanelGaugeGradientStop>
        {
            new() { At = 0.2, Color = "accent" },
            new() { At = 0.9, Color = "#ef4444" },
        };

        var target = new NexusSettings();
        ProfileSharing.ApplyCategory(target, source, ProfileSharing.Dashboard);
        Assert.NotNull(target.Panel.DashboardGaugeGradient);
        Assert.Equal("accent", target.Panel.DashboardGaugeGradient![0].Color);

        ProfileSharing.ResetCategory(target, ProfileSharing.Dashboard);
        Assert.Null(target.Panel.DashboardGaugeGradient);
    }

    [Fact]
    public void Dashboard_LeavesTheWorkstationConflictWhitelistAlone()
    {
        var source = new NexusSettings();
        source.Ui.ConflictAutoKillExclusions = new List<string>();
        var target = new NexusSettings();
        target.Ui.ConflictAutoKillExclusions = new List<string> { "gcc" };

        ProfileSharing.ApplyCategory(target, source, ProfileSharing.Dashboard);
        Assert.Equal(new[] { "gcc" }, target.Ui.ConflictAutoKillExclusions);

        ProfileSharing.ResetCategory(target, ProfileSharing.Dashboard);
        Assert.Equal(new[] { "gcc" }, target.Ui.ConflictAutoKillExclusions);
    }

    [Fact]
    public void Dashboard_LeavesTheWorkstationHiddenSidebarDevicesAlone()
    {
        var source = new NexusSettings();
        var target = new NexusSettings();
        target.Ui.HiddenSidebarDevices = new List<string> { "curated:keeb" };

        ProfileSharing.ApplyCategory(target, source, ProfileSharing.Dashboard);
        Assert.Equal(new[] { "curated:keeb" }, target.Ui.HiddenSidebarDevices);

        ProfileSharing.ResetCategory(target, ProfileSharing.Dashboard);
        Assert.Equal(new[] { "curated:keeb" }, target.Ui.HiddenSidebarDevices);
    }

    [Fact]
    public void Dashboard_LeavesTheRecentLightingListsAlone()
    {
        var source = new NexusSettings();
        var target = new NexusSettings();
        target.Ui.RecentStaticColors = new List<string> { "#FF8800" };
        target.Ui.RecentAnimations = new List<string> { "fire" };

        ProfileSharing.ApplyCategory(target, source, ProfileSharing.Dashboard);
        ProfileSharing.ResetCategory(target, ProfileSharing.Dashboard);

        Assert.Equal(new[] { "#FF8800" }, target.Ui.RecentStaticColors);
        Assert.Equal(new[] { "fire" }, target.Ui.RecentAnimations);
    }

    [Fact]
    public void ApplyCategory_Device_ReplacesTargetStreamDeckWithSources()
    {
        var source = new NexusSettings();
        source.StreamDeck.Decks["SN-SOURCE"] = new PhysicalDeckSettings { Name = "Source Deck" };

        var target = new NexusSettings();
        target.StreamDeck.Decks["SN-STALE"] = new PhysicalDeckSettings { Name = "Stale Deck" };

        ProfileSharing.ApplyCategory(target, source, ProfileSharing.Device);

        var deck = Assert.Single(target.StreamDeck.Decks);
        Assert.Equal("SN-SOURCE", deck.Key);
        Assert.Equal("Source Deck", deck.Value.Name);
    }

    /// <summary>The recent-apps ring names this machine's processes and exe paths, so a profile export or cloud sync never carries it.</summary>
    [Fact]
    public void ApplyCategory_Device_NeverCarriesTheRecentAppsRing()
    {
        var source = new NexusSettings();
        source.StreamDeck.RecentApps.Add(new RecentApp { ProcessKey = "msedge", Name = "Microsoft Edge", ExePath = @"C:\edge.exe" });
        source.StreamDeck.RecentAppsExcluded.Add("explorer");

        var extract = ProfileSharing.ExtractShareable(source);

        Assert.Empty(extract.StreamDeck.RecentApps);
        Assert.Empty(extract.StreamDeck.RecentAppsExcluded);
        // Extracting must not touch the live ring itself.
        Assert.Single(source.StreamDeck.RecentApps);
        Assert.Single(source.StreamDeck.RecentAppsExcluded);
    }

    /// <summary>Loading a profile (whose file carries no ring) keeps the live machine's ring.</summary>
    [Fact]
    public void ApplyCategory_Device_KeepsTheTargetsRecentAppsRing()
    {
        var live = new NexusSettings();
        live.StreamDeck.RecentApps.Add(new RecentApp { ProcessKey = "msedge", Name = "Microsoft Edge" });
        var fromFile = new NexusSettings();
        fromFile.StreamDeck.Decks["SN-1"] = new PhysicalDeckSettings { Name = "Deck" };

        ProfileSharing.ApplyCategory(live, fromFile, ProfileSharing.Device);

        Assert.Single(live.StreamDeck.Decks);
        Assert.Single(live.StreamDeck.RecentApps);
    }

    [Fact]
    public void ApplyCategory_Device_ReplacesTargetKeebWithSources()
    {
        var source = new NexusSettings();
        source.Keeb.RotaryLeft = "Volume";
        source.Keeb.GameMode.AltF4 = true;

        var target = new NexusSettings();
        target.Keeb.RotaryLeft = "Scroll";

        ProfileSharing.ApplyCategory(target, source, ProfileSharing.Device);

        Assert.Equal("Volume", target.Keeb.RotaryLeft);
        Assert.True(target.Keeb.GameMode.AltF4);
    }

    [Fact]
    public void ApplyCategory_Device_LeavesOtherCategoriesOnTargetUntouched()
    {
        var source = new NexusSettings();
        source.StreamDeck.Decks["SN-SOURCE"] = new PhysicalDeckSettings();

        var target = new NexusSettings();
        target.Lighting.LastMediaId = "keep-me";

        ProfileSharing.ApplyCategory(target, source, ProfileSharing.Device);

        Assert.Equal("keep-me", target.Lighting.LastMediaId);
    }

    [Fact]
    public void ResetCategory_Device_ClearsStreamDeckToFreshDefault()
    {
        var target = new NexusSettings();
        target.StreamDeck.Decks["SN-1"] = new PhysicalDeckSettings { Name = "Deck" };

        ProfileSharing.ResetCategory(target, ProfileSharing.Device);

        Assert.Empty(target.StreamDeck.Decks);
    }

    [Fact]
    public void ResetCategory_Device_ClearsKeebToFreshDefault()
    {
        var target = new NexusSettings();
        target.Keeb.RotaryLeft = "Volume";
        target.Keeb.GameMode.AltF4 = true;

        ProfileSharing.ResetCategory(target, ProfileSharing.Device);

        Assert.Equal(new KeebSettings().RotaryLeft, target.Keeb.RotaryLeft);
        Assert.False(target.Keeb.GameMode.AltF4);
    }
}
