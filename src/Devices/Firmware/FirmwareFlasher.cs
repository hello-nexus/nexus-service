using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Devices;
using Nexus.Service.Platform;
using Nexus.Service.Plugins;

namespace Nexus.Service.Devices.Firmware;

/// <summary>
/// Orchestrates a full firmware flash, encoding the sequence proven on hardware
/// (CNVS, 2026-05-28): ensure WinUSB driver -> in-app DFU entry (hub writes key +
/// magic, releases its COM port) -> wait for the bootloader to enumerate as
/// 3402:0a00 -> dfu-util download at 0x0800C000 -> readback-verify -> flag-erase
/// (0xFF @ 0x0801FFF0) + leave. One flash at a time via <see cref="FlashGate"/>;
/// progress lives in <see cref="Status"/> so the UI survives navigation, and
/// <see cref="IsFlashing"/> blocks app shutdown.
/// </summary>
public sealed class FirmwareFlasher
{
    private readonly BundledFirmwareCatalog _catalog;
    private readonly IReadOnlyList<IDfuFlashTarget> _targets;
    private readonly WinUsbDriverInstaller _winusb;
    private readonly DfuUtil _dfu;
    private readonly PluginProviderRegistry _registry;
    private readonly FlashGate _flashGate;
    private readonly DfuRecoveryMonitor _recovery;

    public FirmwareFlasher(
        BundledFirmwareCatalog catalog,
        IEnumerable<IDfuFlashTarget> targets,
        WinUsbDriverInstaller winusb,
        DfuUtil dfu,
        PluginProviderRegistry registry,
        FlashGate flashGate,
        DfuRecoveryMonitor recovery)
    {
        _catalog = catalog;
        _targets = targets.ToList();
        _winusb = winusb;
        _dfu = dfu;
        _registry = registry;
        _flashGate = flashGate;
        _recovery = recovery;
    }

    // First-party DFU targets (static DI) + any plugin targets (registry snapshot,
    // read fresh each call). Firmware grants stay first-party-only at v1; a plugin
    // target only reaches here once the cert grants it (Phase 3).
    private IEnumerable<IDfuFlashTarget> AllTargets => _targets.Concat(_registry.DfuTargets);

    /// <summary>Shared status object; also written by ApkFlasher.</summary>
    public FlashStatusDto Status => _flashGate.Status;

    /// <summary>True while either a DFU flash or an APK flash is running.</summary>
    public bool IsFlashing => _flashGate.IsFlashing;

    /// <summary>
    /// Every image the connected device for <paramref name="connectedFirmwareType"/>
    /// can be flashed with, including sibling-variant images (a Gen1 CNVS can also
    /// take the Gen2 image). Powers the dev-only cross-branch picker. Empty if no
    /// connected target handles that key.
    /// </summary>
    public IReadOnlyList<FlashableImage> FlashableImages(string connectedFirmwareType)
    {
        var result = new List<FlashableImage>();
        if (string.IsNullOrEmpty(connectedFirmwareType)) return result;
        var target = AllTargets.FirstOrDefault(t => t.IsConnected && t.FirmwareType == connectedFirmwareType);
        if (target is null) return result;
        foreach (var key in _catalog.DeviceIds)
        {
            if (!target.CanFlash(key)) continue;
            foreach (var v in _catalog.GetAvailableVersions(key))
                result.Add(new FlashableImage { FirmwareType = key, Version = v });
        }
        return result;
    }

    /// <summary>
    /// Begin a flash on a background task. Returns false (with a reason) if one
    /// is already running, the image isn't bundled, or no connected device can
    /// take it. Validates everything up front so the caller gets immediate feedback.
    /// </summary>
    public bool TryStart(string deviceType, string version, out string error)
    {
        if (string.IsNullOrWhiteSpace(deviceType) || string.IsNullOrWhiteSpace(version))
        {
            error = "deviceType and version are required.";
            return false;
        }
        if (!_catalog.GetAvailableVersions(deviceType).Contains(version))
        {
            error = $"No bundled firmware for {deviceType} {version}.";
            return false;
        }
        var target = AllTargets.FirstOrDefault(t => t.IsConnected && t.CanFlash(deviceType));
        if (target is null)
        {
            error = $"No connected device can flash {deviceType}.";
            return false;
        }

#if !DEV_TOOLS
        // Release builds permit upgrades only: the connected variant's latest
        // bundled image. Cross-variant and downgrade / re-flash are dev-tools-
        // only (brick risk) and gated out of release - see the DEV_TOOLS define.
        if (deviceType != target.FirmwareType)
        {
            error = "Cross-variant flashing is not permitted in this build.";
            return false;
        }
        if (version != _catalog.GetLatestVersion(deviceType))
        {
            error = "Only the latest firmware version can be installed.";
            return false;
        }
#endif

        // A second device in DFU makes the bus ambiguous: this one would enter DFU
        // and then be refused, stranding it too.
        if (_recovery.AnyInDfu())
        {
            error = "A device is already in update mode. Recover it first.";
            return false;
        }

        if (!_flashGate.TryAcquire(out error))
        {
            return false;
        }

        Begin(deviceType, version);
        _ = Task.Run(() => RunAsync(deviceType, version, target, expectedProductId: null));
        return true;
    }

