using System.Linq;
using Nexus.Service.Peripherals;
using Xunit;

namespace Nexus.Service.Tests;

public class LightingDevicesCatalogTests
{
    [Fact]
    public void Catalog_IsLoadedFromEmbeddedOpenRgbResource()
    {
        var all = LightingDevicesCatalog.All;

        Assert.True(all.Count > 1500,
            $"expected the OpenRGB-derived catalog (>1500 devices), got {all.Count} - " +
            "the embedded resource is likely missing or stale");
    }

    [Fact]
    public void Catalog_ContainsKnownHidDeviceWithVidPid()
    {
        var all = LightingDevicesCatalog.All;

        var hyteKeeb = all.FirstOrDefault(d => d.Model.Contains("Keeb TKL"));
        Assert.NotNull(hyteKeeb);
        Assert.Equal("0x3402", hyteKeeb!.VendorId);
        Assert.Equal("0x0300", hyteKeeb.ProductId);
    }

    [Theory]
    // PIDs the extractor must resolve from sibling headers, not the detector file.
    [InlineData("Corsair K70 RGB PRO", "0x1B1C", "0x1BC4")]
    [InlineData("Corsair K70 Core RGB", "0x1B1C", "0x1BFD")]
    public void Catalog_OpenRgbRowsKeepPidsDefinedInHeaders(string name, string vid, string pid)
    {
        var device = LightingDevicesCatalog.All.SingleOrDefault(
            d => d.Source == "openrgb" && d.Vendor + " " + d.Model == name);

        Assert.NotNull(device);
        Assert.Equal(vid, device!.VendorId);
        Assert.Equal(pid, device.ProductId);
    }

    [Fact]
    public void Catalog_OpenRgbRowsMostlyCarryUsbIds()
    {
        // Only i2c/SMBus, generic and dynamic detectors (plus a few wildcard HID
        // registrations) have no USB PID. A regression in the extractor's macro
        // resolution shows up as hundreds of extra keyless rows.
        var openRgb = LightingDevicesCatalog.All.Where(d => d.Source == "openrgb").ToList();
        var withPid = openRgb.Count(d => d.ProductId != "-");

        Assert.True(withPid > 1000, $"expected >1000 OpenRGB rows with a USB PID, got {withPid} of {openRgb.Count}");
    }

    [Fact]
    public void Catalog_AssignsSensibleCategories()
    {
        var all = LightingDevicesCatalog.All;

        Assert.Contains(all, d => d.Category == "keyboard");
        Assert.Contains(all, d => d.Category == "mouse");
        Assert.Contains(all, d => d.Category == "gpu");
        Assert.Contains(all, d => d.Category == "motherboard");
    }

    [Theory]
    // Declared by the OpenRGB fork's own device tables, not the controller class name.
    [InlineData("Corsair", "Katar Pro", "mouse")]
    [InlineData("Corsair", "Katar Pro XT", "mouse")]
    [InlineData("Corsair", "K65 Mini", "keyboard")]
    // Runtime-typed controllers: resolved from the device name.
    [InlineData("Corsair", "K70 RGB MK.2", "keyboard")]
    [InlineData("Corsair", "Scimitar RGB", "mouse")]
    [InlineData("Logitech", "G502 HERO Gaming Mouse", "mouse")]
    [InlineData("Logitech", "G Powerplay Mousepad", "mousemat")]
    [InlineData("SteelSeries", "Aerox 3 Wireless", "mouse")]
    public void Catalog_OpenRgbRowsCarryTheDeviceType(string vendor, string model, string category)
    {
        var device = LightingDevicesCatalog.All.First(
            d => d.Source == "openrgb" && d.Vendor == vendor && d.Model == model);

        Assert.Equal(category, device.Category);
    }

