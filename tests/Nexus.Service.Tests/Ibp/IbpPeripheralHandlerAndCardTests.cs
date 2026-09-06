using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Ibp;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Ibp;

/// <summary>
/// The NEX-81 regression bar: an iBUYPOWER keyboard must show up as an
/// iBUYPOWER keyboard, not as the Keeb, plus the card / structure the
/// lighting page gets for each unit and the supported-devices catalog rows.
/// </summary>
public class IbpPeripheralHandlerAndCardTests
{
    private static UsbDeviceEntry Entry(int vid, int pid) => new() { VendorId = vid, ProductId = pid };

    private static IbpPeripheralHub EmptyHub() => new(new StubHidEnumerator());

    [Theory]
    [InlineData(0x0301)]
    [InlineData(0x0302)]
    [InlineData(0x0303)]
    [InlineData(0x0304)]
    [InlineData(0x0305)]
    public void Keeb_handler_no_longer_claims_the_ibuypower_keyboards(int pid)
    {
        var keeb = new KeebHandler();
        var ibp = new IbpKeyboardHandler(EmptyHub());
        var bus = new List<UsbDeviceEntry> { Entry(0x3402, pid) };
        Assert.False(keeb.IsConnected(bus));
        Assert.True(ibp.IsConnected(bus));
        Assert.False(new IbpMouseHandler(EmptyHub()).IsConnected(bus));
    }

    [Fact]
    public void Keeb_handler_still_owns_the_tkl()
    {
        var bus = new List<UsbDeviceEntry> { Entry(0x3402, 0x0300) };
        Assert.True(new KeebHandler().IsConnected(bus));
        Assert.False(new IbpKeyboardHandler(EmptyHub()).IsConnected(bus));
        Assert.Single(new KeebHandler().Identifiers);
    }

    [Theory]
    [InlineData(0x0200)]
    [InlineData(0x0201)]
    public void Mouse_handler_claims_the_ibuypower_mice(int pid)
    {
        var bus = new List<UsbDeviceEntry> { Entry(0x3402, pid) };
        Assert.True(new IbpMouseHandler(EmptyHub()).IsConnected(bus));
        Assert.False(new IbpKeyboardHandler(EmptyHub()).IsConnected(bus));
        Assert.False(new KeebHandler().IsConnected(bus));
    }

    [Fact]
    public void Handlers_are_first_party_gated_and_pageless()
    {
        IDeviceHandler kb = new IbpKeyboardHandler(EmptyHub());
        IDeviceHandler mouse = new IbpMouseHandler(EmptyHub());
        Assert.Equal("ibp-keyboard", kb.Id);
        Assert.Equal("ibp-mouse", mouse.Id);
        Assert.Equal("keyboard", kb.Category);
        Assert.Equal("mouse", mouse.Category);
        Assert.False(kb.HasPage);
        Assert.False(mouse.HasPage);
        Assert.True(kb.SupportsNexusControl);
        Assert.True(mouse.SupportsNexusControl);
        Assert.False(DeviceControlPolicy.IsExperimental(kb.Id));
        Assert.False(DeviceControlPolicy.IsExperimental(mouse.Id));
        Assert.True(DeviceControlPolicy.DefaultOn(kb.Id));
        Assert.True(DeviceControlPolicy.DefaultOn(mouse.Id));
        Assert.Null(DeviceControlPolicy.ConflictAppFor(kb.Id));
        Assert.Equal(5, kb.Identifiers.Count);
        Assert.Equal(2, mouse.Identifiers.Count);
        Assert.Empty(kb.Identifiers.Select(i => i.ProductId).Intersect(mouse.Identifiers.Select(i => i.ProductId)));
    }

    [Fact]
    public void Handler_name_follows_the_attached_model()
    {
        var hub = EmptyHub();
        var kb = new IbpKeyboardHandler(hub);
        var mouse = new IbpMouseHandler(hub);
        Assert.Equal("iBUYPOWER Keyboard", kb.Name);
        Assert.Equal("iBUYPOWER Mouse", mouse.Name);

        var hid = new OneUnitEnumerator(IbpPeripheralProtocol.Mk9ProKeyboard);
        using var live = new IbpPeripheralHub(hid);
        live.Reconcile(new HashSet<IbpPeripheralModel>(IbpPeripheralProtocol.Models));
        Assert.Equal("iBUYPOWER MK9 Pro Keyboard", new IbpKeyboardHandler(live).Name);
        Assert.Equal("iBUYPOWER Mouse", new IbpMouseHandler(live).Name);
    }