    /// <summary>
    /// Re-flash the device <see cref="DfuRecoveryMonitor"/> found stranded in its
    /// bootloader. Same sequence as <see cref="TryStart"/> minus the in-app DFU entry,
    /// plus a re-read of the product key once the bus is confirmed to hold one device.
    /// Release builds only take the key-identified variant's latest image.
    /// </summary>
    public bool TryStartRecovery(string deviceType, string version, out string error)
    {
        if (string.IsNullOrWhiteSpace(deviceType) || string.IsNullOrWhiteSpace(version))
        {
            error = "deviceType and version are required.";
            return false;
        }
        if (!_catalog.GetAvailableVersions(deviceType).Contains(version))
        {
            error = $"No bundled firmware for {deviceType} {version}.";
            return false;
        }
        var stranded = _recovery.Current;
        if (stranded is null || stranded.State == StrandedDfuState.Identifying)
        {
            error = "No identified device is waiting for recovery.";
            return false;
        }

#if !DEV_TOOLS
        if (stranded.State != StrandedDfuState.Ready || stranded.Identity?.FirmwareType != deviceType)
        {
            error = "This device cannot be recovered automatically.";
            return false;
        }
        if (version != _catalog.GetLatestVersion(deviceType))
        {
            error = "Only the latest firmware version can be installed.";
            return false;
        }
#endif

        if (!_flashGate.TryAcquire(out error))
        {
            return false;
        }

        ServiceLog.Info($"[firmware] recovery requested for {stranded.Serial} " +
                        $"(key {(stranded.ProductId is int p ? $"0x{p:X4}" : "none")}) -> {deviceType} {version}");
        Begin(deviceType, version);
        _ = Task.Run(() => RunAsync(deviceType, version, target: null, expectedProductId: stranded.ProductId));
        return true;
    }

    private void Begin(string deviceType, string version)
    {
        Status.DeviceType = deviceType;
        Status.Version = version;
        Status.Phase = "preparing";
        Status.Percent = 0;
        Status.Message = "Preparing…";
        Status.Success = false;
        Status.Error = "";
    }

