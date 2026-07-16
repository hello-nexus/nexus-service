using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Models.Displays;
using Nexus.Service.Peripherals.Hyte.Y70Display;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Identity / connection-detection tests for every device handler. These are
/// the cheapest possible regression net: if a handler's Id, Name, Category, or
/// VID/PID list is accidentally changed, the device disappears from
/// /devices/all in production.
/// </summary>
public class DeviceHandlerTests
{
    private static UsbDeviceEntry Entry(int vid, int pid, string name = "Test")
        => new() { VendorId = vid, ProductId = pid, Name = name };

    [Fact]
    public void Cnvs_id_and_metadata()
    {
        var h = TestHandlers.Cnvs();
        Assert.Equal("cnvs", h.Id);
        Assert.Equal("CNVS", h.Name);
        Assert.Equal("controller", h.Category);
        Assert.NotEmpty(h.Identifiers);
    }

    [Fact]
    public void Cnvs_detects_known_vid_pids()
    {
        var h = TestHandlers.Cnvs();
        foreach (var id in h.Identifiers)
        {
            var detected = new List<UsbDeviceEntry> { Entry(id.VendorId, id.ProductId) };
            Assert.True(h.IsConnected(detected), $"CnvsHandler should detect {id.VendorId:X4}:{id.ProductId:X4}");
        }
    }

    [Fact]
    public void Cnvs_does_not_falsely_match_random_vid()
    {
        var h = TestHandlers.Cnvs();
        var detected = new List<UsbDeviceEntry> { Entry(0x046d, 0xc52b) }; // Logitech mouse
        Assert.False(h.IsConnected(detected));
    }

    [Fact]
    public void Cnvs_returns_empty_firmware_when_disconnected()
    {
        var h = TestHandlers.Cnvs();
        Assert.Equal(string.Empty, h.GetFirmwareVersion());
    }

    [Theory]
    [MemberData(nameof(AllHandlers))]
    public void Every_handler_returns_false_for_empty_device_list(IDeviceHandler handler)
    {
        Assert.False(handler.IsConnected(new List<UsbDeviceEntry>()));
    }

    [Theory]
    [MemberData(nameof(AllHandlers))]
    public void Every_handler_detects_each_of_its_own_identifiers(IDeviceHandler handler)
    {
        // QSeriesHandler matches by USB product-name string rather than
        // raw (VID, PID), since the Q-series enumerates with different
        // PIDs in each USB mode. Probe it with a representative name so
        // the same generic identifier-listing assertion still holds.
        var probeName = handler is QSeriesHandler ? "HYTE Q60 Display" : "Test";
        foreach (var id in handler.Identifiers)
        {
            var detected = new List<UsbDeviceEntry> { Entry(id.VendorId, id.ProductId, probeName) };
            Assert.True(handler.IsConnected(detected),
                $"{handler.Name} should detect {id.VendorId:X4}:{id.ProductId:X4}");
        }
    }

