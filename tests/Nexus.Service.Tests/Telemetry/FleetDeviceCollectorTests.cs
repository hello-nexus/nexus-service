using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Telemetry;
using Xunit;

public class FleetDeviceCollectorTests
{
    private static UsbDeviceEntry Entry(int vid, int pid, string name = "", string manufacturer = "") =>
        new() { VendorId = vid, ProductId = pid, Name = name, Manufacturer = manufacturer };

    [Fact]
    public void Keeps_peripherals_and_drops_bus_plumbing()
    {
        var devices = FleetDeviceCollector.Collect(new[]
        {
            Entry(0x1532, 0x0226, "Huntsman Elite", "Razer"),
            Entry(0x1d6b, 0x0002, "USB Root Hub"),          // Linux Foundation root hub
            Entry(0x8087, 0x0024, "Integrated Rate Matching Hub"),
            Entry(0x05e3, 0x0610, "Generic USB Hub"),        // dropped by name, vendor is not listed
            Entry(0x046d, 0xc52b, "USB Receiver", "Logitech"),
        });

        Assert.Equal(
            new[] { (0x046d, 0xc52b), (0x1532, 0x0226) },
            devices.Select(d => (d.Vid, d.Pid)).ToArray());
    }

    [Theory]
    [InlineData("USB2.0 Hub", false)]
    [InlineData("USB3.1 Hub", false)]
    [InlineData("Generic USB Hub", false)]
    [InlineData("Uni Hub", true)]
    [InlineData("Smart Hub", true)]
    public void Generic_usb_hubs_are_dropped_by_word_but_branded_hubs_are_kept(string name, bool kept) =>
        Assert.Equal(kept ? 1 : 0, FleetDeviceCollector.Collect(new[] { Entry(0x1111, 0x2222, name) }).Count);

    [Fact]
    public void Orders_by_id_so_enumeration_order_is_not_a_hardware_change()
    {
        var forward = FleetDeviceCollector.Collect(new[] { Entry(1, 2), Entry(3, 4), Entry(1, 1) });
        var reversed = FleetDeviceCollector.Collect(new[] { Entry(1, 1), Entry(3, 4), Entry(1, 2) });

        Assert.Equal(
            forward.Select(d => (d.Vid, d.Pid)),
            reversed.Select(d => (d.Vid, d.Pid)));
        Assert.Equal(new[] { (1, 1), (1, 2), (3, 4) }, forward.Select(d => (d.Vid, d.Pid)).ToArray());
    }

    [Fact]
    public void Composite_interfaces_collapse_to_the_row_carrying_a_real_name()
    {
        var devices = FleetDeviceCollector.Collect(new[]
        {
            Entry(0x1532, 0x0226),                              // interface row, no product string
            Entry(0x1532, 0x0226, "Huntsman Elite", "Razer"),
        });

        var only = Assert.Single(devices);
        Assert.Equal("Huntsman Elite", only.Name);
        Assert.Equal("Razer", only.Manufacturer);
    }

    [Fact]
    public void Rejects_out_of_range_ids()
    {
        var devices = FleetDeviceCollector.Collect(new[]
        {
            Entry(0, 0x1234, "no vendor"),
            Entry(0x1234, 0, "no product"),
            Entry(0x1_0000, 0x1234, "vid overflow"),
            Entry(0x1532, 0x0226, "real"),
        });

        Assert.Single(devices);
    }

    [Fact]
    public void Caps_the_reported_set()
    {
        var many = Enumerable.Range(1, 100).Select(i => Entry(0x1532, i)).ToList();
        Assert.Equal(FleetDeviceCollector.MaxDevices, FleetDeviceCollector.Collect(many).Count);
    }

    [Fact]
    public void Truncates_an_overlong_label_to_the_wire_cap()
    {
        var devices = FleetDeviceCollector.Collect(new[] { Entry(1, 1, new string('x', 200)) });
        Assert.Equal(64, devices[0].Name.Length);
    }

    [Fact]
    public void Never_carries_a_serial()
    {
        var props = typeof(FleetEventDevice).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("Serial", props);
    }
}
