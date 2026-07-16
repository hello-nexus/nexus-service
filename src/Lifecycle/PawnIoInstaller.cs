using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lifecycle.Native;
using Nexus.Service.Platform;

namespace Nexus.Service.Lifecycle;

public enum PawnIoInstallResult
{
    AlreadyInstalled,
    Installed,
    Upgraded,
    Repaired,
    UpgradeStagedPendingReboot,
    UserDenied,
    Failed,
    NotApplicable, // non-Windows
}

/// <summary>
/// Auto-installs the bundled PawnIO kernel driver via pnputil.
///
/// On first launch, Nexus.exe self-elevates (one UAC prompt) and reruns
/// itself with --install-pawnio. The elevated child runs pnputil /add-driver /install
/// against the bundled PawnIO.inf, which copies the driver to the driver store,
/// creates the root device node, and starts the kernel service. Subsequent
/// launches detect the registered service and compare its version against
/// the bundled driver; an older installed version triggers a second elevated
/// run (--install-pawnio --upgrade) that rebinds the existing device to the
/// newer package without creating a duplicate device node.
///
/// We use pnputil (not raw SCM) because PawnIO.sys is signed with a regular
/// code-signing cert (not a Microsoft kernel cross-signature). The catalog file
/// (.cat) provides the trust chain that Windows accepts via the Driver Store
/// install path. Direct SCM CreateService → StartService fails with
/// ERROR_INVALID_IMAGE_HASH (577) without going through the catalog.
///
/// Registered is not running: the service registry key and the driver-store
/// package survive states where \Device\PawnIO does not exist (deleted device
/// node, a driver that failed to start after an upgrade). A version compare
/// cannot see any of them, so every check also opens the device itself and a
/// registered-but-dead driver triggers an elevated repair
/// (--install-pawnio --repair) that reinstalls the package, rebinds - or
/// recreates - the root device node, and verifies the device opens.
/// </summary>
public static class PawnIoInstaller
{
    private const string ServiceRegistryKey = @"SYSTEM\CurrentControlSet\Services\PawnIO";
    private const string HardwareId = "Root\\PawnIO";

    // SoftwareDevice setup class GUID - matches Class={62f9c741-...} in PawnIO.inf
    private static readonly Guid SoftwareDeviceClassGuid = new("62f9c741-b25a-46ce-b54c-9bccce08b6f2");

    private const int ErrorSuccessRebootRequired = 3010;
    private const string ImagePathValueName = "ImagePath";

    // Two boot-time readings taken minutes apart on the same boot can differ
    // by a little clock drift; this tolerance is generous enough to absorb
    // that while still catching an actual reboot (which shifts TickCount64's
    // baseline by however long the machine was down, always far more than this).
    private static readonly TimeSpan BootTimeMatchTolerance = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ensures the PawnIO kernel driver is installed, up to date, and actually
    /// running. If the driver service isn't registered yet, spawns an elevated
    /// helper (one-time UAC prompt) to install it. If it is registered but the
    /// installed driver is older than the bundled one, spawns the same
    /// elevated helper to upgrade it in place - unless a prior run already
    /// staged that exact upgrade this boot, in which case it reports the
    /// pending-reboot state without prompting again. If it is registered and
    /// version-current but \Device\PawnIO does not open, spawns the helper to
    /// repair it (reinstall + rebind/recreate the device node). Only a
    /// registered, version-current, openable driver returns AlreadyInstalled.
    /// </summary>
    public static async Task<PawnIoInstallResult> EnsureInstalledAsync(CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return PawnIoInstallResult.NotApplicable;
        }