    [Theory]
    [InlineData("Katar Pro", "CorsairPeripheralV2Controller", "hid", "mouse", "mouse")]
    [InlineData("Katar Pro", "CorsairPeripheralV2Controller", "hid", null, "mouse")]
    [InlineData("Corsair K95 RGB", "CorsairPeripheralController", "hid", null, "keyboard")]
    [InlineData("Corsair H100i Pro XT", "CorsairHydroPlatinumController", "hid", "cooler", "aio")]
    [InlineData("Lian Li Uni Hub", "LianLiController", "hid", "cooler", "fan")]
    [InlineData("NZXT RGB & Fan Controller", "NZXTHue2Controller", "hid", "ledstrip", "fan")]
    [InlineData("NZXT Hue 2", "NZXTHue2Controller", "hid", "ledstrip", "light")]
    [InlineData("Corsair Galleon K100 SD", "CorsairGalleonController", "hid", null, "keyboard")]
    [InlineData("MSI Optix MAG274QRF", "MSIOptixController", "hid", "light", "monitor")]
    [InlineData("NZXT B650E Motherboard", "NZXTHue2Controller", "hid", "ledstrip", "motherboard")]
    [InlineData("NZXT Hue 2 Motherboard", "NZXTHue2Controller", "hid", "ledstrip", "motherboard")]
    [InlineData("ASUS ROG Strix LC III", "AsusAuraUSBController", "hid", "motherboard", "aio")]
    [InlineData("NZXT Kraken X3", "NZXTKrakenController", "hid", "ledstrip", "aio")]
    [InlineData("NZXT Kraken 2024 Elite", "NZXTKrakenController", "hid", "ledstrip", "aio")]
    [InlineData("Razer Kraken 7.1", "RazerController", "hid", null, "headset")]
    [InlineData("Corsair Scimitar Elite RGB", "CorsairPeripheralController", "hid", null, "mouse")]
    [InlineData("Bloody W90 Pro", "A4TechController", "hid", null, "mouse")]
    [InlineData("Bloody MP 50RS", "A4TechController", "hid", null, "mousemat")]
    [InlineData("Ionico Light Bar", "IonicoController", "hid", null, "light")]
    [InlineData("Robobloq Monitor Light Strip", "RobobloqController", "hid", "ledstrip", "light")]
    [InlineData("Philips Hue", "PhilipsHueController", "remote", "light", "light")]
    // Nothing declared and no name hint: the controller-class heuristic still applies.
    [InlineData("Some Thing", "OpenRGBGPUController", "i2c", null, "gpu")]
    [InlineData("Some Thing", "MysteryController", "hid", null, "controller")]
    public void ResolveCategory_PrefersDeclaredTypeThenNameThenController(
        string name, string controller, string kind, string? type, string expected)
    {
        Assert.Equal(expected, LightingDevicesCatalog.ResolveCategory(name, controller, kind, type));
    }

    [Theory]
    [InlineData("Lian Li", "Uni Fan SL-Infinity", "0x0CF2", "0xA102")]
    [InlineData("Lian Li", "Galahad II Trinity", "0x0416", "0x7373")]
    [InlineData("Lian Li", "SL-LCD", "0x1CBE", "0x0005")]
    [InlineData("Lian Li", "HydroShift II LCD-S", "0x1CBE", "0xA034")]
    [InlineData("Lian Li", "HydroShift II OLED Curved", "0x1CBE", "0xA068")]
    [InlineData("Lian Li", "Strimer Wireless", "-", "-")]
    [InlineData("Asiahorse", "Umbra V2", "0x1A86", "0xFE04")]
    [InlineData("Tryx", "Panorama", "0x391A", "0x1011")]
    [InlineData("Aftershock", "Glacier Matrix 360", "0x38C1", "0x0026")]
    [InlineData("DeepCool", "Mystique", "0x3633", "0x0009")]
    [InlineData("Corsair", "Galleon 100 SD Keyboard", "0x1B1C", "0x2B0C")]
    [InlineData("Corsair", "Galleon 100 SD Dials", "0x1B1C", "0x2B18")]
    [InlineData("Corsair", "iCUE LINK System Hub", "0x1B1C", "0x0C3F")]
    [InlineData("NZXT", "Kraken Z3", "0x1E71", "0x3008")]
    [InlineData("NZXT", "Kraken X3", "0x1E71", "0x2007")]
    [InlineData("NZXT", "Kraken Elite", "0x1E71", "0x300C")]
    [InlineData("HYTE", "Y70 Touch", "0x3402", "0x0C00")]
    // DDC-only panels: EDID identity, no USB function, so no VID/PID.
    [InlineData("HYTE", "Y70 Touch GW", "-", "-")]
    [InlineData("HYTE", "Y70 Ina Touch", "-", "-")]
    [InlineData("iBUYPOWER", "MiniHub", "0x3402", "0x0900")]
    public void Catalog_ListsNativelyDrivenFirstPartyDevices(string vendor, string model, string vid, string pid)
    {
        var device = LightingDevicesCatalog.All.SingleOrDefault(
            d => d.Vendor == vendor && d.Model == model);

        Assert.NotNull(device);
        Assert.Equal("nexus", device!.Source);
        Assert.Equal(vid, device.VendorId);
        Assert.Equal(pid, device.ProductId);
    }

