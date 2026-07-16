using System;
using Nexus.Service.Models.Widgets;

namespace Nexus.Service.Common.ExternalTools;

/// <summary>
/// Whether the service may fetch and launch a vendor driver .exe.
///
/// Ships disabled: the AW5 is driven natively by
/// <see cref="Nexus.Service.Peripherals.Aw5.Aw5PanelWorker"/>, and the vendor binary
/// would be a second writer on the same HID. It also has no graceful stop
/// (<see cref="HostExeInstallStrategy.Terminate"/> is a hard kill), so the two cannot
/// hand the device over. Exactly one of them runs: enabling this stands the native
/// worker down.
///
/// The AW5 app is the only one carrying a driver manifest block, so disabling this
/// disables host-exe drivers outright. Android (adb) drivers push an APK rather than
/// run a host process and are unaffected.
///
/// To restore the vendor path, flip the DI registration to <c>enabled: true</c>.
/// </summary>
public sealed class DriverExePolicy
{
    public DriverExePolicy(bool enabled)
    {
        Enabled = enabled;
    }

    public bool Enabled { get; }

    /// <summary>True when this driver runs a host .exe rather than pushing an APK over adb.</summary>
    public static bool IsHostExe(AppManifestDriver driver)
        => !string.Equals(driver.Target, "android-adb", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this driver must not be fetched, launched, or offered as installable.</summary>
    public bool IsBlocked(AppManifestDriver driver) => !Enabled && IsHostExe(driver);
}