    // target null = recovery: the device is already in DFU. expectedProductId is the
    // key it was identified by; the key is re-read so a swapped board is never flashed.
    private async Task RunAsync(string deviceType, string version, IDfuFlashTarget? target, int? expectedProductId)
    {
        string? binPath = null;
        string? flagPath = null;
        string? readbackPath = null;
        await _recovery.AcquireBusAsync();
        try
        {
            // 1. Resolve + convert the bundled image to a flat bin.
            Set("preparing", 5, "Reading firmware image…");
            byte[] bin;
            using (var hex = _catalog.OpenFirmware(deviceType, version)
                   ?? throw new InvalidOperationException($"firmware {deviceType}/{version} missing"))
            {
                bin = IntelHex.Parse(hex).Data;
            }
            binPath = Path.Combine(Path.GetTempPath(), $"nexus-fw-{deviceType}-{version}.bin");
            await File.WriteAllBytesAsync(binPath, bin);

            // 2. Stage the WinUSB driver so the DFU device is openable.
            Set("preparing", 10, "Preparing USB driver…");
            await _winusb.EnsureInstalledAsync(CancellationToken.None);

            // 3. In-app DFU entry (hub writes key+magic, releases its COM port).
            //    Recovery skips it: the device is already in the bootloader.
            if (target is not null)
            {
                Set("entering-dfu", 15, "Switching device to update mode…");
                if (!target.EnterDfuMode())
                {
                    Fail("Could not switch the device into update mode.");
                    return;
                }
            }

            // 4. Wait for the bootloader to enumerate as 3402:0a00.
            Set("waiting-dfu", 25, "Waiting for bootloader…");
            var dfuError = await WaitForDfuAsync(TimeSpan.FromSeconds(20));
            if (dfuError is not null)
            {
                Fail(dfuError);
                return;
            }

            if (target is null && expectedProductId is int expected)
            {
                Set("waiting-dfu", 30, "Checking device…");
                var slot = await _recovery.ReadKeySlotAsync(CancellationToken.None);
                var pid = slot is null ? null : DfuProductKey.ParseProductId(slot);
                if (pid != expected)
                {
                    // Re-identify on the next tick rather than retrying against this stale key.
                    _recovery.Clear();
                    Fail(slot is null ? "Could not read the device in update mode. Try again." : "The device in update mode changed. Try again.");
                    return;
                }
            }

            // 5. Download the app image at 0x0800C000 (no leave yet).
            Set("downloading", 40, "Writing firmware…");
            var dl = await _dfu.DownloadAsync(binPath, DfuUtil.AppBaseAddress, leave: false, CancellationToken.None);
            if (!dl.Success) { Fail($"Flash failed: {Tail(dl.Output)}", dl.Output); return; }

            // 6. Read back + verify byte-for-byte.
            Set("verifying", 70, "Verifying…");
            readbackPath = Path.Combine(Path.GetTempPath(), $"nexus-fw-{deviceType}-readback.bin");
            var up = await _dfu.UploadAsync(readbackPath, DfuUtil.AppBaseAddress, bin.Length, CancellationToken.None);
            if (!up.Success) { Fail($"Verify read-back failed: {Tail(up.Output)}", up.Output); return; }
            var readback = await File.ReadAllBytesAsync(readbackPath);
            if (!readback.AsSpan().SequenceEqual(bin))
            {
                Fail("Verification mismatch - flashed image does not match the source.");
                return;
            }

            // 7. Erase the boot flag (0xFF @ 0x0801FFF0) and read it back: a flag
            //    left at 0xDD keeps the device in the bootloader on every boot.
            Set("finalizing", 90, "Finalizing…");
            flagPath = Path.Combine(Path.GetTempPath(), "nexus-fw-flag.bin");
            await File.WriteAllBytesAsync(flagPath, Enumerable.Repeat((byte)0xFF, DfuProductKey.SlotLength).ToArray());
            var fl = await _dfu.DownloadAsync(flagPath, DfuUtil.BootFlagAddress, leave: false, CancellationToken.None);
            if (!fl.Success) { Fail($"Clearing the update flag failed: {Tail(fl.Output)}", fl.Output); return; }
            // An unreadable slot is not proof of failure (older flows never read it),
            // so only a read that shows a surviving key stops the flash here.
            var flag = await _recovery.ReadKeySlotAsync(CancellationToken.None);
            if (flag is null)
            {
                ServiceLog.Warn($"[firmware] {deviceType}: update flag not read back; leaving anyway");
            }
            else if (flag.Take(DfuProductKey.SlotLength).Any(b => b != 0xFF))
            {
                Fail($"The update flag did not clear ({Convert.ToHexString(flag)}).");
                return;
            }

            // 8. Leave -> the bootloader resets into the app. dfu-util reports a
            //    get_status error on :leave (the device detaches before the final
            //    status read), so its exit code is not a failure signal.
            await _dfu.DownloadAsync(flagPath, DfuUtil.BootFlagAddress, leave: true, CancellationToken.None);

            if (target is null) _recovery.Clear();
            Status.Success = true;
            Set("done", 100, $"Updated to {version}.");
        }
        catch (Exception ex)
        {
            Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            TryDelete(binPath); TryDelete(flagPath); TryDelete(readbackPath);
            _recovery.ReleaseBus();
            _flashGate.Release();
        }
    }

    // Returns null once exactly one device is in DFU, else an error message.
    private async Task<string?> WaitForDfuAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var r = await _dfu.ListAsync(CancellationToken.None);
            var count = DfuUtil.CountDfuDevices(r.Output);
            if (count == 1) return null;
            // Refuse an ambiguous bus: with more than one device in DFU we
            // can't tell which board dfu-util will target, and flashing the
            // wrong one bricks it. Fail loudly rather than gamble.
            if (count > 1)
            {
                return $"{count} devices are in DFU mode; refusing to flash an ambiguous target. Disconnect all but one and retry.";
            }
            await Task.Delay(1000);
        }
        return "Device did not appear in update mode (DFU).";
    }

    private void Set(string phase, int percent, string message)
    {
        Status.Phase = phase;
        Status.Percent = percent;
        Status.Message = message;
        ServiceLog.Info($"[firmware] {Status.DeviceType} {Status.Version}: {phase} {percent}% {message}");
    }

    private void Fail(string error, string? dfuOutput = null)
    {
        Status.Phase = "failed";
        Status.Success = false;
        Status.Error = error;
        Status.Message = error;
        ServiceLog.Error($"[firmware] {Status.DeviceType} {Status.Version}: failed: {error}" +
                         (dfuOutput is null ? "" : $" (dfu-util: {DfuUtil.TailLines(dfuOutput, 6)})"));
    }

    private static string Tail(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var lines = s.TrimEnd().Split('\n');
        return lines[^1].Trim();
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
