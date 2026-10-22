using System.Linq;
using Nexus.Service.Conflicts;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

public class ConflictPillarFilterTests
{
    private static ConflictAppDefinition App(string id) => ConflictAppCatalog.All.Single(a => a.Id == id);

    [Theory]
    [InlineData("signalrgb", true, true, true)]
    [InlineData("signalrgb", true, false, true)]
    [InlineData("signalrgb", false, true, false)]
    [InlineData("signalrgb", false, false, false)]
    [InlineData("fan-control", false, true, true)]
    [InlineData("fan-control", true, false, false)]
    [InlineData("fan-control", false, false, false)]
    public void SinglePillarApp_CompetesOnlyWhileItsPillarIsOn(string id, bool lighting, bool cooling, bool competes)
    {
        Assert.Equal(competes, ConflictAppCatalog.Competes(App(id), lighting, cooling));
    }

    [Theory]
    [InlineData("armoury-crate", true, false, true)]
    [InlineData("armoury-crate", false, true, true)]
    [InlineData("armoury-crate", false, false, false)]
    public void DualApp_IsIgnoredOnlyWhenBothPillarsAreOff(string id, bool lighting, bool cooling, bool competes)
    {
        Assert.Equal(competes, ConflictAppCatalog.Competes(App(id), lighting, cooling));
    }

    [Theory]
    [InlineData("hyte-nexus-2")]
    [InlineData("razer-synapse")]
    [InlineData("msi-afterburner")]
    public void AlwaysEndedPeripheralAndMonitoringApps_CompeteWithBothPillarsOff(string id)
    {
        Assert.True(ConflictAppCatalog.Competes(App(id), lightingOn: false, coolingOn: false));
    }

    [Theory]
    [InlineData("nzxt-cam", "nzxt-kraken")]
    [InlineData("tryx-kanali", "tryx")]
    [InlineData("armoury-crate", "asus-ryujin-lcd")]
    [InlineData("deepcool", "deepcool-mystique")]
    public void ADisplayApp_CompetesWithBothPillarsOff_OnlyForAHandlerInUse(string id, string handler)
    {
        Assert.False(ConflictAppCatalog.Competes(App(id), false, false));
        Assert.False(ConflictAppCatalog.Competes(App(id), false, false, h => false));
        Assert.True(ConflictAppCatalog.Competes(App(id), false, false, h => h == handler));
    }

    [Theory]
    [InlineData("icue", "corsair")]
    [InlineData("lian-li-l-connect", "lianli")]
    [InlineData("lian-li-l-connect", "lianli-wireless")]
    [InlineData("nzxt-cam", "nzxt-kraken")]
    [InlineData("asiahorse-umbra", "asiahorse-umbra")]
    public void ASecondaryPillarWithHandlers_CompetesOnlyWhileOneOfThemIsInUse(string id, string handler)
    {
        Assert.True(ConflictAppCatalog.Competes(App(id), true, false));
        Assert.False(ConflictAppCatalog.Competes(App(id), false, true, null, h => false));
        Assert.True(ConflictAppCatalog.Competes(App(id), false, true, null, h => h == handler));
        Assert.False(ConflictAppCatalog.Competes(App(id), false, false, null, h => true));
    }

    [Fact]
    public void NzxtKrakenLighting_CompetesWithCoolingOnlyWhileTheKrakenIsInUse()
    {
        Assert.True(ConflictAppCatalog.Competes(App("nzxt-kraken"), false, true));
        Assert.False(ConflictAppCatalog.Competes(App("nzxt-kraken"), true, false, null, h => false));
        Assert.True(ConflictAppCatalog.Competes(App("nzxt-kraken"), true, false, null, h => h == "nzxt-kraken"));
    }

    [Fact]
    public void LConnectDisplayHandlers_IncludeTheWirelessLcdScreens()
    {
        Assert.Contains("lianli-wireless", App("lian-li-l-connect").DisplayHandlers);
    }

    [Theory]
    [InlineData("armoury-crate", false, true, true)]
    [InlineData("msi-center", false, true, true)]
    [InlineData("msi-gaming-center", true, false, true)]
    [InlineData("evga-aio", true, false, true)]
    [InlineData("thermaltake-rgb-plus", false, true, true)]
    [InlineData("cooler-master-plus", false, true, true)]
    [InlineData("armoury-crate", false, false, false)]
    [InlineData("evga-aio", false, false, false)]
    [InlineData("icue", false, false, false)]
    [InlineData("lian-li-l-connect", false, false, false)]
    public void PillarLevelDualApps_CompeteOnEitherPillar(string id, bool lighting, bool cooling, bool competes)
    {
        Assert.Equal(competes, ConflictAppCatalog.Competes(App(id), lighting, cooling));
    }

    [Fact]
    public void LConnectDisplayHandlers_AreDisplaysOnly()
    {
        var handlers = App("lian-li-l-connect").DisplayHandlers;
        Assert.DoesNotContain("lianli-aio", handlers);
        Assert.Contains("lianli-tl-lcd", handlers);
    }

    [Fact]
    public void EveryDualApp_NamesADifferentSecondCategory()
    {
        foreach (var app in ConflictAppCatalog.All.Where(a => a.AlsoCategory.Length > 0))
        {
            Assert.NotEqual(app.Category, app.AlsoCategory);
            Assert.Contains(app.AlsoCategory, new[] { "lighting", "cooling" });
            Assert.Contains(app.Category, new[] { "lighting", "cooling" });
        }
    }
}
