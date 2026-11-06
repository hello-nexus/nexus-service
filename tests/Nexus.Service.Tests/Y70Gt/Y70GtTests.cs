using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices.Firmware;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals;
using Nexus.Service.Peripherals.Hyte.Np50;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Persistence;
using Nexus.Service.Tests;

namespace Nexus.Service.Tests.Y70Gt;

public class Y70GtTests
{
    [Fact]
    public void Pid_maps_to_the_gt_variant_and_has_no_ota_product_key()
    {
        Assert.Equal("y70-gt", Y70DisplayProtocol.VariantForProductId(0x0C06));
        Assert.Equal(-1, Y70DisplayProtocol.ProductIdForVariant(Y70DisplayProtocol.VariantGt));
        Assert.Null(DfuProductKey.IdentityForProductId(0x0C06));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(12, 0, 12)]
    [InlineData(13, 1, 12)]
    [InlineData(25, 1, 0)]
    [InlineData(26, 2, 0)]
    [InlineData(32, 2, 12)]
    [InlineData(33, 3, 12)]
    [InlineData(39, 3, 0)]
    [InlineData(40, 4, 0)]
    [InlineData(46, 4, 12)]
    public void Backlight_wire_order_is_column_major_serpentine_from_the_left_column(int index, int column, int row)
    {
        Assert.Equal((column, row), Y70DisplayProtocol.BacklightWireOrder[index]);
    }

    [Fact]
    public void Backlight_geometry_has_47_unique_cells_with_13_rows_in_left_columns_and_even_rows_in_the_rest()
    {
        var order = Y70DisplayProtocol.BacklightWireOrder;
        Assert.Equal(47, Y70DisplayProtocol.BacklightLedCount);
        Assert.Equal(47, order.Length);
        Assert.Equal(47, order.Distinct().Count());
        for (var col = 0; col < 5; col++)
        {
            var rows = order.Where(c => c.Column == col).Select(c => c.Row).OrderBy(r => r).ToArray();
            var expected = col < 2 ? Enumerable.Range(0, 13).ToArray() : new[] { 0, 2, 4, 6, 8, 10, 12 };
            Assert.Equal(expected, rows);
        }
    }

    [Fact]
    public void Backlight_stream_has_the_header_then_grb_triples()
    {
        var rgb = new byte[47 * 3];
        rgb[0] = 0x11; rgb[1] = 0x22; rgb[2] = 0x33;
        rgb[46 * 3] = 0xAA; rgb[46 * 3 + 1] = 0xBB; rgb[46 * 3 + 2] = 0xCC;

        var frame = Y70DisplayProtocol.BuildBacklightStream(rgb);

        Assert.Equal(7 + 141, frame.Length);
        Assert.Equal(new byte[] { 0xFF, 0xEE, 0x01, 0x01, 0x00, 0x00, 0x00 }, frame[..7]);
        Assert.Equal(new byte[] { 0x22, 0x11, 0x33 }, frame[7..10]);
        Assert.Equal(new byte[] { 0xBB, 0xAA, 0xCC }, frame[^3..]);
    }

    [Fact]
    public void Backlight_stream_leaves_missing_leds_dark()
    {
        var frame = Y70DisplayProtocol.BuildBacklightStream(new byte[] { 1, 2, 3 });

        Assert.Equal(7 + 141, frame.Length);
        Assert.All(frame[10..], b => Assert.Equal(0, b));
    }

    private static Y70DisplayHub ConnectedHub(string variant, out FakeTransport transport)
    {
        var t = new FakeTransport();
        transport = t;
        var hub = new Y70DisplayHub(new FakeDiscovery(variant), _ => t);
        Assert.True(hub.EnsureConnected());
        return hub;
    }

    [Fact]
    public void Hub_streams_the_backlight_only_for_the_gt_variant()
    {
        var gt = ConnectedHub(Y70DisplayProtocol.VariantGt, out var gtPort);
        Assert.True(gt.HasBacklight);
        Assert.True(gt.WriteBacklight(new byte[141]));
        Assert.Equal(148, Assert.Single(gtPort.Writes).Length);

        var truly = ConnectedHub(Y70DisplayProtocol.VariantTruly, out var trulyPort);
        Assert.False(truly.HasBacklight);
        Assert.False(truly.WriteBacklight(new byte[141]));
        Assert.Empty(trulyPort.Writes);
    }

