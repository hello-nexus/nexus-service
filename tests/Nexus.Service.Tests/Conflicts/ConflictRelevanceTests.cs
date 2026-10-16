using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Service.Conflicts;
using Nexus.Service.Lifecycle;
using Nexus.Service.Models.Conflicts;
using Nexus.Service.Peripherals;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Conflicts;

public class ConflictRelevanceTests
{
    private const string StreamDeck = "elgato-stream-deck";

    private static readonly SupportedUsbDevice[] Supported =
    {
        new(0x1038, 0x161A, "SteelSeries Apex 3"),
        new(0x1E71, 0x2021, "NZXT RGB Controller"),
        new(0x0FD9, 0x0080, "Elgato Stream Deck MK.2"),
        new(0x0B05, 0x19AF, "ASUS Aura LED Controller"),
    };

    private static readonly CuratedUsbMapping[] Curated =
    {
        new(StreamDeck, 0x0FD9, 0x0080, "Stream Deck"),
    };

    private static ConflictRelevanceResult Eval(
        ConnectedUsb[] usb, string? board = null, string[]? ram = null) =>
        ConflictRelevance.Evaluate(ConflictAppCatalog.All, usb, board, ram ?? Array.Empty<string>(), Supported, Curated);

    private static ConnectedUsb Usb(int vid, int pid, string name = "device") => new(vid, pid, name);

    [Fact]
    public void StreamDeckApp_WithOnlyAnElgatoCaptureCard_IsNotAConflict()
    {
        var result = Eval(new[] { Usb(0x0FD9, 0x005E, "Elgato HD60 X") });

        Assert.False(result.Qualifies(StreamDeck));
        Assert.Empty(result.Apps[StreamDeck].Devices);
    }

    [Fact]
    public void StreamDeckApp_WithAStreamDeck_IsAConflictByTheCuratedPid()
    {
        var result = Eval(new[] { Usb(0x0FD9, 0x0080, "Stream Deck MK.2") });

        Assert.True(result.Qualifies(StreamDeck));
        var device = Assert.Single(result.Apps[StreamDeck].Devices);
        Assert.Equal(("0x0FD9", "0x0080", ConflictRelevance.SourceCurated), (device.VendorId, device.ProductId, device.Source));
    }

    [Fact]
    public void SteelSeriesGg_WithAnApex3_IsAConflictWithThatDevice()
    {
        var result = Eval(new[] { Usb(0x1038, 0x161A, "SteelSeries Apex 3") });

        Assert.True(result.Qualifies("steelseries-gg"));
        Assert.Equal("SteelSeries Apex 3", Assert.Single(result.Apps["steelseries-gg"].Devices).Name);
        Assert.False(result.Qualifies("nzxt-cam"));
    }

    [Fact]
    public void AVendorIdAloneIsNotEnough_ThePidMustBeASupportedDevice()
    {
        var result = Eval(new[] { Usb(0x1038, 0x9999) });

        Assert.False(result.Qualifies("steelseries-gg"));
    }

