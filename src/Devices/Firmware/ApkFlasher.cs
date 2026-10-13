using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Common.ExternalTools;
using Nexus.Service.Models.Devices;
using Nexus.Service.Panel;
using Nexus.Service.Platform;

namespace Nexus.Service.Devices.Firmware;

/// <summary>Delegate for fetching the qshell manifest entry, injectable in tests.</summary>
internal delegate Task<ToolVersion?> ManifestFetcher(CancellationToken ct);

/// <summary>Delegate for downloading the qshell APK and returning its path, injectable in tests.</summary>
internal delegate Task<string?> ApkDownloader(CancellationToken ct);

/// <summary>
/// Manual-flash path for the qshell APK on the connected Q-series panel.
/// Installs qshell alongside the OEM launcher (both coexist), then sets qshell
/// as the default HOME via cmd package set-home-activity.
/// Acquires <see cref="FlashGate"/> so this flash and a concurrent DFU flash
/// are mutually exclusive.
/// </summary>
public sealed class ApkFlasher
{
    // Customer build: qshell 0.1.5-marito (platform-signed USB re-arm), never the public manifest.
    private const string QshellManifestBase = "https://usercontent.hellonexus.com/builds/qshell-0.1.5-marito";
    private const string QshellManifestUrl = QshellManifestBase + "/latest.json";
    private const string QshellDownloadBase = QshellManifestBase;

    internal const string QshellPackage = "com.hellonexus.qshell";
    private const string QshellComponent = QshellPackage + "/" + QshellPackage + ".MainActivity";

    // adb shell command that switches the default HOME launcher (no reboot needed).
    private const string SetHomeCmd = "cmd package set-home-activity ";
    // Launches the current default HOME so the panel switches immediately after set-home.
    private const string GoHomeCmd = "am start -a android.intent.action.MAIN -c android.intent.category.HOME";
    // Best-effort check: resolves the current default HOME; output must contain qshell package.
    private const string ResolveHomeCmd = "cmd package resolve-activity -a android.intent.action.MAIN -c android.intent.category.HOME";

    /// <summary>DeviceType key used in firmware-status and flash endpoints.</summary>
    internal const string DeviceTypeKey = "qseries-app";

    private static readonly ExternalToolSpec QshellSpec = new(
        ToolId: "qshell",
        Variant: "apk",
        ManifestUrl: QshellManifestUrl,
        DownloadUrlBase: QshellDownloadBase,
        FilePattern: "*.apk",
        Launch: new ToolLaunchOptions());

    // Manifest cache: avoids a remote HTTPS round-trip on every /status poll (60 s TTL).
    private const int ManifestCacheTtlSeconds = 60;
    private ToolVersion? _cachedManifest;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    private readonly IAdbDeviceRegistry _deviceRegistry;
    private readonly ManifestFetcher _fetchManifest;
    private readonly ApkDownloader _downloadApk;
    private readonly AdbInstallRunner _installRunner;
    private readonly AdbUninstallRunner? _uninstallRunner;
    private readonly FlashGate _flashGate;

    /// <summary>Production constructor.</summary>
    public ApkFlasher(IAdbDeviceRegistry deviceRegistry, ExternalToolManager toolManager, FlashGate flashGate)
    {
        _deviceRegistry = deviceRegistry;
        _fetchManifest = ct => toolManager.GetLatestAsync(QshellSpec, ct);
        _downloadApk = ct => toolManager.ResolveAsync(QshellSpec, ct);
        _installRunner = AdbHelpers.RunAdbInstall;
        _flashGate = flashGate;
    }

    /// <summary>Test seam: inject all I/O as delegates.</summary>
    internal ApkFlasher(
        IAdbDeviceRegistry deviceRegistry,
        FlashGate flashGate,
        ManifestFetcher manifestFetcher,
        ApkDownloader apkDownloader,
        AdbInstallRunner? installRunner = null,
        AdbUninstallRunner? uninstallRunner = null)
    {
        _deviceRegistry = deviceRegistry;
        _flashGate = flashGate;
        _fetchManifest = manifestFetcher;
        _downloadApk = apkDownloader;
        _installRunner = installRunner ?? AdbHelpers.RunAdbInstall;
        _uninstallRunner = uninstallRunner;
    }