    [Fact]
    public void Hub_binds_touch_and_gt_when_both_are_discovered()
    {
        var hub = BothHub(true, true, out var touch, out var gt);
        Assert.True(hub.EnsureConnected());
        Assert.True(hub.EnsureBacklightConnected());
        Assert.Equal(Y70DisplayProtocol.VariantTouch, hub.Variant);
        Assert.True(hub.IsConnected);
        Assert.True(hub.HasBacklight);
        Assert.Equal("y70:GTSER", hub.BacklightDeviceId);
        Assert.True(hub.WriteBacklight(new byte[141]));
        Assert.Single(gt.Writes);
        Assert.Empty(touch.Writes);
    }

    [Fact]
    public void Hub_binds_gt_only_as_primary()
    {
        var hub = BothHub(false, true, out _, out var gt);
        Assert.True(hub.EnsureConnected());
        Assert.True(hub.EnsureBacklightConnected());
        Assert.Equal(Y70DisplayProtocol.VariantGt, hub.Variant);
        Assert.Equal(hub.DeviceId, hub.BacklightDeviceId);
        Assert.True(hub.WriteBacklight(new byte[141]));
        Assert.Single(gt.Writes);
    }

    [Fact]
    public void Hub_binds_touch_only_without_backlight()
    {
        var hub = BothHub(true, false, out var touch, out _);
        Assert.True(hub.EnsureConnected());
        Assert.False(hub.EnsureBacklightConnected());
        Assert.True(hub.IsConnected);
        Assert.False(hub.HasBacklight);
        Assert.Equal("", hub.BacklightDeviceId);
        Assert.False(hub.WriteBacklight(new byte[141]));
        Assert.Empty(touch.Writes);
    }

    [Fact]
    public void Ensure_connected_skips_the_gt_port_already_held_beside_the_primary()
    {
        var opens = new List<string>();
        var ports = new List<Y70DisplayPort>
        {
            new() { PortName = "COM3", Serial = "TOUCH", Variant = Y70DisplayProtocol.VariantTouch },
            new() { PortName = "COM7", Serial = "GTSER", Variant = Y70DisplayProtocol.VariantGt },
        };
        var primary = new FakeTransport();
        var hub = new Y70DisplayHub(new ListDiscovery(ports), p => { opens.Add(p.PortName); return p.PortName == "COM3" ? primary : new FakeTransport(); });
        Assert.True(hub.EnsureConnected());
        Assert.True(hub.EnsureBacklightConnected());
        Assert.Equal(new[] { "COM3", "COM7" }, opens);

        // Primary drops on its own; reconnect must not reopen the held GT port.
        // Enumeration order can change between scans: the held GT port now comes first.
        primary.Open = false;
        ports.Reverse();
        Assert.True(hub.EnsureConnected());
        Assert.Equal(new[] { "COM3", "COM7", "COM3" }, opens);
        Assert.Equal(Y70DisplayProtocol.VariantTouch, hub.Variant);
        Assert.True(hub.HasBacklight);
    }

    private static Y70DisplayHub BothHub(bool touchPresent, bool gtPresent, out FakeTransport touch, out FakeTransport gt)
    {
        var tt = new FakeTransport();
        var gg = new FakeTransport();
        touch = tt;
        gt = gg;
        var ports = new List<Y70DisplayPort>();
        if (touchPresent) ports.Add(new Y70DisplayPort { PortName = "COM3", Serial = "TOUCH", Variant = Y70DisplayProtocol.VariantTouch });
        if (gtPresent) ports.Add(new Y70DisplayPort { PortName = "COM7", Serial = "GTSER", Variant = Y70DisplayProtocol.VariantGt });
        return new Y70DisplayHub(new ListDiscovery(ports), p => p.PortName == "COM7" ? gg : tt);
    }

    private sealed class ListDiscovery : IY70DisplayPortDiscovery
    {
        private readonly IReadOnlyList<Y70DisplayPort> _ports;
        public ListDiscovery(IReadOnlyList<Y70DisplayPort> ports) => _ports = ports;
        public IReadOnlyList<Y70DisplayPort> Discover() => _ports;
    }

    [Fact]
    public void Handler_names_the_gt_and_claims_its_pid()
    {
        var hub = ConnectedHub(Y70DisplayProtocol.VariantGt, out _);
        var handler = new Nexus.Service.Devices.Handlers.Y70Handler(hub, TestHandlers.FakeTopology());

        Assert.Equal("HYTE Y70 GT", handler.Name);
        Assert.Equal("y70-gt", handler.FirmwareType);
        Assert.Contains(handler.Identifiers, id => id.VendorId == 0x3402 && id.ProductId == 0x0C06);
    }

