using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Xunit;

namespace Nexus.Service.Tests;

public class UsbEnumeratorTests
{
    // ---- Windows entry builder (CfgMgr32 enumeration) ----

    private static (List<UsbDeviceEntry> Result, HashSet<string> Seen) NewSink()
        => (new List<UsbDeviceEntry>(), new HashSet<string>(System.StringComparer.OrdinalIgnoreCase));

    private static void Append(
        List<UsbDeviceEntry> result, HashSet<string> seen, string instanceId,
        string busReported = "", string deviceDesc = "", string manufacturer = "",
        string className = "", string driverName = "", string locationInfo = "")
        => UsbDeviceEntryBuilder.Append(result, seen, instanceId, busReported,
            deviceDesc, manufacturer, className, driverName, locationInfo);

    [Fact]
    public void Windows_Prefers_BusReportedDeviceDesc_Over_DeviceDescription()
    {
        // The USB iProduct string (BusReportedDeviceDesc) is the brand-friendly
        // name; DeviceDesc for HID interface nodes is the generic "USB Input
        // Device".
        var (result, seen) = NewSink();
        Append(result, seen, @"USB\VID_1B1C&PID_1B2E&MI_00\9&1357d11b&1&0000",
            busReported: "Corsair Gaming M65 Pro RGB Mouse",
            deviceDesc: "USB Input Device",
            manufacturer: "(Standard system devices)",
            className: "HIDClass",
            driverName: "input.inf",
            locationInfo: "000e.0000.0000.016.000.000.000.000.000");

        var entry = Assert.Single(result);
        Assert.Equal(0x1B1C, entry.VendorId);
        Assert.Equal(0x1B2E, entry.ProductId);
        Assert.Equal("Corsair Gaming M65 Pro RGB Mouse", entry.Name);
        Assert.Equal("(Standard system devices)", entry.Manufacturer);
        Assert.Equal("HIDClass", entry.Class);
        Assert.Equal("input.inf", entry.Driver);
        Assert.Equal("000e.0000.0000.016.000.000.000.000.000", entry.Location);
        Assert.Equal("9&1357d11b&1&0000", entry.Serial);
        Assert.Equal(@"USB\VID_1B1C&PID_1B2E&MI_00", entry.HardwareId);
    }

    [Fact]
    public void Windows_Falls_Back_To_DeviceDescription_When_BusReported_Missing()
    {
        var (result, seen) = NewSink();
        Append(result, seen, @"USB\VID_046D&PID_C548\9&abcdef&0&0",
            deviceDesc: "Logitech USB Receiver",
            manufacturer: "Logitech",
            locationInfo: "Port_#0003.Hub_#0001");

        var entry = Assert.Single(result);
        Assert.Equal("Logitech USB Receiver", entry.Name);
        Assert.Equal("Port_#0003.Hub_#0001", entry.Location);
    }

    [Fact]
    public void Windows_Dedupes_Composite_Interfaces_By_BusReportedDeviceDesc()
    {
        // All three nodes of a composite device share BusReportedDeviceDesc;
        // dedupe by (VID, PID, Name) collapses them to one row.
        var (result, seen) = NewSink();
        Append(result, seen, @"USB\VID_1B1C&PID_1B2E\SERIAL", busReported: "Corsair Gaming M65 Pro RGB Mouse");
        Append(result, seen, @"USB\VID_1B1C&PID_1B2E&MI_00\9&1&0", busReported: "Corsair Gaming M65 Pro RGB Mouse");
        Append(result, seen, @"USB\VID_1B1C&PID_1B2E&MI_01\9&1&1", busReported: "Corsair Gaming M65 Pro RGB Mouse");

        var entry = Assert.Single(result);
        Assert.Equal("Corsair Gaming M65 Pro RGB Mouse", entry.Name);
    }

    [Fact]
    public void Windows_Skips_NonUsb_And_RootHub_Entries()
    {
        var (result, seen) = NewSink();
        Append(result, seen, @"USB\ROOT_HUB30\5&18297c0c&0&0", deviceDesc: "USB Root Hub (USB 3.0)");
        Append(result, seen, @"PCI\VEN_1022&DEV_14E3", deviceDesc: "PCI Device");
        Append(result, seen, @"USB\VID_1B1C&PID_1B2E\9&xyz&1&0", deviceDesc: "Real Device", manufacturer: "Corsair");

        var entry = Assert.Single(result);
        Assert.Equal("Real Device", entry.Name);
        Assert.Equal("Corsair", entry.Manufacturer);
    }

    [Fact]
    public void Windows_Skips_Instances_Without_VidPid()
    {
        var (result, seen) = NewSink();
        Append(result, seen, @"USB\UNKNOWN_DEVICE\123", deviceDesc: "No ids");
        Assert.Empty(result);
    }

    [Fact]
    public void Windows_ParseInstanceId_Extracts_VidPid_And_Serial()
    {
        var (vid, pid, deviceKey, serial) = UsbDeviceEntryBuilder.ParseInstanceId(@"USB\VID_3402&PID_0C01\A1B2C3");
        Assert.Equal(0x3402, vid);
        Assert.Equal(0x0C01, pid);
        Assert.Equal("VID_3402&PID_0C01", deviceKey);
        Assert.Equal("A1B2C3", serial);
    }

    // ---- Mac JSON parser ----

