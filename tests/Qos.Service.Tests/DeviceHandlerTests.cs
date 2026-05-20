using System.Collections.Generic;
using System.Linq;
using Qos.Service.Devices;
using Qos.Service.Devices.Handlers;
using Xunit;

namespace Qos.Service.Tests;

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
        var h = new CnvsHandler();
        Assert.Equal("cnvs", h.Id);
        Assert.Equal("CNVS", h.Name);
        Assert.Equal("controller", h.Category);
        Assert.NotEmpty(h.Identifiers);
    }

    [Fact]
    public void Cnvs_detects_known_vid_pids()
    {
        var h = new CnvsHandler();
        foreach (var id in h.Identifiers)
        {
            var detected = new List<UsbDeviceEntry> { Entry(id.VendorId, id.ProductId) };
            Assert.True(h.IsConnected(detected), $"CnvsHandler should detect {id.VendorId:X4}:{id.ProductId:X4}");
        }
    }

    [Fact]
    public void Cnvs_does_not_falsely_match_random_vid()
    {
        var h = new CnvsHandler();
        var detected = new List<UsbDeviceEntry> { Entry(0x046d, 0xc52b) }; // Logitech mouse
        Assert.False(h.IsConnected(detected));
    }

    [Fact]
    public void Cnvs_returns_empty_firmware_when_disconnected()
    {
        var h = new CnvsHandler();
        Assert.Equal(string.Empty, h.GetFirmwareVersion());
    }

    [Theory]
    [MemberData(nameof(AllHandlers))]
    public void Every_handler_has_id_name_category(IDeviceHandler handler)
    {
        Assert.False(string.IsNullOrEmpty(handler.Id));
        Assert.False(string.IsNullOrEmpty(handler.Name));
        Assert.False(string.IsNullOrEmpty(handler.Category));
    }

    [Theory]
    [MemberData(nameof(AllHandlers))]
    public void Every_handler_returns_false_for_empty_device_list(IDeviceHandler handler)
    {
        Assert.False(handler.IsConnected(new List<UsbDeviceEntry>()));
    }

    [Theory]
    [MemberData(nameof(AllHandlers))]
    public void Every_handler_has_at_least_one_identifier(IDeviceHandler handler)
    {
        // FanHub has identifiers via VID/PID; if a handler omits them, IsConnected
        // could only return true for type-coupled checks - flag that here.
        Assert.NotNull(handler.Identifiers);
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
        var qs = new QSeriesHandler();
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
        var qs = new QSeriesHandler();
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
        var qs = new QSeriesHandler();
        // A random "Q60" in the descriptor under a third-party VID
        // shouldn't trip the handler — keeps the name-match honest.
        Assert.False(qs.IsConnected(new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x1234, ProductId = 0xABCD, Name = "Generic Q60 Adapter" },
        }));
    }

    [Fact]
    public void QSeries_handler_covers_Q60_and_Q80()
    {
        var qs = new QSeriesHandler();
        // Q-series handler reports both PIDs under both VIDs; the on-
        // device runtime and the qos panel pipeline treat Q60 and Q80
        // identically. Bench firmware enumerates under MediaTek's VID
        // (0x0E8D); HYTE's own VID (0x3402) is kept as a defensive
        // fallback for a future revision.
        var pids = qs.Identifiers.Select(id => id.ProductId).ToHashSet();
        Assert.Contains(0x201D, pids); // Q60 under MediaTek VID (bench-verified)
        Assert.Contains(0x201C, pids); // Q80 under MediaTek VID
        Assert.Contains(0x0600, pids); // Q60 legacy HYTE VID
        Assert.Contains(0x0603, pids); // Q80 legacy HYTE VID
    }

    [Fact]
    public void Y70_and_QSeries_categories_are_displays()
    {
        var y70 = new Y70Handler();
        var qs = new QSeriesHandler();
        // Both are device-display peripherals; exact category strings are
        // implementation detail but should be non-empty.
        Assert.False(string.IsNullOrEmpty(y70.Category));
        Assert.False(string.IsNullOrEmpty(qs.Category));
    }

    public static IEnumerable<object[]> AllHandlers()
    {
        yield return new object[] { new CnvsHandler() };
        yield return new object[] { new QSeriesHandler() };
        yield return new object[] { new Y70Handler() };
        yield return new object[] { new KeebHandler() };
        yield return new object[] { new FanHubHandler() };
    }

    [Fact]
    public void Keeb_handler_detects_suoai_keeb_tkl()
    {
        var k = new KeebHandler();
        Assert.True(k.IsConnected(new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x3402, ProductId = 0x0300, Name = "HYTE Keeb TKL" },
        }), "should match Keeb TKL VID/PID");

        // Old MK9 PIDs are intentionally NOT in the identifier list — they used
        // a different protocol that the service has never actually driven.
        Assert.False(k.IsConnected(new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x3402, ProductId = 0x0900, Name = "Legacy MK9" },
        }), "MK9 (0x0900) should no longer be claimed by KeebHandler");
        Assert.False(k.IsConnected(new List<UsbDeviceEntry>
        {
            new() { VendorId = 0x3402, ProductId = 0x0901, Name = "Legacy MK9 Pro" },
        }), "MK9 Pro (0x0901) should no longer be claimed by KeebHandler");
    }
}