        if (IsServiceRegistered())
        {
            var deviceAvailable = IsDeviceAvailable();
            var bundled = GetBundledDriverVersion();
            if (bundled is null)
            {
                // No bundled driver beside this exe (dev run) - nothing to
                // upgrade or repair from, whatever the device state is.
                ServiceLog.Info(
                    $"[pawnio] registered, no bundled driver, device {(deviceAvailable ? "up" : "unavailable")} -> leaving as-is");
                return PawnIoInstallResult.AlreadyInstalled;
            }

            var installed = GetInstalledDriverVersion();
            if (NeedsRepair(installed, bundled, deviceAvailable))
            {
                var repairMarker = PawnIoUpgradeMarkerStore.Read();
                var repairBootTimeUtc = GetBootTimeUtc();
                if (IsStagedThisBoot(repairMarker, bundled, repairBootTimeUtc))
                {
                    ServiceLog.Info($"[pawnio] repair to {bundled} staged; pending reboot");
                    return PawnIoInstallResult.UpgradeStagedPendingReboot;
                }

                ServiceLog.Info(
                    $"[pawnio] installed {installed?.ToString() ?? "unknown"}, bundled {bundled}, device unavailable -> repairing");
                var repairResult = await RunElevatedAsync(ElevatedRun.Repair, ct);
                if (repairResult == PawnIoInstallResult.UpgradeStagedPendingReboot)
                {
                    PawnIoUpgradeMarkerStore.Write(new PawnIoUpgradeMarker
                    {
                        StagedVersion = bundled.ToString(),
                        StagedAtBootTimeUtc = repairBootTimeUtc,
                    });
                }
                else if (repairResult == PawnIoInstallResult.Repaired)
                {
                    PawnIoUpgradeMarkerStore.Delete();
                }

                return repairResult;
            }

            if (!ShouldUpgrade(installed, bundled))
            {
                PawnIoUpgradeMarkerStore.Delete();
                ServiceLog.Info(
                    $"[pawnio] installed {installed?.ToString() ?? "unknown"}, bundled {bundled}, device up -> up to date");
                return PawnIoInstallResult.AlreadyInstalled;
            }

            var marker = PawnIoUpgradeMarkerStore.Read();
            var currentBootTimeUtc = GetBootTimeUtc();
            if (!ShouldPromptUpgrade(installed, bundled, marker, currentBootTimeUtc))
            {
                ServiceLog.Info($"[pawnio] upgrade to {bundled} staged; pending reboot");
                return PawnIoInstallResult.UpgradeStagedPendingReboot;
            }

            ServiceLog.Info(
                $"[pawnio] installed {installed}, bundled {bundled}, device {(deviceAvailable ? "up" : "unavailable")} -> upgrading");
            var result = await RunElevatedAsync(ElevatedRun.Upgrade, ct);
            if (result == PawnIoInstallResult.UpgradeStagedPendingReboot)
            {
                PawnIoUpgradeMarkerStore.Write(new PawnIoUpgradeMarker
                {
                    StagedVersion = bundled.ToString(),
                    StagedAtBootTimeUtc = currentBootTimeUtc,
                });
            }
            else if (result == PawnIoInstallResult.Upgraded)
            {
                PawnIoUpgradeMarkerStore.Delete();
            }

            return result;
        }

        var installResult = await RunElevatedAsync(ElevatedRun.Install, ct);
        if (installResult == PawnIoInstallResult.UpgradeStagedPendingReboot)
        {
            // Latch a reboot-staged fresh install like the upgrade and repair
            // paths, or a same-boot service restart sees registered + dead and
            // runs a redundant repair.
            var stagedBundled = GetBundledDriverVersion();
            if (stagedBundled is not null)
            {
                PawnIoUpgradeMarkerStore.Write(new PawnIoUpgradeMarker
                {
                    StagedVersion = stagedBundled.ToString(),
                    StagedAtBootTimeUtc = GetBootTimeUtc(),
                });
            }
        }

