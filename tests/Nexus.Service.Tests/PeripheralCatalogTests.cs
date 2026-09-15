using System.Linq;
using Nexus.Service.Peripherals;
using Xunit;

namespace Nexus.Service.Tests;

public class PeripheralCatalogTests
{
    [Fact]
    public void SupportedDevicesCatalog_ContainsStreamDecks()
    {
        var decks = SupportedDevicesCatalog.All.Where(d => d.Vendor == "Elgato").ToList();
        Assert.NotEmpty(decks);
        Assert.Contains(decks, d => d.ProductId.Equals("0x0080", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SupportedDevicesCatalog_ClaimsOnlyNativelyDrivenDevices()
    {
        // A row here is a support claim. Devices Nexus only detects, or only
        // lights up through OpenRGB, belong in LightingDevicesCatalog instead.
        Assert.All(SupportedDevicesCatalog.All, d => Assert.Equal("nexus", d.Source));
    }

    [Fact]
    public void SupportedDevicesCatalog_HasStableCategories()
    {
        foreach (var d in SupportedDevicesCatalog.All)
        {
            Assert.Contains(d.Category, new[] { "mouse", "keyboard", "headset", "controller" });
        }
    }

    [Fact]
    public void SupportedDevicesCatalog_AllEntriesHaveVidPid()
    {
        foreach (var d in SupportedDevicesCatalog.All)
        {
            Assert.StartsWith("0x", d.VendorId);
            Assert.StartsWith("0x", d.ProductId);
            Assert.Equal(6, d.VendorId.Length);
            Assert.Equal(6, d.ProductId.Length);
        }
    }

    [Fact]
    public void LightingDevicesCatalog_LoadsFromEmbeddedResource()
    {
        // LightingDevicesCatalog.All loads from the openrgb-supported-devices.json
        // embedded resource; a missing resource yields an empty list, so a
        // populated catalog is the proof the resource embedded and parsed.
        Assert.NotEmpty(LightingDevicesCatalog.All);
    }

    [Fact]
    public void LightingDevicesCatalog_CategoriesAreReasonable_WhenPopulated()
    {
        foreach (var d in LightingDevicesCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Category));
            Assert.False(string.IsNullOrWhiteSpace(d.Model));
        }
    }

    [Fact]
    public void LightingDevicesCatalog_InjectsFirstPartyHyteDevices()
    {
        var hyte = LightingDevicesCatalog.All.Where(d => d.Vendor == "HYTE").ToList();
        Assert.Contains(hyte, d => d.Model == "THICC Q60");
        Assert.Contains(hyte, d => d.Model == "THICC Q80");
        Assert.Contains(hyte, d => d.Model == "Nexus Portal NP50");
        // The OpenRGB fork's own HYTE rows are suppressed, so every HYTE row is first-party.
        Assert.All(hyte, d => Assert.Equal("nexus", d.Source));
    }

    [Fact]
    public void LightingDevicesCatalog_marks_galahad_lcd_as_rgb_and_screen_capable()
    {
        var galahad = Assert.Single(LightingDevicesCatalog.All,
            d => d.Vendor == "Lian Li" && d.Model == "Galahad II LCD");

        Assert.Contains("rgb", galahad.Capabilities);
        Assert.Contains("screen", galahad.Capabilities);
        Assert.Equal("0x7395", galahad.ProductId, ignoreCase: true);
    }

    [Fact]
    public void LightingDevicesCatalog_DropsMislabeledNexusCaseRow()
    {
        // The OpenRGB "HYTE Nexus" detector's row is mislabeled model "Nexus";
        // the curated first-party list replaces every HYTE row.
        Assert.DoesNotContain(LightingDevicesCatalog.All, d => d.Vendor == "HYTE" && d.Model == "Nexus");
        Assert.DoesNotContain(LightingDevicesCatalog.All, d => d.Vendor == "HYTE" && d.Source == "openrgb");
    }

    [Fact]
    public void LightingDevicesCatalog_EverySourceIsKnown()
    {
        Assert.All(LightingDevicesCatalog.All, d => Assert.Contains(d.Source, new[] { "nexus", "openrgb" }));
    }
}
