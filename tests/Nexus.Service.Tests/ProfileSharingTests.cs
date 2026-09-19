using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

public class ProfileSharingTests
{
    [Fact]
    public void All_ListsFiveCategoriesInDocumentedOrder()
    {
        Assert.Equal(
            new[] { ProfileSharing.Lighting, ProfileSharing.Cooling, ProfileSharing.Theme, ProfileSharing.Dashboard, ProfileSharing.Device },
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