        return installResult;
    }

    /// <summary>
    /// True when the registered driver should be repaired rather than upgraded
    /// or left alone: no device object exists and no upgrade is going to run
    /// (an upgrade rebinds the device anyway, so it subsumes the repair). An
    /// unknown installed version with a dead device is the deleted-ImagePath
    /// state and also repairs.
    /// </summary>
    internal static bool NeedsRepair(Version? installed, Version bundled, bool deviceAvailable)
    {
        return !deviceAvailable && !ShouldUpgrade(installed, bundled);
    }

    private static DateTime GetBootTimeUtc() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>
    /// True when an elevated upgrade run should be prompted for. False when
    /// no upgrade is needed, or when a prior run already staged this exact
    /// bundled version and no reboot has happened since (the marker's boot
    /// time still matches), so the pending package just needs a restart.
    /// </summary>
    internal static bool ShouldPromptUpgrade(
        Version? installed, Version bundled, PawnIoUpgradeMarker? marker, DateTime currentBootTimeUtc)
    {
        if (!ShouldUpgrade(installed, bundled))
        {
            return false;
        }

        return !IsStagedThisBoot(marker, bundled, currentBootTimeUtc);
    }

    /// <summary>
    /// True when the marker records this exact bundled version as staged on
    /// the current boot - the pending package only needs a reboot, so neither
    /// the upgrade nor the repair path should run the elevated helper again.
    /// </summary>
    internal static bool IsStagedThisBoot(
        PawnIoUpgradeMarker? marker, Version bundled, DateTime currentBootTimeUtc)
    {
        return marker is not null
            && Version.TryParse(marker.StagedVersion, out var stagedVersion)
            && stagedVersion == bundled
            && (marker.StagedAtBootTimeUtc - currentBootTimeUtc).Duration() <= BootTimeMatchTolerance;
    }

    private enum ElevatedRun
    {
        Install,
        Upgrade,
        Repair,
    }

    // Spawns the elevated --install-pawnio helper (with --upgrade or --repair
    // for those modes) and waits for it to exit.
    private static async Task<PawnIoInstallResult> RunElevatedAsync(ElevatedRun mode, CancellationToken ct)
    {
        var infPath = Path.Combine(AppContext.BaseDirectory, "pawnio", "PawnIO.inf");
        if (!File.Exists(infPath))
        {
            ServiceLog.Error($"[pawnio] bundled INF not found at {infPath}");
            return PawnIoInstallResult.Failed;
        }

        var exePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath))
        {
            ServiceLog.Error("[pawnio] cannot determine own exe path");
            return PawnIoInstallResult.Failed;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = mode switch
            {
                ElevatedRun.Upgrade => "--install-pawnio --upgrade",
                ElevatedRun.Repair => "--install-pawnio --repair",
                _ => "--install-pawnio",
            },
            Verb = "runas",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED - user clicked "No" on the UAC prompt
            return PawnIoInstallResult.UserDenied;
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[pawnio] failed to spawn elevated installer: {ex.Message}");
            return PawnIoInstallResult.Failed;
        }

        if (proc is null)
        {
            return PawnIoInstallResult.Failed;
        }

        try
        { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { return PawnIoInstallResult.Failed; }

        if (proc.ExitCode == ErrorSuccessRebootRequired)
        {
            return PawnIoInstallResult.UpgradeStagedPendingReboot;
        }

        if (proc.ExitCode != 0)
        {
            ServiceLog.Error($"[pawnio] elevated installer exited with code {proc.ExitCode}");
            return PawnIoInstallResult.Failed;
        }

        return mode switch
        {
            ElevatedRun.Upgrade => PawnIoInstallResult.Upgraded,
            ElevatedRun.Repair => PawnIoInstallResult.Repaired,
            _ => IsServiceRegistered() ? PawnIoInstallResult.Installed : PawnIoInstallResult.Failed,
        };
    }

    /// <summary>
    /// True only when the installed version is known and strictly older than
    /// the bundled one. An unknown installed version never triggers an
    /// upgrade, and a bundled version that is not newer never downgrades.
    /// </summary>
    internal static bool ShouldUpgrade(Version? installed, Version bundled)
    {
        return installed is not null && installed < bundled;
    }

    internal static Version? GetBundledDriverVersion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return File.Exists(PawnIoPaths.BundledSysPath) ? ReadFileVersion(PawnIoPaths.BundledSysPath) : null;
    }

    internal static Version? GetInstalledDriverVersion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(ServiceRegistryKey);
            if (key?.GetValue(ImagePathValueName) is not string imagePath || string.IsNullOrWhiteSpace(imagePath))
            {
                return null;
            }

            var resolved = ResolveImagePath(imagePath);
            return resolved is not null && File.Exists(resolved) ? ReadFileVersion(resolved) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves a driver service ImagePath registry value to a real file
    /// path. Handles the forms Windows uses for a kernel driver: an absolute
    /// drive path, an NT native path (\??\C:\...), a %SystemRoot%-relative
    /// token (\SystemRoot\...), and a bare path relative to %SystemRoot%
    /// (System32\Drivers\X.sys). Returns null for an NT native path that
    /// isn't drive-rooted (e.g. a volume-GUID path) - not a form PawnIO's
    /// ImagePath uses, and not resolvable to a plain file path here.
    /// systemRoot overrides the environment lookup for testing.
    /// </summary>
    internal static string? ResolveImagePath(string imagePath, string? systemRoot = null)
    {
        var path = imagePath.Trim();

        const string NtPathPrefix = @"\??\";
        if (path.StartsWith(NtPathPrefix, StringComparison.Ordinal))
        {
            path = path[NtPathPrefix.Length..];
            return path.Length >= 2 && path[1] == ':' ? path : null;
        }

        const string SystemRootToken = @"\SystemRoot\";
        if (path.StartsWith(SystemRootToken, StringComparison.OrdinalIgnoreCase))
        {
            return JoinSystemRoot(path[SystemRootToken.Length..], systemRoot);
        }

        // A drive-rooted path ("C:\...") is already absolute; anything else
        // is relative to %SystemRoot%.
        if (path.Length >= 2 && path[1] == ':')
        {
            return path;
        }

        return JoinSystemRoot(path, systemRoot);
    }

    private static string JoinSystemRoot(string relative, string? systemRoot)
    {
        var root = (systemRoot ?? Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows").TrimEnd('\\');
        return $@"{root}\{relative.TrimStart('\\')}";
    }

    [SupportedOSPlatform("windows")]
    internal static Version? ReadFileVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (info.FileMajorPart == 0 && info.FileMinorPart == 0
                && info.FileBuildPart == 0 && info.FilePrivatePart == 0)
            {
                return null;
            }

            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Elevated entry point. Called when Nexus.exe is launched with the
    /// --install-pawnio command-line arg (plus --upgrade when upgrading an
    /// already-registered driver in place, or --repair when the driver is
    /// registered but \Device\PawnIO does not open). Runs pnputil to install
    /// the bundled driver. Returns 0 when the driver is live, 3010
    /// (ERROR_SUCCESS_REBOOT_REQUIRED) when it is bound but needs a device
    /// restart or reboot to activate, 1 on failure.
    /// </summary>
    public static int RunElevatedInstall(bool upgrade = false, bool repair = false)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return 1;
        }

        try
        {
            return repair ? DoRepair() : upgrade ? DoUpgrade() : DoInstall();
        }
        catch (Exception ex)
        {
            LogError($"unhandled exception: {ex}");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static int DoInstall()
    {
        if (!IsCurrentProcessElevated())
        {
            LogError("not running as administrator");
            return 1;
        }

        var infPath = Path.Combine(AppContext.BaseDirectory, "pawnio", "PawnIO.inf");
        if (!File.Exists(infPath))
        {
            LogError($"bundled INF not found at {infPath}");
            return 1;
        }
        Log($"installing driver from {infPath}");

        if (!RunPnputilAddDriver(infPath))
        {
            return 1;
        }

        // pnputil added the driver package to the driver store, but PawnIO is a
        // root-enumerated PnP device - there's no physical device for PnP to
        // auto-enumerate. We have to create the root device node ourselves via
        // SetupAPI, then call UpdateDriverForPlugAndPlayDevices to bind the
        // driver to it.
        var bindResult = CreateRootDeviceAndBindDriver(infPath);
        if (bindResult == DriverBindResult.Failed)
        {
            LogError("failed to create root device");
            return 1;
        }

        // Verify the service is now registered.
        if (!IsServiceRegistered())
        {
            LogError("driver service not registered after install");
            return 1;
        }

        if (bindResult == DriverBindResult.RebootRequired)
        {
            Log("install staged, reboot required to activate");
            return ErrorSuccessRebootRequired;
        }

        if (!WaitForDeviceOpen())
        {
            LogError(@"driver bound but \Device\PawnIO does not open");
            return 1;
        }

        Log("install complete, device open");
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static int DoUpgrade()
    {
        if (!IsCurrentProcessElevated())
        {
            LogError("not running as administrator");
            return 1;
        }

        if (!IsServiceRegistered())
        {
            LogError("PawnIO service not registered, nothing to upgrade");
            return 1;
        }

        var infPath = Path.Combine(AppContext.BaseDirectory, "pawnio", "PawnIO.inf");
        if (!File.Exists(infPath))
        {
            LogError($"bundled INF not found at {infPath}");
            return 1;
        }
        Log($"upgrading driver from {infPath}");

        if (!RunPnputilAddDriver(infPath))
        {
            return 1;
        }

        // The root device normally exists from the original install - only
        // rebind the driver, do not create a second device node. A missing
        // node (the service key survives its deletion) is recreated instead.
        var bindResult = BindDriverToExistingDevice(infPath);
        if (bindResult == DriverBindResult.NoDevice)
        {
            Log("device node missing; recreating it");
            bindResult = CreateRootDeviceAndBindDriver(infPath);
        }

        if (bindResult == DriverBindResult.Failed)
        {
            LogError("failed to bind upgraded driver");
            return 1;
        }

        if (bindResult == DriverBindResult.RebootRequired)
        {
            return ErrorSuccessRebootRequired;
        }

        if (!WaitForDeviceOpen())
        {
            LogError(@"driver bound but \Device\PawnIO does not open");
            return 1;
        }

        Log("upgrade complete, device open");
        return 0;
    }

    /// <summary>
    /// Elevated repair for a registered-but-dead driver: the service key and
    /// driver-store package look healthy but \Device\PawnIO does not open
    /// (deleted device node, a driver that failed to start). Reinstalls the
    /// package, rebinds the existing device node - recreating it when it is
    /// gone - and verifies the device opens before reporting success.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static int DoRepair()
    {
        if (!IsCurrentProcessElevated())
        {
            LogError("not running as administrator");
            return 1;
        }

        if (!IsServiceRegistered())
        {
            LogError("PawnIO service not registered, nothing to repair");
            return 1;
        }

        var infPath = Path.Combine(AppContext.BaseDirectory, "pawnio", "PawnIO.inf");
        if (!File.Exists(infPath))
        {
            LogError($"bundled INF not found at {infPath}");
            return 1;
        }
        Log($"repairing driver from {infPath}");

        if (!RunPnputilAddDriver(infPath))
        {
            return 1;
        }

        var bindResult = BindDriverToExistingDevice(infPath);
        if (bindResult == DriverBindResult.NoDevice)
        {
            Log("device node missing; recreating it");
            bindResult = CreateRootDeviceAndBindDriver(infPath);
        }

        if (bindResult == DriverBindResult.Failed)
        {
            LogError("failed to bind driver during repair");
            return 1;
        }

        if (bindResult == DriverBindResult.RebootRequired)
        {
            Log("repair staged, reboot required to activate");
            return ErrorSuccessRebootRequired;
        }

        if (!WaitForDeviceOpen())
        {
            LogError(@"driver bound but \Device\PawnIO still does not open");
            return 1;
        }

        Log("repair complete, device open");
        return 0;
    }

    // UpdateDriverForPlugAndPlayDevices returns once the install transaction
    // commits; the device object can appear shortly after. Poll the device
    // itself briefly so a 0 exit means "openable now", not "bind reported ok".
    [SupportedOSPlatform("windows")]
    private static bool WaitForDeviceOpen()
    {
        var deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            if (IsDeviceAvailable())
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(250);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool RunPnputilAddDriver(string infPath)
    {
        // pnputil.exe lives in System32 - use the absolute path so we don't
        // depend on PATH.
        var pnputil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        if (!File.Exists(pnputil))
        {
            LogError($"pnputil.exe not found at {pnputil}");
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = pnputil,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/add-driver");
        psi.ArgumentList.Add(infPath);
        psi.ArgumentList.Add("/install");

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                LogError("Process.Start returned null");
                return false;
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                Log($"pnputil stdout: {stdout.Trim()}");
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                Log($"pnputil stderr: {stderr.Trim()}");
            }

            Log($"pnputil exit code: {proc.ExitCode}");

            // pnputil exit codes:
            //   0 = success
            //   259 (ERROR_NO_MORE_ITEMS) = nothing to install
            //   3010 (ERROR_SUCCESS_REBOOT_REQUIRED) = installed but reboot needed
            // We accept 0 and 3010 as success.
            if (proc.ExitCode != 0 && proc.ExitCode != ErrorSuccessRebootRequired)
            {
                LogError($"pnputil failed with exit code {proc.ExitCode}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LogError($"pnputil invocation failed: {ex.Message}");
            return false;
        }
    }

    // Live: the new driver is loaded and running. RebootRequired: the
    // package is committed to the driver store and bound to the device, but
    // the currently loaded kernel image is unchanged until a device restart
    // or reboot. NoDevice: no Root\PawnIO device node exists to bind to (the
    // service registry key survives node deletion). Failed: the bind did not
    // take at all.
    private enum DriverBindResult
    {
        Live,
        RebootRequired,
        NoDevice,
        Failed,
    }

    // SPAPI_E_NO_SUCH_DEVINST - UpdateDriverForPlugAndPlayDevices found no
    // device node matching the hardware id.
    private const int SpapiENoSuchDevinst = unchecked((int)0xE000020B);

    [SupportedOSPlatform("windows")]
    private static DriverBindResult CreateRootDeviceAndBindDriver(string infPath)
    {
        var classGuid = SoftwareDeviceClassGuid;
        var deviceInfoSet = SetupApi.SetupDiCreateDeviceInfoList(in classGuid, IntPtr.Zero);
        if (deviceInfoSet == SetupApi.INVALID_HANDLE_VALUE)
        {
            LogError($"SetupDiCreateDeviceInfoList failed: {Marshal.GetLastWin32Error()}");
            return DriverBindResult.Failed;
        }

        try
        {
            var deviceInfoData = new SetupApi.SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>(),
            };

            if (!SetupApi.SetupDiCreateDeviceInfo(
                    deviceInfoSet,
                    "PawnIO",
                    in classGuid,
                    null,
                    IntPtr.Zero,
                    SetupApi.DICD_GENERATE_ID,
                    ref deviceInfoData))
            {
                LogError($"SetupDiCreateDeviceInfo failed: {Marshal.GetLastWin32Error()}");
                return DriverBindResult.Failed;
            }

            // Hardware ID is REG_MULTI_SZ - double-null-terminated UTF-16
            var hwIdBuffer = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
            if (!SetupApi.SetupDiSetDeviceRegistryProperty(
                    deviceInfoSet,
                    ref deviceInfoData,
                    SetupApi.SPDRP_HARDWAREID,
                    hwIdBuffer,
                    (uint)hwIdBuffer.Length))
            {
                LogError($"SetupDiSetDeviceRegistryProperty failed: {Marshal.GetLastWin32Error()}");
                return DriverBindResult.Failed;
            }

            if (!SetupApi.SetupDiCallClassInstaller(
                    SetupApi.DIF_REGISTERDEVICE,
                    deviceInfoSet,
                    ref deviceInfoData))
            {
                LogError($"SetupDiCallClassInstaller(DIF_REGISTERDEVICE) failed: {Marshal.GetLastWin32Error()}");
                return DriverBindResult.Failed;
            }

            Log("root device registered");

            // Now bind the driver to our newly created device. INF path must be
            // absolute and the catalog file must be in the same directory.
            var bindResult = BindDriver(infPath);
            if (bindResult is DriverBindResult.Failed or DriverBindResult.NoDevice)
            {
                // NoDevice against a node created lines above means the
                // registration didn't take; either way the bind failed.
                // Cleanup the device we just created.
                SetupApi.SetupDiCallClassInstaller(SetupApi.DIF_REMOVE, deviceInfoSet, ref deviceInfoData);
                return DriverBindResult.Failed;
            }

            Log($"driver bound to root device (reboot needed: {bindResult == DriverBindResult.RebootRequired})");
            return bindResult;
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    // Rebinds the driver to the device node an earlier install already
    // created. Used by the upgrade and repair paths, which must not create a
    // second device node for the same hardware ID.
    [SupportedOSPlatform("windows")]
    private static DriverBindResult BindDriverToExistingDevice(string infPath)
    {
        var bindResult = BindDriver(infPath);
        switch (bindResult)
        {
            case DriverBindResult.RebootRequired:
                // Another process may hold the current driver handle open
                // (PawnIO is a shared kernel service); do not force it to
                // unload. The new package is already in the driver store and
                // bound to the device, so it activates on the next device
                // restart or reboot without disrupting whoever is using it now.
                Log("driver bound to existing device, reboot required to activate");
                break;
            case DriverBindResult.Live:
                Log("driver bound to existing device");
                break;
        }

        return bindResult;
    }

    // Binds infPath's driver package to the Root\PawnIO device. Treats both
    // a true return and a false return with ERROR_SUCCESS_REBOOT_REQUIRED as
    // a committed bind, since the package is in the driver store either way
    // and only needs a later device restart to take over from the currently
    // loaded image.
    [SupportedOSPlatform("windows")]
    private static DriverBindResult BindDriver(string infPath)
    {
        bool rebootRequired = false;
        if (SetupApi.UpdateDriverForPlugAndPlayDevices(
                IntPtr.Zero,
                HardwareId,
                infPath,
                SetupApi.INSTALLFLAG_FORCE,
                ref rebootRequired))
        {
            return rebootRequired ? DriverBindResult.RebootRequired : DriverBindResult.Live;
        }

        var err = Marshal.GetLastWin32Error();
        if (err == ErrorSuccessRebootRequired)
        {
            return DriverBindResult.RebootRequired;
        }

        if (err == SpapiENoSuchDevinst)
        {
            Log("no PawnIO device node exists to bind");
            return DriverBindResult.NoDevice;
        }

        LogError($"UpdateDriverForPlugAndPlayDevices failed: {err}");
        return DriverBindResult.Failed;
    }

    private static bool IsServiceRegistered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(ServiceRegistryKey);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    /// <summary>
    /// True when a \Device\PawnIO object exists - the driver is loaded and
    /// running right now, regardless of what the registry says. Opening the
    /// device is the same probe LibreHardwareMonitor uses to reach the driver,
    /// so it is the authoritative liveness signal: a registered-but-dead
    /// driver fails it and triggers the repair path, and every
    /// install/upgrade/repair verifies against it before reporting success.
    /// Only FILE/PATH_NOT_FOUND count as dead: any other open failure
    /// (ERROR_ACCESS_DENIED from an unelevated caller, a sharing violation)
    /// proves an object answered, and treating it as dead would force-rebind
    /// a live driver on every unelevated interactive launch.
    /// </summary>
    internal static bool IsDeviceAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        try
        {
            var handle = CreateFileW(
                @"\\?\GLOBALROOT\Device\PawnIO",
                0xC0000000, // GENERIC_READ | GENERIC_WRITE
                3,          // FILE_SHARE_READ | FILE_SHARE_WRITE
                IntPtr.Zero,
                3,          // OPEN_EXISTING
                0x80,       // FILE_ATTRIBUTE_NORMAL
                IntPtr.Zero);

            if (handle != new IntPtr(-1))
            {
                CloseHandle(handle);
                return true;
            }

            var err = Marshal.GetLastWin32Error();
            return err != ErrorFileNotFound && err != ErrorPathNotFound;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [SupportedOSPlatform("windows")]
    private static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string LogPath => Path.Combine(
        Nexus.Service.Platform.ServiceLog.LogsDirectory, "nexus-pawnio.log");

    private static void Log(string msg)
    {
        Console.Error.WriteLine($"[pawnio-install] {msg}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:O} {msg}\n");
        }
        catch { /* logging is best-effort */ }
    }

    private static void LogError(string msg) => Log("ERROR: " + msg);
}