    [Theory]
    [InlineData("SteelSeries Apex Pro TKL 2023 Wired", "0x1628", true)]
    [InlineData("SteelSeries Apex Pro TKL Gen 3 Wired", "0x1642", true)]
    [InlineData("SteelSeries Apex Pro TKL 2023 Wireless", "0x1630", false)]
    [InlineData("SteelSeries Apex Pro TKL", "0x1614", false)]
    public void The_apex_screen_adds_a_capability_without_renaming_the_keyboard(string name, string pid, bool screen)
    {
        var rows = LightingDevicesCatalog.All.Where(d => d.ProductId == pid && d.VendorId == "0x1038").ToList();
        var row = Assert.Single(rows);

        Assert.Equal("openrgb", row.Source);
        Assert.Equal(name, $"{row.Vendor} {row.Model}");
        Assert.Contains("rgb", row.Capabilities);
        Assert.Equal(screen, row.Capabilities.Contains("screen"));
    }

    [Fact]
    public void Catalog_SuppressesOpenRgbRowsForNativelyDrivenHardware()
    {
        // Lian Li Uni Fan/Strimer/Galahad and the Corsair iCUE LINK hub also ship in
        // the OpenRGB resource; each VID:PID must survive exactly once, as "nexus".
        var all = LightingDevicesCatalog.All;

        foreach (var (vid, pid) in new[]
                 {
                     ("0x0CF2", "0xA102"), // Uni Fan SL-Infinity
                     ("0x0416", "0x7373"), // Galahad II Trinity
                     ("0x0CF2", "0xA200"), // Strimer
                     ("0x1B1C", "0x0C3F"), // iCUE LINK System Hub
                     ("0x1E71", "0x3012"), // Kraken Elite V2 - OpenRGB registers this PID too
                 })
        {
            var rows = all.Where(d => d.VendorId == vid && d.ProductId == pid).ToList();
            Assert.Single(rows);
            Assert.Equal("nexus", rows[0].Source);
        }
    }

    /// <summary>
    /// Every Kraken the service drives has a curated row, and the OpenRGB detector rows for
    /// the same hardware are gone. The X2/M2 rows must survive: they sit on a different
    /// OpenRGB controller and Nexus has no driver for them.
    /// </summary>
    [Fact]
    public void Catalog_CoversEveryKrakenTheServiceDrives()
    {
        var all = LightingDevicesCatalog.All;

        foreach (var model in Nexus.Service.Peripherals.Nzxt.KrakenModel.All)
        {
            var pid = "0x" + model.ProductId.ToString("X4");
            var rows = all.Where(d => d.VendorId == "0x1E71" && d.ProductId == pid).ToList();
            Assert.Single(rows);
            Assert.Equal("nexus", rows[0].Source);
            Assert.Equal("aio", rows[0].Category);
        }

        Assert.DoesNotContain(all, d => d.Source == "openrgb" && d.Model.Contains("Kraken X3"));
        Assert.Contains(all, d => d.Source == "openrgb" && d.ProductId == "0x170E");
        Assert.Contains(all, d => d.Source == "openrgb" && d.ProductId == "0x1715");
    }

    /// <summary>
    /// The 2023 Kraken and Kraken Elite have an LCD and no addressable LEDs at all, so
    /// their rows must not claim RGB - the modal's capability column is the only place a
    /// user learns what Nexus actually drives on a given cooler.
    /// </summary>
    [Fact]
    public void Catalog_MarksTheScreenOnlyKrakensWithoutRgb()
    {
        var all = LightingDevicesCatalog.All;

        foreach (var pid in new[] { "0x300C", "0x300E" })
        {
            var row = all.Single(d => d.VendorId == "0x1E71" && d.ProductId == pid);
            Assert.Equal(new[] { "screen" }, row.Capabilities);
        }

        var eliteV2 = all.Single(d => d.VendorId == "0x1E71" && d.ProductId == "0x3012");
        Assert.Equal(new[] { "rgb", "screen" }, eliteV2.Capabilities);

        var x3 = all.Single(d => d.VendorId == "0x1E71" && d.ProductId == "0x2007");
        Assert.Equal(new[] { "rgb" }, x3.Capabilities);
    }

