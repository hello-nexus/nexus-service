using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Peripherals.BulkPanels;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class BulkPanelHandlerTests
{
    private static IReadOnlyList<UsbDeviceEntry> Present(IBulkPanelDriver d) =>
        new[] { new UsbDeviceEntry { VendorId = d.VendorId, ProductId = d.ProductIds[0] } };

    [Fact]
    public void No_warning_until_an_open_attempt_has_failed()
    {
        var driver = new ZMatricesPanelDriver(ZMatricesModel.MaxE87);
        using var hub = new BulkPanelHub(driver);
        var handler = new BulkPanelHandler(hub);

        Assert.Null(handler.GetWarning(Present(driver)));

        hub.SetOpenFailed(true);
        Assert.Equal("bulk-pipe-unavailable", handler.GetWarning(Present(driver)));

        hub.SetOpenAccessDenied(true);
        Assert.Equal("access-denied", handler.GetWarning(Present(driver)));

        hub.SetOpenFailed(false);
        Assert.Null(handler.GetWarning(Present(driver)));
    }

    [Fact]
    public void Only_the_max_e87_reports_the_light_category()
    {
        using var e87 = new BulkPanelHub(new ZMatricesPanelDriver(ZMatricesModel.MaxE87));
        using var glacier = new BulkPanelHub(new ZMatricesPanelDriver());
        using var other = new BulkPanelHub(new HydroShift2LcdDriver());

        Assert.Equal("light", new BulkPanelHandler(e87).Category);
        Assert.Equal("cooler", new BulkPanelHandler(glacier).Category);
        Assert.Equal("cooler", new BulkPanelHandler(other).Category);
    }
}