    [Fact]
    public void Supported_devices_listing_hides_the_pre_release_gt_but_the_catalog_keeps_it()
    {
        Assert.DoesNotContain(AllSupportedDevices.Listed, d => d.VendorId == "0x3402" && d.ProductId == "0x0C06");
        Assert.DoesNotContain(AllSupportedDevices.Listed, d => d.Model == "Y70 GT");
        var row = Assert.Single(AllSupportedDevices.All, d => d.VendorId == "0x3402" && d.ProductId == "0x0C06");
        Assert.Equal("case", row.Category);
        Assert.True(row.Hidden);
        Assert.Contains(AllSupportedDevices.Listed, d => d.Model == "Y70 Touch");
    }

    [Fact]
    public void Unclaimed_gt_usb_entry_produces_no_stray_inventory_row()
    {
        var hub = ConnectedHub(Y70DisplayProtocol.VariantGt, out _);
        var handler = new Nexus.Service.Devices.Handlers.Y70Handler(hub, TestHandlers.FakeTopology());
        var usb = new[] { new Nexus.Service.Devices.UsbDeviceEntry { VendorId = 0x3402, ProductId = 0x0C06 } };

        var rows = Nexus.Service.Devices.DeviceInventoryMapper.FromUsb(
            usb, new Nexus.Service.Devices.IDeviceHandler[] { handler }, new HashSet<string>(), new HashSet<(int, int)>(), AllSupportedDevices.All);

        Assert.Empty(rows);
    }

    [Fact]
    public void Card_is_a_47_led_matrix_named_for_the_backlight()
    {
        var hub = ConnectedHub(Y70DisplayProtocol.VariantGt, out _);
        var provider = new Y70GtLightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());

        var card = Assert.Single(provider.GetAll().Devices);

        Assert.Equal("Y70 GT Backlight", card.Name);
        Assert.Equal(hub.DeviceId, card.Id);
        Assert.Equal(47, card.LedCount);
        Assert.Equal("matrix", card.ZoneType);
        Assert.False(card.ZoneResizable);
        Assert.True(Y70GtLightingDeviceProvider.IsY70GtId(card.Id));
    }

    [Fact]
    public void Frame_uvs_follow_the_wire_order_on_the_grid()
    {
        var hub = ConnectedHub(Y70DisplayProtocol.VariantGt, out _);
        var provider = new Y70GtLightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());

        var frame = Assert.Single(provider.BuildFrames(3));

        Assert.Equal(3, frame.Index);
        Assert.Equal(47, frame.LedCount);
        Assert.NotNull(frame.LedU);
        Assert.NotNull(frame.LedV);
        // Index 0 is column 0, row 0; index 13 is column 1, row 12.
        Assert.Equal(0.5f / 5f, frame.LedU![0], 4);
        Assert.Equal(0.5f / 13f, frame.LedV![0], 4);
        Assert.Equal(1.5f / 5f, frame.LedU[13], 4);
        Assert.Equal(12.5f / 13f, frame.LedV[13], 4);
    }

    [Fact]
    public void Other_variants_expose_no_backlight_card_frames_or_structure()
    {
        var hub = ConnectedHub(Y70DisplayProtocol.VariantTruly, out _);
        var provider = new Y70GtLightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());

        Assert.False(provider.IsConnected);
        Assert.Empty(provider.GetAll().Devices);
        Assert.Empty(provider.BuildFrames(0));
        Assert.Empty(provider.GetStructures());
    }

    [Fact]
    public void Structure_is_one_matrix_segment_with_per_led_uvs()
    {
        var hub = ConnectedHub(Y70DisplayProtocol.VariantGt, out _);
        var provider = new Y70GtLightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());

        var structure = Assert.Single(provider.GetStructures());
        var segment = Assert.Single(structure.Segments);

        Assert.Equal(hub.DeviceId, structure.DeviceId);
        Assert.Equal(47, segment.LedCount);
        Assert.Equal("matrix", segment.ZoneType);
        Assert.Equal(47, segment.DefaultU!.Length);
        Assert.Equal(47, segment.DefaultV!.Length);
    }

    private sealed class FakeDiscovery : IY70DisplayPortDiscovery
    {
        private readonly string _variant;
        public FakeDiscovery(string variant) => _variant = variant;
        public IReadOnlyList<Y70DisplayPort> Discover() => new[]
        {
            new Y70DisplayPort { PortName = "COM_TEST", Serial = "GTTEST1", Variant = _variant },
        };
    }

    private sealed class FakeTransport : INp50Transport
    {
        public readonly List<byte[]> Writes = new();
        public bool Open = true;
        public bool IsOpen => Open;
        public string Serial => "GTTEST1";
        public void Write(ReadOnlySpan<byte> data) => Writes.Add(data.ToArray());
        public void DiscardInput() { }
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }
}