    /// <summary>0x0C01/0x0C02 are the two Touch Infinite panel revisions; the case drives no LEDs.</summary>
    [Fact]
    public void Catalog_CoversTheY70PanelsAsScreenOnly()
    {
        foreach (var (pid, model) in new[]
                 {
                     ("0x0C00", "Y70 Touch"),
                     ("0x0C01", "Y70 Touch Infinite"),
                     ("0x0C02", "Y70 Touch Infinite"),
                 })
        {
            var row = LightingDevicesCatalog.All.Single(d => d.VendorId == "0x3402" && d.ProductId == pid);
            Assert.Equal("nexus", row.Source);
            Assert.Equal("HYTE", row.Vendor);
            Assert.Equal(model, row.Model);
            Assert.Equal("case", row.Category);
            Assert.Equal(new[] { "screen" }, row.Capabilities);
        }
    }

    /// <summary>
    /// Every CNVS PID the handler claims needs its own row: the Supported Devices dot
    /// matches on exact VID:PID, so a revision missing from the catalog shows as
    /// unsupported while the service is driving it (NEX-72, a 0B01 unit).
    /// </summary>
    [Fact]
    public void Catalog_CoversEveryCnvsPidTheHandlerClaims()
    {
        foreach (var productId in Nexus.Service.Peripherals.Hyte.Cnvs.CnvsProtocol.ProductIds)
        {
            var pid = "0x" + productId.ToString("X4");
            var row = LightingDevicesCatalog.All.Single(d => d.VendorId == "0x3402" && d.ProductId == pid);
            Assert.Equal("nexus", row.Source);
            Assert.Equal("HYTE", row.Vendor);
            Assert.Equal("CNVS", row.Model);
            Assert.Equal("mousemat", row.Category);
        }
    }

    /// <summary>The MiniHub ships on HYTE's shared 0x3402 VID but is iBUYPOWER's product.</summary>
    [Fact]
    public void Catalog_BrandsTheMiniHubAsIbuypower()
    {
        var row = LightingDevicesCatalog.All.Single(d => d.VendorId == "0x3402" && d.ProductId == "0x0900");

        Assert.Equal("iBUYPOWER", row.Vendor);
        Assert.Equal("MiniHub", row.Model);
    }

    [Fact]
    public void UsbVendorIds_CoverOpenRgbAndFirstPartyVendors()
    {
        var ids = LightingDevicesCatalog.UsbVendorIds;

        // Dozens of distinct vendors in the OpenRGB resource; a tiny set means
        // the embedded resource failed to parse and the hot-plug filter would
        // silently stop matching real RGB hardware.
        Assert.True(ids.Count >= 50, $"expected >= 50 vendor ids, got {ids.Count}");
        Assert.Contains(0x1B1C, ids); // Corsair
        Assert.Contains(0x0CF2, ids); // Lian Li
        Assert.Contains(0x3402, ids); // HYTE (first-party)
        Assert.Contains(0x391A, ids); // Tryx (first-party only)
        Assert.DoesNotContain(0x0781, ids); // SanDisk - storage, never RGB
    }

    [Theory]
    [InlineData("Cooler Master MM711", "Cooler Master", "MM711")]
    [InlineData("Turtle Beach Vulcan II TKL", "Turtle Beach", "Vulcan II TKL")]
    [InlineData("Red Square Keyrox TKL", "Red Square", "Keyrox TKL")]
    [InlineData("Lian Li O11 Dynamic - Razer Edition", "Lian Li", "O11 Dynamic - Razer Edition")]
    [InlineData("Corsair K70 RGB", "Corsair", "K70 RGB")]
    [InlineData("Cooler", "", "Cooler")]
    public void SplitVendorModel_KeepsMultiWordBrandsWhole(string name, string vendor, string model)
    {
        Assert.Equal((vendor, model), LightingDevicesCatalog.SplitVendorModel(name));
    }

    [Fact]
    public void Catalog_NamesCoolerMasterInFull()
    {
        var rows = LightingDevicesCatalog.All.Where(d => d.Source == "openrgb").ToList();

        Assert.Contains(rows, d => d.Vendor == "Cooler Master");
        Assert.DoesNotContain(rows, d => d.Vendor == "Cooler");
    }
}