    [Fact]
    public void QSeries_handler_detects_by_product_name()
    {
        var qs = TestHandlers.QSeries();
        // Bench-verified product strings from a real Q60 (and the
        // analogous Q80 ones). VID is one of the known Q-series vendors
        // (MediaTek 0x0E8D or HYTE 0x3402); PID can be anything.
        Assert.True(qs.IsConnected(new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x0E8D, ProductId = 0x2048, Name = "HYTE Q60 Display" },
        }), "should match Q60 ADB-mode descriptor");
        Assert.True(qs.IsConnected(new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x3402, ProductId = 0x0400, Name = "HYTE THICC Q60" },
        }), "should match THICC Q60 descriptor under HYTE VID");
        Assert.True(qs.IsConnected(new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x0E8D, ProductId = 0xFFFF, Name = "HYTE Q80 Display" },
        }), "should match Q80 (any PID under MediaTek VID)");
    }

    [Fact]
    public void QSeries_handler_rejects_non_qseries_devices_under_known_vids()
    {
        var qs = TestHandlers.QSeries();
        // MediaTek and HYTE VIDs cover lots of other devices; the name
        // gate keeps the handler from claiming them all.
        Assert.False(qs.IsConnected(new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x0E8D, ProductId = 0x2080, Name = "MediaTek Preloader" },
        }));
        Assert.False(qs.IsConnected(new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x3402, ProductId = 0x0C01, Name = "HYTE Y70 Display" },
        }));
    }

    [Fact]
    public void QSeries_handler_rejects_qseries_name_under_unknown_vid()
    {
        var qs = TestHandlers.QSeries();
        // A random "Q60" in the descriptor under a third-party VID
        // shouldn't trip the handler - keeps the name-match honest.
        Assert.False(qs.IsConnected(new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x1234, ProductId = 0xABCD, Name = "Generic Q60 Adapter" },
        }));
    }

    [Fact]
    public void QSeries_handler_covers_Q60_and_Q80()
    {
        var qs = TestHandlers.QSeries();
        // Q-series handler reports both PIDs under both VIDs; the on-
        // device runtime and the nexus panel pipeline treat Q60 and Q80
        // identically. Bench firmware enumerates under MediaTek's VID
        // (0x0E8D); HYTE's own VID (0x3402) is kept as a defensive
        // fallback for a future revision.
        var pids = qs.Identifiers.Select(id => id.ProductId).ToHashSet();
        Assert.Contains(0x201D, pids); // Q60 under MediaTek VID (bench-verified)
        Assert.Contains(0x201C, pids); // Q80 under MediaTek VID
        Assert.Contains(0x0600, pids); // Q60 legacy HYTE VID
        Assert.Contains(0x0603, pids); // Q80 legacy HYTE VID
    }

    private static DisplayTopologyService TopologyWithY70Monitor()
        => TestHandlers.FakeTopology(new List<RawDisplayInfo>
        {
            new() { Id = "y70-monitor", RawHardwareId = Y70DisplayProtocol.DdcPanelHardwareNames[0] },
        });

    private static DisplayTopologyService TopologyWithMonitor(string rawHardwareId)
        => TestHandlers.FakeTopology(new List<RawDisplayInfo>
        {
            new() { Id = "y70-monitor", RawHardwareId = rawHardwareId },
        });

    [Fact]
    public void Y70_monitor_only_is_connected_with_no_serial_and_no_usb()
    {
        var h = TestHandlers.Y70(TopologyWithY70Monitor());
        Assert.True(h.IsConnected(new List<UsbDeviceEntry>()));
    }

    [Fact]
    public void Y70_not_connected_with_no_monitor_no_serial_no_usb()
    {
        var h = TestHandlers.Y70();
        Assert.False(h.IsConnected(new List<UsbDeviceEntry>()));
    }

    [Fact]
    public void Y70_warns_usb_disconnected_when_monitor_only()
    {
        var h = TestHandlers.Y70(TopologyWithY70Monitor());
        Assert.Equal("usb-disconnected", h.GetWarning(new List<UsbDeviceEntry>()));
    }

    [Fact]
    public void Y70_no_warning_when_neither_monitor_nor_serial_present()
    {
        var h = TestHandlers.Y70();
        Assert.Null(h.GetWarning(new List<UsbDeviceEntry>()));
    }

    [Theory]
    [InlineData(true, true, false, false, null)]                        // fully connected
    [InlineData(false, true, false, false, "usb-disconnected")]         // display only, no panel USB function
    [InlineData(false, true, true, false, null)]                        // Y70ti: touch-only cable proves the cable is attached
    [InlineData(false, true, false, true, null)]                        // GW/Ina: no USB serial function exists; DDC drives it
    [InlineData(true, false, false, false, "display-disconnected")]     // USB/serial only, no display
    [InlineData(true, false, true, false, "display-disconnected")]      // digitizer does not substitute for the display
    [InlineData(false, false, false, false, null)]                      // neither
    [InlineData(false, false, true, false, null)]                       // digitizer alone (no Y70 EDID) warns nothing
    [InlineData(false, false, false, true, null)]                       // GW/Ina EDID gone (unplugged) warns nothing
    public void Y70_warning_matrix(bool serialConnected, bool hasDisplay, bool touchOnlyUsb, bool ddcOnlyPanel, string? expected)
    {
        Assert.Equal(expected, Y70Handler.ComputeWarning(serialConnected, hasDisplay, touchOnlyUsb, ddcOnlyPanel));
    }

    [Theory]
    [InlineData("RTK1234", "y70-gw")]
    [InlineData("RTK2345", "y70-ina")]
    public void Y70_ddc_only_monitor_has_no_warning_and_reports_its_variant(string edidFragment, string expectedVariant)
    {
        var h = TestHandlers.Y70(TopologyWithMonitor(edidFragment));
        Assert.True(h.IsConnected(new List<UsbDeviceEntry>()));
        Assert.Null(h.GetWarning(new List<UsbDeviceEntry>()));
        Assert.Equal(expectedVariant, h.FirmwareType);
    }

    [Fact]
    public void Y70_serial_variant_monitor_only_keeps_keyless_firmware_type()
    {
        var h = TestHandlers.Y70(TopologyWithY70Monitor());
        Assert.Equal("usb-disconnected", h.GetWarning(new List<UsbDeviceEntry>()));
        Assert.Equal("y70", h.FirmwareType);
    }

    [Fact]
    public void Ina_edid_fragment_is_recognized_as_a_y70_display()
    {
        // RTK2345 was added to the reference SCREEN_NAMES after the original
        // port; without it an Ina is invisible to rotation, detection, and
        // DDC targeting.
        Assert.True(DisplayTopologyService.IsY70Display("MONITOR\\RTK2345\\{guid}\\0001"));
    }

    [Theory]
    [InlineData(0x222A, 0x0001)] // ILITEK (Y70ti)
    [InlineData(0x27C0, 0x0859)] // Y70 Touch serial variant
    public void Y70_touch_digitizer_without_serial_function_suppresses_usb_warning(int vid, int pid)
    {
        var h = TestHandlers.Y70(TopologyWithY70Monitor());
        var devices = new List<UsbDeviceEntry>
        {
            new() { VendorId = vid, ProductId = pid, Name = "TouchScreen" },
        };
        Assert.Null(h.GetWarning(devices));
    }

    [Fact]
    public void Y70_serial_function_on_bus_but_hub_disconnected_still_warns()
    {
        // COM port held / driver failure: the 0x3402 serial function is
        // enumerated but the hub cannot connect - a real degraded state.
        var h = TestHandlers.Y70(TopologyWithY70Monitor());
        var devices = new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x27C0, ProductId = 0x0859, Name = "TouchScreen" },
            new() { VendorId = 0x3402, ProductId = 0x0C01, Name = "HYTE Y70 Display" },
        };
        Assert.Equal("usb-disconnected", h.GetWarning(devices));
    }

    [Fact]
    public void Y70_identifiers_are_the_hyte_reference_pid_set()
    {
        // Wire contract per HYTE's reference controllers (Y70Touch /
        // Y70TouchInfinite / Y70TouchTruly): VID_3402 & PID_0C00/0C01/0C02.
        // Pinned as literals so a drifted constant fails here.
        var ids = TestHandlers.Y70().Identifiers.Select(i => (i.VendorId, i.ProductId)).ToArray();
        Assert.Equal(new[] { (0x3402, 0x0C00), (0x3402, 0x0C01), (0x3402, 0x0C02) }, ids);
    }

    [Fact]
    public void Aw5_id_and_metadata()
    {
        var h = new Aw5Handler();
        Assert.Equal("aw5", h.Id);
        Assert.Equal("iBUYPOWER AW5", h.Name);
        Assert.Equal("cooler", h.Category);
    }

    [Fact]
    public void Aw5_identifiers_are_the_published_driver_variants_only()
    {
        // One PID per ODM variant that has a published driver binary. Apaltek
        // (0x0405) is deliberately absent - listing a cooler whose driver cannot
        // be fetched would show a device Nexus can neither drive nor explain.
        var ids = new Aw5Handler().Identifiers.Select(i => (i.VendorId, i.ProductId)).ToArray();
        Assert.Equal(new[] { (0x3402, 0x0406), (0x3402, 0x0407) }, ids);
    }

    [Fact]
    public void Aw5_detects_known_vid_pids()
    {
        var h = new Aw5Handler();
        foreach (var id in h.Identifiers)
        {
            Assert.True(h.IsConnected(new List<UsbDeviceEntry> { Entry(id.VendorId, id.ProductId) }),
                $"Aw5Handler should detect {id.VendorId:X4}:{id.ProductId:X4}");
        }
    }

    [Fact]
    public void Aw5_ignores_apaltek_and_sibling_ibuypower_hardware()
    {
        var h = new Aw5Handler();
        // 0x0405 Apaltek (unpublished driver), 0x0900 MiniHub - same VID, not an AW5 we drive.
        Assert.False(h.IsConnected(new List<UsbDeviceEntry> { Entry(0x3402, 0x0405) }));
        Assert.False(h.IsConnected(new List<UsbDeviceEntry> { Entry(0x3402, 0x0900) }));
    }

    [Fact]
    public void Aw5_offers_the_nexus_control_gate()
    {
        // Nexus never opens the cooler, but the gate still governs the vendor
        // driver process, so the UI must show the switch.
        Assert.True(((IDeviceHandler)new Aw5Handler()).SupportsNexusControl);
        // The interface default stays true, so no existing handler is affected.
        Assert.True(((IDeviceHandler)TestHandlers.FanHub()).SupportsNexusControl);
    }

    [Fact]
    public void Aw5_is_first_party_so_it_carries_no_experimental_badge()
    {
        // Experimental no longer short-circuits on the control opt-out, so the brand
        // list is the only thing keeping the badge off this first-party cooler. Read
        // the id off the handler: a rename must fail here, not ship a badge.
        var h = (IDeviceHandler)new Aw5Handler();
        Assert.False(DeviceControlPolicy.IsExperimental(h.Id));
    }

    public static IEnumerable<object[]> AllHandlers()
    {
        yield return new object[] { TestHandlers.Cnvs() };
        yield return new object[] { TestHandlers.QSeries() };
        yield return new object[] { TestHandlers.Y70() };
        yield return new object[] { new KeebHandler() };
        yield return new object[] { TestHandlers.FanHub() };
        yield return new object[] { new Aw5Handler() };
    }
}