    [Fact]
    public void NzxtCam_WithAnNzxtRgbController_IsAConflict()
    {
        var result = Eval(new[] { Usb(0x1E71, 0x2021, "NZXT RGB Controller") });

        Assert.True(result.Qualifies("nzxt-cam"));
    }

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "armoury-crate", true)]
    [InlineData("ASUSTeK COMPUTER INC.", "msi-mystic-light", false)]
    [InlineData("Micro-Star International Co., Ltd.", "msi-mystic-light", true)]
    [InlineData("Gigabyte Technology Co., Ltd.", "gigabyte-rgb-fusion", true)]
    [InlineData("ASRock", "asrock-polychrome", true)]
    [InlineData("Some Unknown Board Maker", "armoury-crate", false)]
    public void BoardVendorAttributesItsOwnAppsOnly(string board, string appId, bool expected)
    {
        var result = Eval(Array.Empty<ConnectedUsb>(), board);

        Assert.Equal(expected, result.Qualifies(appId));
        if (expected)
        {
            Assert.Equal(ConflictRelevance.SourceBoard, Assert.Single(result.Apps[appId].Devices).Source);
        }
    }

    [Fact]
    public void RamOnlyVendorApp_IsNotAConflict()
    {
        var result = Eval(Array.Empty<ConnectedUsb>(), null, new[] { "Corsair", "XPG" });

        Assert.False(result.Qualifies("icue"));
        Assert.False(result.Qualifies("xpg-prime"));
        Assert.Contains("RAM Corsair", result.Unattributed);
    }

    [Fact]
    public void Nexus2_IsAlwaysAConflict()
    {
        Assert.True(Eval(Array.Empty<ConnectedUsb>()).Qualifies("hyte-nexus-2"));
    }

    [Fact]
    public void UniversalRgbApp_NeedsADeviceNexusWouldDrive()
    {
        Assert.False(Eval(new[] { Usb(0x046D, 0xC52B, "Unifying receiver") }).Qualifies("signalrgb"));

        var result = Eval(new[] { Usb(0x1038, 0x161A, "SteelSeries Apex 3") });

        Assert.True(result.Qualifies("signalrgb"));
        Assert.Equal("SteelSeries Apex 3", Assert.Single(result.Apps["signalrgb"].Devices).Name);
    }

    [Fact]
    public void UniversalRgbApp_QualifiesOnARecognizedBoard()
    {
        Assert.True(Eval(Array.Empty<ConnectedUsb>(), "ASUSTeK COMPUTER INC.").Qualifies("openrgb"));
    }

    [Fact]
    public void ADeviceListedTwice_IsAttributedOnce()
    {
        var result = Eval(new[] { Usb(0x1038, 0x161A, "Apex 3"), Usb(0x1038, 0x161A, "Apex 3") });

        Assert.Single(result.Apps["steelseries-gg"].Devices);
    }

    [Theory]
    [InlineData(@"HID: \\?\HID#VID_1038&PID_161A&MI_01#7&abc", 0x1038, 0x161A)]
    [InlineData("hid#vid_1e71&pid_2021", 0x1E71, 0x2021)]
    public void HidPathIdsAreParsed(string location, int vid, int pid)
    {
        Assert.Equal((vid, pid), ConflictRelevance.ParseHidPath(location));
    }

    [Theory]
    [InlineData("")]
    [InlineData("I2C: /dev/i2c-3, address 0x70")]
    [InlineData(null)]
    public void ALocationWithoutIdsParsesToNull(string? location)
    {
        Assert.Null(ConflictRelevance.ParseHidPath(location));
    }

    [Fact]
    public void ACardWithAnIdIsOwnedByIdNotByVendorName()
    {
        var byId = ConflictDeviceOwnership.AppIdsForCard("SteelSeries", @"HID: \\?\HID#VID_1E71&PID_2021");

        Assert.Contains("nzxt-cam", byId);
        Assert.DoesNotContain("steelseries-gg", byId);
        Assert.Contains("signalrgb", byId);
    }

    [Fact]
    public void ACardWithoutAnIdFallsBackToTheVendorName()
    {
        Assert.Contains("steelseries-gg", ConflictDeviceOwnership.AppIdsForCard("SteelSeries", "I2C: bus 1"));
    }

    [Fact]
    public void EveryCatalogVendorIdIsInTheBundledSupportedDevices()
    {
        foreach (var def in ConflictAppCatalog.All)
        {
            foreach (var vid in def.VendorIds)
            {
                Assert.True(LightingDevicesCatalog.UsbVendorIds.Contains(vid), $"{def.Id}: 0x{vid:X4}");
            }
            foreach (var packed in def.UsbIds)
            {
                Assert.True(LightingDevicesCatalog.UsbVendorIds.Contains(packed >> 16), $"{def.Id}: 0x{packed:X8}");
            }
        }
    }

    [Fact]
    public void EveryBoardVendorNormalizesFromARealManufacturerString()
    {
        var known = new HashSet<string> { "ASUS", "MSI", "Gigabyte", "ASRock" };
        foreach (var def in ConflictAppCatalog.All.Where(d => d.BoardVendor.Length > 0))
        {
            Assert.Contains(def.BoardVendor, known);
        }
    }
}

public class ConflictWatcherRelevanceTests
{
    private static Dictionary<string, List<(int Pid, string? Path)>> Running(params string[] names) =>
        names.ToDictionary(n => n, n => new List<(int, string?)> { (100, null) }, StringComparer.OrdinalIgnoreCase);

