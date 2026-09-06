using System.Linq;
using Nexus.Service.Peripherals;
using Xunit;

namespace Nexus.Service.Tests;

public class AllSupportedDevicesTests
{
    [Fact]
    public void Merged_IncludesBothPeripheralsAndLighting()
    {
        var all = AllSupportedDevices.All;
        // Source "nexus" + category "controller" is reachable only from
        // SupportedDevicesCatalog; the OpenRGB rows are all Source "openrgb".
        Assert.Contains(all, d => d.Source == "nexus" && d.Category == "controller");
        Assert.Contains(all, d => d.Vendor == "Tryx");      // from the lighting catalog
        Assert.True(all.Count > 1500);
    }

    [Fact]
    public void Merged_HasNoDuplicateRealVidPid()
    {
        var dups = AllSupportedDevices.All
            .Where(d => d.VendorId is not ("-" or "") && d.ProductId is not ("-" or ""))
            .GroupBy(d => d.VendorId.ToUpperInvariant() + ":" + d.ProductId.ToUpperInvariant())
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(dups);
    }

    [Fact]
    public void Merged_UnionsCapabilitiesForDevicesInBothCatalogs()
    {
        string Key(Nexus.Service.Models.Peripherals.SupportedDeviceDto d) =>
            d.VendorId.ToUpperInvariant() + ":" + d.ProductId.ToUpperInvariant();
        bool Real(Nexus.Service.Models.Peripherals.SupportedDeviceDto d) =>
            d.VendorId is not ("-" or "") && d.ProductId is not ("-" or "");

        var peripheralKeys = SupportedDevicesCatalog.All.Where(Real).Select(Key).ToHashSet();
        var overlap = LightingDevicesCatalog.All.Where(Real).Where(d => peripheralKeys.Contains(Key(d))).ToList();

        var merged = AllSupportedDevices.All.Where(Real).ToDictionary(Key, d => d);
        foreach (var lit in overlap)
        {
            var per = SupportedDevicesCatalog.All.First(d => Real(d) && Key(d) == Key(lit));
            var row = merged[Key(lit)];
            Assert.Equal("nexus", row.Source); // native metadata wins over openrgb
            foreach (var cap in per.Capabilities.Concat(lit.Capabilities))
            {
                Assert.Contains(cap, row.Capabilities);
            }
        }
    }

    [Theory]
    [InlineData("Tryx", "Panorama")]
    [InlineData("HYTE", "THICC Q60")]
    [InlineData("Corsair", "iCUE LINK LCD")]
    public void Merged_ScreenDevicesCarryScreenCapability(string vendor, string model)
    {
        var d = AllSupportedDevices.All.Single(x => x.Vendor == vendor && x.Model == model);
        Assert.Contains("rgb", d.Capabilities);
        Assert.Contains("screen", d.Capabilities);
    }

    [Fact]
    public void Merged_LeadsWithHyteAndIbuypowerInCatalogOrder()
    {
        var all = AllSupportedDevices.All;
        var leading = all.TakeWhile(d => AllSupportedDevices.IsLeadingVendor(d.Vendor)).ToList();
        Assert.NotEmpty(leading);
        Assert.Contains(leading, d => d.Vendor == "HYTE");
        Assert.Contains(leading, d => d.Vendor == "iBUYPOWER");
        // No first-party row after the first foreign row.
        Assert.DoesNotContain(all.Skip(leading.Count), d => AllSupportedDevices.IsLeadingVendor(d.Vendor));
        Assert.Contains(all.Skip(leading.Count), d => d.Vendor == "Elgato");
        // Catalog order survives inside the leading block.
        var q60 = leading.FindIndex(d => d.Model == "THICC Q60");
        var miniHub = leading.FindIndex(d => d.Model == "MiniHub");
        Assert.True(q60 >= 0 && miniHub > q60);
    }

    [Fact]
    public void LeadingVendorsFirst_IsAStablePartition()
    {
        Nexus.Service.Models.Peripherals.SupportedDeviceDto Row(string vendor, string model) =>
            new() { Vendor = vendor, Model = model, VendorId = "-", ProductId = "-" };
        var rows = new System.Collections.Generic.List<Nexus.Service.Models.Peripherals.SupportedDeviceDto>
        {
            Row("Elgato", "a"), Row("HYTE", "b"), Row("Corsair", "c"), Row("iBUYPOWER", "d"), Row("hyte", "e"), Row("Lian Li", "f"),
        };
        var ordered = AllSupportedDevices.LeadingVendorsFirst(rows).Select(d => d.Model).ToList();
        Assert.Equal(new[] { "b", "d", "e", "a", "c", "f" }, ordered);
    }

    [Fact]
    public void Merged_OpenRgbDevicesCarryRgbCapability()
    {
        var openRgb = AllSupportedDevices.All.Where(d => d.Source == "openrgb").ToList();
        Assert.NotEmpty(openRgb);
        Assert.All(openRgb, d => Assert.Contains("rgb", d.Capabilities));
    }
}
