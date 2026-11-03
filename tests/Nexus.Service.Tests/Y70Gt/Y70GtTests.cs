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
    [InlineData(@"MONITOR\RTK2234\{4d36e96e}\0001", "y70-gt")]
    [InlineData(@"MONITOR\rtk2234\{4d36e96e}\0001", "y70-gt")]
    [InlineData(@"MONITOR\RTK1234\{4d36e96e}\0001", "")]
    [InlineData("", "")]
    public void Edid_fragment_maps_to_the_gt_variant(string hardwareId, string expected)
    {
        Assert.Equal(expected, Y70DisplayProtocol.GtVariantForHardwareId(hardwareId));
    }

    [Fact]
    public void Gt_edid_is_a_y70_display()
    {
        Assert.Contains("RTK2234", Y70DisplayProtocol.DdcPanelHardwareNames);
    }

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(6, 4, 6)]
    [InlineData(7, 3, 6)]
    [InlineData(13, 3, 0)]
    [InlineData(14, 2, 0)]
    [InlineData(21, 1, 6)]
    [InlineData(28, 0, 0)]
    [InlineData(34, 0, 6)]
    public void Backlight_wire_order_is_serpentine_from_the_right_column(int index, int column, int row)
    {
        Assert.Equal((column, row), Y70DisplayProtocol.BacklightWireOrder[index]);
    }

    [Fact]
    public void Backlight_wire_order_visits_every_cell_of_the_5x7_grid_once()
    {
        var order = Y70DisplayProtocol.BacklightWireOrder;
        Assert.Equal(35, order.Length);
        Assert.Equal(35, order.Distinct().Count());
        Assert.All(order, c =>
        {
            Assert.InRange(c.Column, 0, 4);
            Assert.InRange(c.Row, 0, 6);
        });
    }

    [Fact]
    public void Backlight_stream_has_the_header_then_grb_triples()
    {
        var rgb = new byte[35 * 3];
        rgb[0] = 0x11; rgb[1] = 0x22; rgb[2] = 0x33;
        rgb[34 * 3] = 0xAA; rgb[34 * 3 + 1] = 0xBB; rgb[34 * 3 + 2] = 0xCC;

        var frame = Y70DisplayProtocol.BuildBacklightStream(rgb);

        Assert.Equal(7 + 105, frame.Length);
        Assert.Equal(new byte[] { 0xFF, 0xEE, 0x01, 0x01, 0x00, 0x00, 0x00 }, frame[..7]);
        Assert.Equal(new byte[] { 0x22, 0x11, 0x33 }, frame[7..10]);
        Assert.Equal(new byte[] { 0xBB, 0xAA, 0xCC }, frame[^3..]);
    }

    [Fact]
    public void Backlight_stream_leaves_missing_leds_dark()
    {
        var frame = Y70DisplayProtocol.BuildBacklightStream(new byte[] { 1, 2, 3 });

        Assert.Equal(7 + 105, frame.Length);
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
        Assert.True(gt.WriteBacklight(new byte[105]));
        Assert.Equal(112, Assert.Single(gtPort.Writes).Length);

        var truly = ConnectedHub(Y70DisplayProtocol.VariantTruly, out var trulyPort);
        Assert.False(truly.HasBacklight);
        Assert.False(truly.WriteBacklight(new byte[105]));
        Assert.Empty(trulyPort.Writes);
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
    public void Supported_devices_lists_the_gt_with_screen_and_rgb()
    {
        var row = LightingDevicesCatalog.All.Single(d => d.VendorId == "0x3402" && d.ProductId == "0x0C06");
        Assert.Equal("Y70 GT", row.Model);
        Assert.Equal("case", row.Category);
        Assert.Equal(new[] { "rgb", "screen" }, row.Capabilities.OrderBy(c => c, StringComparer.Ordinal));
    }

    [Fact]
    public void Card_is_a_35_led_matrix_named_for_the_backlight()
    {
        var hub = ConnectedHub(Y70DisplayProtocol.VariantGt, out _);
        var provider = new Y70GtLightingDeviceProvider(hub, new InMemoryConfigStore(), new Np50IdentifyTracker());

        var card = Assert.Single(provider.GetAll().Devices);

        Assert.Equal("Y70 GT Backlight", card.Name);
        Assert.Equal(hub.DeviceId, card.Id);
        Assert.Equal(35, card.LedCount);
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
        Assert.Equal(35, frame.LedCount);
        Assert.NotNull(frame.LedU);
        Assert.NotNull(frame.LedV);
        // Index 0 is column 4, row 0; index 7 is column 3, row 6.
        Assert.Equal(4.5f / 5f, frame.LedU![0], 4);
        Assert.Equal(0.5f / 7f, frame.LedV![0], 4);
        Assert.Equal(3.5f / 5f, frame.LedU[7], 4);
        Assert.Equal(6.5f / 7f, frame.LedV[7], 4);
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
        Assert.Equal(35, segment.LedCount);
        Assert.Equal("matrix", segment.ZoneType);
        Assert.Equal(35, segment.DefaultU!.Length);
        Assert.Equal(35, segment.DefaultV!.Length);
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
        public bool IsOpen => true;
        public string Serial => "GTTEST1";
        public void Write(ReadOnlySpan<byte> data) => Writes.Add(data.ToArray());
        public void DiscardInput() { }
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }
}