    /// <summary>
    /// Fetch the latest qshell manifest entry, serving a 60-second cache to
    /// avoid a remote round-trip on every /devices/firmware/status poll.
    /// </summary>
    public async Task<ToolVersion?> GetLatestCachedAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now < _cacheExpiry && _cachedManifest is not null)
        {
            return _cachedManifest;
        }

        await _cacheLock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow < _cacheExpiry && _cachedManifest is not null)
            {
                return _cachedManifest;
            }
            _cachedManifest = await _fetchManifest(ct);
            _cacheExpiry = DateTime.UtcNow.AddSeconds(ManifestCacheTtlSeconds);
            return _cachedManifest;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public bool TryStart(string deviceType, string version, out string error)
    {
        if (deviceType != DeviceTypeKey)
        {
            error = "not handled";
            return false;
        }
        return TryStartCore(version, factoryReset: false, out error);
    }

    /// <summary>
    /// Uninstall qshell (dropping its on-device data), then reinstall the current
    /// manifest build and re-pin it as HOME. Shares <see cref="FlashGate"/> with
    /// the manual flash and DFU paths.
    /// </summary>
    public bool TryStartFactoryReset(out string error)
        => TryStartCore(version: "", factoryReset: true, out error);

    private bool TryStartCore(string version, bool factoryReset, out string error)
    {
        var device = _deviceRegistry.TryGet(QshellPackage);
        if (device is null)
        {
            error = "No Q-series panel is connected.";
            return false;
        }

        if (!_flashGate.TryAcquire(out error))
        {
            return false;
        }

        var status = _flashGate.Status;
        status.DeviceType = DeviceTypeKey;
        status.Version = version;
        status.Phase = "preparing";
        status.Percent = 0;
        status.Message = "Preparing...";
        status.Success = false;
        status.Error = "";
        _ = Task.Run(() => RunAsync(device, factoryReset, CancellationToken.None));
        return true;
    }

    private async Task RunAsync(IAdbDeviceTarget device, bool factoryReset, CancellationToken ct)
    {
        try
        {
            Set("preparing", 5, "Preparing...");

            var dumpsys = await device.ShellAsync("dumpsys package " + QshellPackage, ct);
            var installedVersionCode = AdbHelpers.ParseVersionCode(dumpsys);

            // Invalidate cache so the flash reads a fresh manifest.
            _cacheExpiry = DateTime.MinValue;
            var latest = await _fetchManifest(ct);
            if (latest is null)
            {
                Fail("Could not fetch the qshell manifest.");
                return;
            }

#if !DEV_TOOLS
            // A factory reset reinstalls the same build, so the manual flash's
            // already-up-to-date gate must not apply.
            if (!factoryReset && latest.VersionCode is int pub && installedVersionCode >= pub)
            {
                Fail("The installed qshell version is already up to date.");
                return;
            }
#endif

            Set("downloading", 40, "Downloading APK...");
            var apkPath = await _downloadApk(ct);
            if (apkPath is null)
            {
                Fail("APK download failed.");
                return;
            }

            Set("installing", 70, "Installing on panel...");
            var adbPath = AdbLocator.ResolveAdbPath();
            if (adbPath is null)
            {
                Fail("adb not found.");
                return;
            }

            // Suppress escalation reboots in QSeriesPortWatcher while the install runs.
            device.InstallInProgress = true;
            bool installSuccess;
            string installOutput;
            bool signatureRecovery;
            try
            {
                if (factoryReset)
                {
                    // Dropping qshell's data dir is what wipes the panel; the reinstall
                    // then comes up on first-run defaults. Held inside InstallInProgress
                    // with the install so no tick slips an adb pass between the two,
                    // which would find the panel with no launcher.
                    Set("wiping", 55, "Wiping panel data...");
                    var uninstall = _uninstallRunner ?? AdbHelpers.RunAdbUninstall;
                    var (unOk, unOut) = uninstall(adbPath, device.Serial, QshellPackage);
                    // A transport that blinked between the presence check and here
                    // reports "device not found", which is neither success nor a
                    // real uninstall failure. Wait it out and try once more.
                    if (!unOk && IsDeviceMissing(unOut) && await WaitForDeviceAsync(device, ct))
                    {
                        (unOk, unOut) = uninstall(adbPath, device.Serial, QshellPackage);
                    }
                    if (!unOk)
                    {
                        // Already absent: the reinstall below still restores the panel.
                        if (!unOut.Contains("not installed", StringComparison.OrdinalIgnoreCase)
                            && !unOut.Contains("Unknown package", StringComparison.OrdinalIgnoreCase))
                        {
                            Fail($"Uninstall failed: {unOut}");
                            return;
                        }
                        ServiceLog.Warn($"[apk-flash] factory reset: {QshellPackage} was not installed on {device.Serial}; reinstalling");
                    }

                    Set("wiping", 60, "Waiting for panel...");
                    if (!await WaitForDeviceAsync(device, ct))
                    {
                        Fail("The panel did not come back after the wipe. Its app is uninstalled - reconnect the panel and run the panel-app install to restore it.");
                        return;
                    }
                }

                installSuccess = false;
                installOutput = "";
                signatureRecovery = false;
                var installDeadline = DateTime.UtcNow + InstallPhaseBudget;
                for (var attempt = 1; attempt <= InstallDeviceLossAttempts; attempt++)
                {
                    (installSuccess, installOutput, signatureRecovery) =
                        AdbHelpers.InstallWithSignatureRecovery(adbPath, device.Serial, apkPath, QshellPackage, _installRunner, _uninstallRunner);
                    if (installSuccess || !IsDeviceMissing(installOutput)) break;

                    // The panel re-enumerates several times after the wipe (observed
                    // transport id 2 -> 3 -> 4 on the Q60), so a transport that was
                    // live when the wait returned can be gone again by the install.
                    // Re-wait and retry rather than failing on a window we can outlast.
                    if (DateTime.UtcNow >= installDeadline)
                    {
                        ServiceLog.Warn($"[apk-flash] {device.Serial}: install phase budget spent; giving up");
                        break;
                    }
                    ServiceLog.Warn(
                        $"[apk-flash] {device.Serial}: install attempt {attempt}/{InstallDeviceLossAttempts} lost the device; re-waiting");
                    Set("installing", 68, "Waiting for panel...");
                    if (!await WaitForDeviceAsync(device, ct)) break;
                    Set("installing", 70, "Installing on panel...");
                }
                if (signatureRecovery)
                {
                    // Signing cert changed; recovery uninstalled the old build and retried.
                    Set("installing", 73, "Signature changed; reinstalling...");
                    ServiceLog.Warn($"[apk-flash] cert conflict on {device.Serial}; uninstalled {QshellPackage} and retried");
                }
            }
            finally
            {
                device.InstallInProgress = false;
            }

            if (!installSuccess)
            {
                // The wipe already removed qshell, so a failure here leaves the
                // panel launcher-less; say how to get it back rather than only
                // reporting the adb output.
                Fail(factoryReset
                    ? $"Install failed after the wipe: {installOutput}. The panel's app is uninstalled - reconnect the panel and run the panel-app install to restore it."
                    : $"Install failed: {installOutput}");
                return;
            }

            // cmd package set-home-activity prints "Success" on API 30+ (empty on
            // some builds); a real failure prints an error (e.g. the APK declares no
            // HOME intent). Treat Success/empty as ok, any other output as failure.
            Set("activating", 78, "Setting as default launcher...");
            var setHomeOut = (await device.ShellAsync(SetHomeCmd + QshellComponent, ct)).Trim();
            if (setHomeOut.Length > 0 && !setHomeOut.Contains("Success", StringComparison.OrdinalIgnoreCase))
            {
                Fail($"set-home-activity failed: {setHomeOut}");
                return;
            }
            // Switch the panel to the new HOME immediately.
            await device.ShellAsync(GoHomeCmd, ct);

            Set("verifying", 85, "Verifying...");
            var dumpsys2 = await device.ShellAsync("dumpsys package " + QshellPackage, ct);
            // Skipped when the manifest has no versionCode; the install is assumed correct.
            if (latest.VersionCode is int published && AdbHelpers.ParseVersionCode(dumpsys2) != published)
            {
                Fail("Version mismatch after install.");
                return;
            }
            // Best-effort: confirm qshell is the resolved HOME. Not fatal if the check is unreliable on-device.
            var resolveOut = await device.ShellAsync(ResolveHomeCmd, ct);
            if (!resolveOut.Contains(QshellPackage, StringComparison.Ordinal))
            {
                ServiceLog.Warn($"[apk-flash] resolve-activity did not confirm qshell as HOME: {resolveOut.Trim()}");
            }

            _flashGate.Status.Success = true;
            Set("done", 100, factoryReset ? "Factory reset complete." : $"Updated to {latest.Version}.");
        }
        catch (Exception ex)
        {
            Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _flashGate.Release();
        }
    }

    /// <summary>Bounds the post-wipe wait for the panel's adb shell to answer again.</summary>
    internal static readonly TimeSpan DeviceReturnTimeout = TimeSpan.FromSeconds(120);
    /// <summary>Gap between readiness polls. Test seam: shortened so the fake device does not pace the suite.</summary>
    internal static TimeSpan DeviceReturnPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Consecutive answering polls required before the panel counts as back. One
    /// answer is not enough: the panel re-enumerates repeatedly after the wipe, so
    /// a single reply can come from a transport that is about to disappear again.
    /// </summary>
    private const int DeviceReturnStableReads = 3;

    /// <summary>Install attempts that tolerate the device vanishing mid-install.</summary>
    private const int InstallDeviceLossAttempts = 3;

    /// <summary>
    /// Ceiling on the whole install phase. Each attempt already nests adb's own
    /// retries and a device wait, so without this a panel that never returns
    /// holds <see cref="FlashGate"/> for tens of minutes with no cancel path.
    /// </summary>
    private static readonly TimeSpan InstallPhaseBudget = TimeSpan.FromMinutes(8);

    /// <summary>True when adb failed because the device was not in its list.</summary>
    internal static bool IsDeviceMissing(string output) =>
        output.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || output.Contains("device offline", StringComparison.OrdinalIgnoreCase)
        || output.Contains("no devices", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Polls the panel's adb shell until it answers
    /// <see cref="DeviceReturnStableReads"/> times running, or
    /// <see cref="DeviceReturnTimeout"/> elapses. <c>ShellAsync</c> returns empty
    /// for a device absent from the adb list, so a distinctive echo separates
    /// "back" from "still gone"; any miss resets the streak.
    /// </summary>
    private static async Task<bool> WaitForDeviceAsync(IAdbDeviceTarget device, CancellationToken ct)
    {
        const string marker = "nexus-panel-ready";
        var deadline = DateTime.UtcNow + DeviceReturnTimeout;
        var streak = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var ok = false;
            try
            {
                var echo = await device.ShellAsync("echo " + marker, ct);
                ok = echo.Contains(marker, StringComparison.Ordinal);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* transport still re-enumerating */ }

            streak = ok ? streak + 1 : 0;
            if (streak >= DeviceReturnStableReads) return true;
            await Task.Delay(DeviceReturnPollInterval, ct);
        }
        ServiceLog.Warn($"[apk-flash] {device.Serial}: panel did not stay on adb within {DeviceReturnTimeout.TotalSeconds:F0}s");
        return false;
    }

    private void Set(string phase, int percent, string message)
    {
        var status = _flashGate.Status;
        status.Phase = phase;
        status.Percent = percent;
        status.Message = message;
    }

    private void Fail(string error)
    {
        var status = _flashGate.Status;
        status.Phase = "failed";
        status.Success = false;
        status.Error = error;
        status.Message = error;
    }
}