    private static InMemoryConfigStore OnboardedStore()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.OnboardingCompleted = true;
            s.FeaturesOnboardingCompleted = true;
            s.LightingOnboardingCompleted = true;
            s.Ui.ConflictAutoKillExclusions = new List<string>();
        });
        return store;
    }

    private static ConflictDeviceInventory Inventory(params ConnectedUsb[] usb) => new(
        () => usb,
        () => new[] { new CuratedUsbMapping("elgato-stream-deck", 0x0FD9, 0x0080, "Stream Deck") },
        () => null)
    { TtlMs = 0 };

    private static ConflictWatcher Watcher(
        InMemoryConfigStore store, ConflictDeviceInventory inventory, params string[] processes) =>
        new(new MultiplexHub(), null, new FeatureGates(store), null, () => Running(processes), null, inventory) { HandlerCacheMs = 0 };

    [Fact]
    public void ARunningAppWithNoAttributedDevice_IsNotListed_AndIsStillRunning()
    {
        var watcher = Watcher(OnboardedStore(), Inventory(new ConnectedUsb(0x0FD9, 0x005E, "capture card")), "StreamDeck");

        Assert.Empty(watcher.GetConflicts());
        Assert.True(watcher.IsAppRunning("elgato-stream-deck"));
    }

    [Fact]
    public void ARunningAppWithAnAttributedDevice_IsListedWithIt()
    {
        var watcher = Watcher(OnboardedStore(), Inventory(new ConnectedUsb(0x0FD9, 0x0080, "Stream Deck MK.2")), "StreamDeck");

        var conflict = Assert.Single(watcher.GetConflicts());
        Assert.Equal("Stream Deck MK.2", Assert.Single(conflict.Devices).Name);
    }

    [Fact]
    public void TheStartupShutdown_SkipsAnAppWithNoAttributedDevice_AndEndsOneWithADevice()
    {
        var killed = new List<string>();
        var store = OnboardedStore();
        var without = Watcher(store, Inventory(new ConnectedUsb(0x0FD9, 0x005E, "capture card")), "StreamDeck", "SteelSeriesGG");
        var withDevice = Watcher(store, Inventory(new ConnectedUsb(0x1038, 0x161A, "Apex 3")), "StreamDeck", "SteelSeriesGG");

        // Apex 3 is only attributed when the PID is in the supported list, which the production inventory loads.
        new ConflictStartupShutdown(store, without, NullLogger<ConflictStartupShutdown>.Instance, def => killed.Add(def.Id), _ => false)
            .EndRunningApps();
        Assert.Empty(killed);

        new ConflictStartupShutdown(store, withDevice, NullLogger<ConflictStartupShutdown>.Instance, def => killed.Add(def.Id), _ => false)
            .EndRunningApps();
        Assert.Equal(new[] { "steelseries-gg" }, killed);
    }

    [Fact]
    public void TheLaunchNotifier_SkipsAnAppWithNoAttributedDevice()
    {
        var store = OnboardedStore();
        var processes = Running();
        var watcher = new ConflictWatcher(
            new MultiplexHub(), null, new FeatureGates(store), null, () => processes, null,
            Inventory(new ConnectedUsb(0x0FD9, 0x005E, "capture card"))) { HandlerCacheMs = 0 };
        var notifier = new ConflictLaunchNotifier(watcher, store);
        var launched = new List<string>();
        notifier.AppLaunched += a => launched.Add(a.Id);

        notifier.Tick(0);
        processes["StreamDeck"] = new List<(int, string?)> { (100, null) };
        processes["SteelSeriesGG"] = new List<(int, string?)> { (101, null) };
        notifier.Tick(6_000);

        Assert.Empty(launched);
    }

    [Fact]
    public void TheAttributionLine_NamesDevicesAndLeftAloneApps()
    {
        var result = ConflictRelevance.Evaluate(
            ConflictAppCatalog.All,
            new[] { new ConnectedUsb(0x1038, 0x161A, "Apex 3") },
            null, Array.Empty<string>(),
            new[] { new SupportedUsbDevice(0x1038, 0x161A, "Apex 3") },
            Array.Empty<CuratedUsbMapping>());
        var detected = new[]
        {
            new DetectedConflict { Id = "steelseries-gg" },
            new DetectedConflict { Id = "icue" },
            new DetectedConflict { Id = "hyte-nexus-2" },
        };

        var line = ConflictWatcher.AttributionLine(detected, result);

        Assert.Equal(
            "[conflicts] attribution: hyte-nexus-2 -> Apex 3 1038:161A | icue -> no attributed device, left alone | steelseries-gg -> Apex 3 1038:161A",
            line);
    }
}
