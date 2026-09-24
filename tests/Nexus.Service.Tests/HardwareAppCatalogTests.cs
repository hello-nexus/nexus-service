using System;
using Nexus.Service.Sensors;
using Nexus.Service.Store;
using Xunit;

namespace Nexus.Service.Tests;

public class HardwareAppCatalogTests
{
    private static HardwareAppCatalog Catalog(string panelVariant = "", bool ibuypower = false) =>
        new(() => panelVariant, () => ibuypower);

    [Fact]
    public void NoHardwareMatchesNothing()
    {
        Assert.Empty(Catalog().MatchedAppIds());
    }

    [Fact]
    public void InaPanelMatchesOnlyIna()
    {
        var matched = Catalog(panelVariant: "y70-ina").MatchedAppIds();
        Assert.Equal(new[] { HardwareAppCatalog.InaAppId }, matched);
    }

    [Fact]
    public void TheOtherDdcOnlyY70VariantDoesNotMatch()
    {
        Assert.Empty(Catalog(panelVariant: "y70-gw").MatchedAppIds());
    }

    [Fact]
    public void IbuypowerBoardMatchesOnlyItsApp()
    {
        var matched = Catalog(ibuypower: true).MatchedAppIds();
        Assert.Equal(new[] { HardwareAppCatalog.IbuypowerAppId }, matched);
    }

    [Fact]
    public void BothPiecesOfHardwareMatchBothApps()
    {
        var matched = Catalog(panelVariant: "y70-ina", ibuypower: true).MatchedAppIds();
        Assert.Equal(2, matched.Count);
        Assert.Contains(HardwareAppCatalog.InaAppId, matched);
        Assert.Contains(HardwareAppCatalog.IbuypowerAppId, matched);
    }

    // The account gate is waived on a match, so an id outside the table must
    // never match however the hardware reads.
    [Fact]
    public void AnUnlistedAppIdNeverMatches()
    {
        var catalog = Catalog(panelVariant: "y70-ina", ibuypower: true);
        Assert.False(catalog.IsMatched("com.example.other"));
        Assert.False(catalog.IsMatched(""));
        Assert.False(HardwareAppCatalog.IsHardwareApp("com.example.other"));
    }

    [Fact]
    public void ManufacturerMatchIsCaseAndWhitespaceInsensitive()
    {
        Assert.True(OemInfo.Matches("  ibuypower ", new[] { "iBUYPOWER" }));
        Assert.False(OemInfo.Matches("iBUYPOWER Inc", new[] { "iBUYPOWER" }));
        Assert.False(OemInfo.Matches(null, new[] { "iBUYPOWER" }));
    }
}
