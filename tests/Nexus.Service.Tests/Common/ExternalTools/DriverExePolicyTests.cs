using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Models.Widgets;
using Xunit;

namespace Nexus.Service.Tests.Common.ExternalTools;

/// <summary>
/// Pins the switch that keeps the vendor driver .exe and the native AW5 hub from
/// both driving one cooler. Shipping this on by accident puts two writers on the
/// same HID, which on the Levelplay renders as a panel that flickers between two
/// hosts rather than as an error.
/// </summary>
public class DriverExePolicyTests
{
    private static AppManifestDriver HostExe() => new() { ToolId = "ibp-aw5", DeviceId = "aw5" };
    private static AppManifestDriver Adb() => new() { ToolId = "qshell", Target = "android-adb", Package = "com.nexus.qshell" };

    [Fact]
    public void Ships_with_the_vendor_driver_exe_disabled()
    {
        // The registration in NexusServiceCollectionExtensions passes enabled: false;
        // this pins what that means for a host-exe driver.
        Assert.True(new DriverExePolicy(enabled: false).IsBlocked(HostExe()));
    }

    [Fact]
    public void Enabling_restores_the_vendor_path()
    {
        Assert.False(new DriverExePolicy(enabled: true).IsBlocked(HostExe()));
    }

    [Fact]
    public void Android_adb_drivers_are_never_blocked()
    {
        // An adb driver pushes an APK; it runs no host process and cannot contend
        // for a HID, so the AW5's problem is not its problem.
        Assert.False(new DriverExePolicy(enabled: false).IsBlocked(Adb()));
        Assert.False(new DriverExePolicy(enabled: true).IsBlocked(Adb()));
    }

    [Fact]
    public void A_driver_naming_no_target_counts_as_host_exe()
    {
        // The AW5 manifest omits target; defaulting the other way would silently
        // leave the vendor .exe launching.
        Assert.True(DriverExePolicy.IsHostExe(new AppManifestDriver { ToolId = "x" }));
    }
}
