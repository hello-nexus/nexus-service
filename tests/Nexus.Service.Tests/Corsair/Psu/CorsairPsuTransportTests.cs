using System;
using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Corsair.Psu;
using Nexus.Service.Peripherals.Hid;
using Xunit;

namespace Nexus.Service.Tests.Corsair.Psu;

public class CorsairPsuTransportTests
{
    private sealed class RecordingEnumerator : IHidEnumerator
    {
        public int FindAllCalls;
        public int LoudOpens;
        public int QuietOpens;
        public List<(int Vid, int Pid)> FindCalls { get; } = new();
        public List<HidDeviceInfo> Devices { get; } = new();

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId)
        {
            FindCalls.Add((vendorId, productId));
            return Devices.FindAll(d => d.VendorId == vendorId && d.ProductId == productId);
        }

        public IReadOnlyList<HidDeviceInfo> FindAll() { FindAllCalls++; return Devices; }
        public IHidDevice? Open(string path, bool forInput = false) { LoudOpens++; return null; }
        public IHidDevice? OpenQuiet(string path, bool forInput = false) { QuietOpens++; return null; }
    }

    private sealed class FakeUsb : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Entries { get; } = new();
        public List<UsbDeviceEntry> Enumerate() => new(Entries);
    }

    private static UsbDeviceEntry Usb(int vid, int pid) => new() { VendorId = vid, ProductId = pid };

    [Fact]
    public void Find_without_a_psu_on_the_bus_touches_no_hid_interface()
    {
        var hid = new RecordingEnumerator();
        var usb = new FakeUsb();
        usb.Entries.Add(Usb(0x1B1C, 0x0A3E));

        var found = new HidCorsairPsuTransport(hid, new HardwarePresence(usb)).Find();

        Assert.Null(found);
        Assert.Empty(hid.FindCalls);
        Assert.Equal(0, hid.FindAllCalls);
    }

    [Fact]
    public void Find_with_a_psu_uses_vid_pid_lookups_and_never_enumerates_everything()
    {
        var hid = new RecordingEnumerator();
        hid.Devices.Add(new HidDeviceInfo { VendorId = 0x1B1C, ProductId = 0x0A3E, Path = "headset" });
        hid.Devices.Add(new HidDeviceInfo { VendorId = 0x1B1C, ProductId = 0x1C1F, Path = "psu", Serial = "S1" });
        var usb = new FakeUsb();
        usb.Entries.Add(Usb(0x1B1C, 0x1C1F));

        var found = new HidCorsairPsuTransport(hid, new HardwarePresence(usb)).Find();

        Assert.Equal(new CorsairPsuInfo(0x1C1F, "psu", "S1"), found);
        Assert.Equal(0, hid.FindAllCalls);
        Assert.Equal(new[] { (0x1B1C, 0x1C1F) }, hid.FindCalls);
    }

    [Fact]
    public void Find_with_a_psu_on_the_bus_but_no_hid_match_returns_null()
    {
        var hid = new RecordingEnumerator();
        var usb = new FakeUsb();
        usb.Entries.Add(Usb(0x1B1C, 0x1C1F));

        Assert.Null(new HidCorsairPsuTransport(hid, new HardwarePresence(usb)).Find());
        Assert.Equal(new[] { (0x1B1C, 0x1C1F) }, hid.FindCalls);
    }

    [Fact]
    public void Exchange_opens_without_the_per_open_log_line()
    {
        var hid = new RecordingEnumerator();

        var reply = new HidCorsairPsuTransport(hid, new HardwarePresence(new FakeUsb())).Exchange(new CorsairPsuInfo(0x1C1F, "psu", null), CorsairPsuProtocol.BuildReadMode());

        Assert.Null(reply);
        Assert.Equal(1, hid.QuietOpens);
        Assert.Equal(0, hid.LoudOpens);
    }
}