    [Fact]
    public void Default_partition_emits_one_card_per_unit()
    {
        var unit = new IbpAttachedPeripheral(IbpPeripheralProtocol.Km7Keyboard, "KB1", "path");
        var cards = IbpPeripheralLightingDeviceProvider.BuildCards(unit, new NexusSettings());
        var card = Assert.Single(cards);
        Assert.Equal("ibp:km7-keyboard:KB1:leds", card.Id);
        Assert.Equal("iBUYPOWER Chimera KM7 Keyboard", card.Name);
        Assert.Equal("ledstrip", card.Type);
        Assert.Equal("keyboard", card.IconType);
        Assert.Equal(24, card.LedCount);
        Assert.Equal(24, card.EnabledLedCount);
        Assert.Equal("usb:3402:0301", card.DeviceKey);
        Assert.True(card.LedsOn);
        Assert.Equal(100, card.Brightness);
        Assert.Equal("ibp:km7-keyboard:KB1", card.ParentDeviceId);
        Assert.Equal("ibp:km7-keyboard:KB1", card.DeviceId);
        Assert.Equal(0, card.ZoneIndex);
        Assert.Equal("linear", card.ZoneType);
        Assert.False(card.ZoneResizable);
        Assert.True(card.ZoneCustomizable);

        var mouse = new IbpAttachedPeripheral(IbpPeripheralProtocol.Km10Mouse, "MS1", "path");
        var mc = Assert.Single(IbpPeripheralLightingDeviceProvider.BuildCards(mouse, new NexusSettings()));
        Assert.Equal("mouse", mc.IconType);
        Assert.Equal(3, mc.LedCount);
        Assert.Equal("usb:3402:0201", mc.DeviceKey);
    }

    [Fact]
    public void Card_honours_persisted_prefs_and_power()
    {
        var unit = new IbpAttachedPeripheral(IbpPeripheralProtocol.Mk9Keyboard, "MK1", "path");
        var settings = new NexusSettings();
        settings.Devices.DisabledLightingDevices.Add("ibp:mk9-keyboard:MK1:leds");
        settings.Devices.LightingDevicePrefs["ibp:mk9-keyboard:MK1:leds"] = new LightingDevicePreference { Brightness = 42 };
        var card = Assert.Single(IbpPeripheralLightingDeviceProvider.BuildCards(unit, settings));
        Assert.False(card.LedsOn);
        Assert.Equal(42, card.Brightness);
        Assert.Equal(103, card.LedCount);
    }

    [Fact]
    public void Structure_carries_the_stock_led_positions()
    {
        var unit = new IbpAttachedPeripheral(IbpPeripheralProtocol.Mek4Keyboard, "MEK1", "path");
        var s = IbpPeripheralLightingDeviceProvider.BuildStructure(unit);
        Assert.Equal("ibp:mek4-keyboard:MEK1", s.DeviceId);
        Assert.Equal("usb:3402:0302", s.DeviceKey);
        Assert.True(s.Partitionable);
        var seg = Assert.Single(s.Segments);
        Assert.Equal(126, seg.LedCount);
        Assert.Equal(126, seg.FrameLedCount);
        Assert.False(seg.Resizable);
        Assert.Equal(126, seg.DefaultU!.Length);
        Assert.Equal(126, seg.DefaultV!.Length);
        Assert.Equal(0f, seg.DefaultU[0]);
        Assert.Equal(1f, seg.DefaultU[125]);
        var zone = Assert.Single(s.DefaultZones);
        Assert.Equal("ibp:mek4-keyboard:MEK1:leds", zone.Id);
        Assert.Equal(-1, zone.LegacyZoneIndex);
        Assert.Equal(126, Assert.Single(zone.Slices).Count);
    }

    [Fact]
    public void Supported_devices_catalog_lists_every_model_once_under_ibuypower()
    {
        var rows = LightingDevicesCatalog.All
            .Where(d => d.Source == "nexus" && d.VendorId == "0x3402")
            .ToList();
        foreach (var model in IbpPeripheralProtocol.Models)
        {
            var pid = $"0x{model.ProductId:X4}";
            var row = Assert.Single(rows, r => r.ProductId == pid);
            Assert.Equal("iBUYPOWER", row.Vendor);
            Assert.Equal(model.Kind == IbpPeripheralKind.Keyboard ? "keyboard" : "mouse", row.Category);
            Assert.Equal(new[] { "rgb" }, row.Capabilities);
            Assert.Equal(model.Name, "iBUYPOWER " + row.Model);
        }
    }

    private sealed class OneUnitEnumerator : IHidEnumerator
    {
        private readonly HidDeviceInfo _info;
        public OneUnitEnumerator(IbpPeripheralModel model)
        {
            _info = new HidDeviceInfo
            {
                VendorId = IbpPeripheralProtocol.VendorId, ProductId = model.ProductId,
                Path = model.Id, Serial = "S1", FeatureReportByteLength = model.ReportLength,
            };
        }
        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => FindAll();
        public IReadOnlyList<HidDeviceInfo> FindAll() => new[] { _info };
        public IHidDevice? Open(string path, bool forInput = false) => new NullDevice(_info);

        private sealed class NullDevice : IHidDevice
        {
            private readonly HidDeviceInfo _i;
            public NullDevice(HidDeviceInfo i) { _i = i; }
            public int VendorId => _i.VendorId;
            public int ProductId => _i.ProductId;
            public string Path => _i.Path;
            public string? Serial => _i.Serial;
            public int UsagePage => 0;
            public int Usage => 0;
            public bool SetFeature(System.ReadOnlySpan<byte> report) => true;
            public bool GetFeature(System.Span<byte> buffer) => false;
            public bool GetInputReport(System.Span<byte> buffer) => false;
            public bool Write(System.ReadOnlySpan<byte> report) => true;
            public bool SetOutputReport(System.ReadOnlySpan<byte> report) => true;
            public int Read(System.Span<byte> buffer, int timeoutMs) => 0;
            public void Dispose() { }
        }
    }
}