    [Fact]
    public void Mac_Extracts_Manufacturer_From_Vendor_Parens()
    {
        var json = """
        {
          "SPUSBDataType": [
            {
              "_items": [
                {
                  "_name": "CNVS Controller",
                  "vendor_id": "0x3402 (HYTE)",
                  "product_id": "0x0BFF",
                  "serial_num": "A1B2C3",
                  "location_id": "0x14100000 / 1",
                  "speed": "full_speed"
                }
              ]
            }
          ]
        }
        """;

        var entries = MacUsbEnumerator.ParseJson(json);

        var e = Assert.Single(entries);
        Assert.Equal(0x3402, e.VendorId);
        Assert.Equal(0x0BFF, e.ProductId);
        Assert.Equal("CNVS Controller", e.Name);
        Assert.Equal("HYTE", e.Manufacturer);
        Assert.Equal("A1B2C3", e.Serial);
        Assert.Equal("0x14100000 / 1", e.Location);
        Assert.Equal("full_speed", e.Speed);
        Assert.Equal("USB\\VID_3402&PID_0BFF", e.HardwareId);
    }

    [Fact]
    public void Mac_Recurses_Into_Nested_Items()
    {
        // USB trees are hubs-containing-devices. Nested _items must be visited.
        var json = """
        {
          "SPUSBDataType": [
            {
              "_items": [
                {
                  "_name": "Hub",
                  "_items": [
                    {
                      "_name": "Keyboard",
                      "vendor_id": "0x05AC",
                      "product_id": "0x024F"
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

        var entries = MacUsbEnumerator.ParseJson(json);

        var e = Assert.Single(entries);
        Assert.Equal(0x05AC, e.VendorId);
        Assert.Equal(0x024F, e.ProductId);
        Assert.Equal("Keyboard", e.Name);
    }

    // ---- Linux sysfs reader ----

    [NonWindowsFact]
    public void Linux_Reads_Full_Device_Tree()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nexus-usb-test-{Path.GetRandomFileName()}");
        try
        {
            var deviceDir = Path.Combine(tempRoot, "1-2");
            Directory.CreateDirectory(deviceDir);
            File.WriteAllText(Path.Combine(deviceDir, "idVendor"), "3402\n");
            File.WriteAllText(Path.Combine(deviceDir, "idProduct"), "0bff\n");
            File.WriteAllText(Path.Combine(deviceDir, "product"), "CNVS Controller\n");
            File.WriteAllText(Path.Combine(deviceDir, "manufacturer"), "HYTE\n");
            File.WriteAllText(Path.Combine(deviceDir, "serial"), "A1B2C3\n");
            File.WriteAllText(Path.Combine(deviceDir, "busnum"), "1\n");
            File.WriteAllText(Path.Combine(deviceDir, "devnum"), "4\n");
            File.WriteAllText(Path.Combine(deviceDir, "speed"), "480\n");
            File.WriteAllText(Path.Combine(deviceDir, "bDeviceClass"), "03\n");

            // Root hub entry that should be skipped.
            var rootHub = Path.Combine(tempRoot, "usb1");
            Directory.CreateDirectory(rootHub);
            File.WriteAllText(Path.Combine(rootHub, "idVendor"), "1d6b");
            File.WriteAllText(Path.Combine(rootHub, "idProduct"), "0002");

            // Interface entry that should be skipped.
            var ifaceDir = Path.Combine(tempRoot, "1-2:1.0");
            Directory.CreateDirectory(ifaceDir);

            var entries = LinuxUsbEnumerator.EnumerateFrom(tempRoot);

            var e = Assert.Single(entries);
            Assert.Equal(0x3402, e.VendorId);
            Assert.Equal(0x0BFF, e.ProductId);
            Assert.Equal("CNVS Controller", e.Name);
            Assert.Equal("HYTE", e.Manufacturer);
            Assert.Equal("A1B2C3", e.Serial);
            Assert.Equal("Bus 001 Device 004", e.Location);
            Assert.Equal("High", e.Speed);
            Assert.Equal("HID", e.Class);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Linux_Missing_Sysfs_Root_Returns_Empty()
    {
        var entries = LinuxUsbEnumerator.EnumerateFrom("/tmp/definitely-not-a-sysfs-dir-xyz123");
        Assert.Empty(entries);
    }

    [Fact]
    public void Linux_Maps_All_Speed_Buckets()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"nexus-usb-speed-{Path.GetRandomFileName()}");
        try
        {
            string[][] cases =
            {
                new[] { "1.5", "Low" },
                new[] { "12", "Full" },
                new[] { "480", "High" },
                new[] { "5000", "Super" },
                new[] { "10000", "SuperPlus" },
            };

            for (var i = 0; i < cases.Length; i++)
            {
                var dir = Path.Combine(tempRoot, $"1-{i}");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "idVendor"), "1234");
                File.WriteAllText(Path.Combine(dir, "idProduct"), $"{i + 1:X4}");
                File.WriteAllText(Path.Combine(dir, "speed"), cases[i][0]);
            }

            var entries = LinuxUsbEnumerator.EnumerateFrom(tempRoot);
            var bySpeed = entries.Select(e => e.Speed).ToHashSet();
            foreach (var c in cases)
            {
                Assert.Contains(c[1], bySpeed);
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    /// <summary>macOS 26's system_profiler prints an empty USB tree; the IOKit path answers instead.</summary>
    [MacOnlyFact]
    public void Mac_IoKit_EnumeratesWithIds()
    {
        var devices = new MacUsbEnumerator().Enumerate();

        // A Mac with nothing on its USB ports may list no IOUSBHostDevice at all; every listed entry carries ids.
        Assert.All(devices, d => Assert.True(d.VendorId > 0 && d.ProductId > 0, d.HardwareId));
    }
}
