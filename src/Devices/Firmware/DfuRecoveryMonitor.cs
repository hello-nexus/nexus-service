using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Platform;

namespace Nexus.Service.Devices.Firmware;

/// <summary>ready = identified with a bundled image; unsupported = cannot be identified safely or no image.</summary>
public enum StrandedDfuState { Identifying, Ready, Unsupported }

/// <summary>A device sitting in its bootloader with no flash running - an interrupted update.</summary>
public sealed record StrandedDfuDevice(string Serial, StrandedDfuState State, int? ProductId, DfuDeviceIdentity? Identity, string Reason);

/// <summary>
/// Finds a HYTE device stranded in its DFU bootloader (3402:0A00 while no flash holds
/// the <see cref="FlashGate"/>) and identifies it from its boot-flag key so
/// <see cref="FirmwareFlasher"/> can re-flash it. Identification stages the bundled
/// WinUSB driver and reads 16 bytes with dfu-util; neither writes to the device.
/// </summary>
public sealed class DfuRecoveryMonitor : BackgroundService
{
    public const int DfuVendorId = 0x3402;
    public const int DfuProductId = 0x0A00;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    // A completed flash leaves 0A00 enumerated for a moment after the gate releases;
    // only a device that stays in DFU this long is treated as stranded.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryUnreadable = TimeSpan.FromSeconds(60);

    private readonly HardwarePresence _presence;
    private readonly FlashGate _gate;
    private readonly DfuUtil _dfu;
    private readonly WinUsbDriverInstaller _winusb;
    private readonly BundledFirmwareCatalog _catalog;

    // One dfu-util user at a time: identification here, or a flash in FirmwareFlasher.
    private readonly SemaphoreSlim _bus = new(1, 1);
    private volatile StrandedDfuDevice? _current;
    private DateTime? _firstSeenUtc;
    private DateTime _lastAttemptUtc;

    public DfuRecoveryMonitor(HardwarePresence presence, FlashGate gate, DfuUtil dfu,
        WinUsbDriverInstaller winusb, BundledFirmwareCatalog catalog)
    {
        _presence = presence;
        _gate = gate;
        _dfu = dfu;
        _winusb = winusb;
        _catalog = catalog;
    }

    /// <summary>
    /// The stranded device, or null when none is present. It stays reported while its
    /// recovery flash runs (a normal flash never starts with one present).
    /// </summary>
    public StrandedDfuDevice? Current => _current;

    /// <summary>Forget the device so the next tick identifies it afresh (after a recovery succeeds or its key re-read fails).</summary>
    public void Clear() => _current = null;

    /// <summary>Take the dfu-util bus; FirmwareFlasher holds it for a whole flash.</summary>
    public Task AcquireBusAsync() => _bus.WaitAsync();

    public void ReleaseBus() => _bus.Release();

    /// <summary>True when any device is enumerated in DFU mode right now, stranded or not.</summary>
    public bool AnyInDfu() => _presence.UsbPresent(DfuVendorId, DfuProductId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) { ServiceLog.Error($"[dfu-recovery] tick exception: {ex.GetType().Name}: {ex.Message}"); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (_gate.IsFlashing)
        {
            _firstSeenUtc = null;
            return;
        }

        var entry = _presence.UsbEntriesFor(DfuVendorId).FirstOrDefault(e => e.ProductId == DfuProductId);
        if (entry is null)
        {
            if (_current is not null) ServiceLog.Info($"[dfu-recovery] {_current.Serial} left update mode");
            _current = null;
            _firstSeenUtc = null;
            return;
        }

        var now = DateTime.UtcNow;
        _firstSeenUtc ??= now;
        if (now - _firstSeenUtc < SettleTime) return;

        var cur = _current;
        if (cur is not null && cur.Serial == entry.Serial)
        {
            if (cur.State == StrandedDfuState.Ready) return;
            if (cur.State == StrandedDfuState.Unsupported && (cur.ProductId is not null || now - _lastAttemptUtc < RetryUnreadable)) return;
        }

        if (!await _bus.WaitAsync(0, ct).ConfigureAwait(false)) return;
        StrandedDfuDevice c;
        try
        {
            if (_gate.IsFlashing) return;
            _lastAttemptUtc = now;
            ServiceLog.Info($"[dfu-recovery] device {entry.Serial} is in update mode with no flash running; identifying");
            _current = new StrandedDfuDevice(entry.Serial, StrandedDfuState.Identifying, null, null, "");
            c = await IdentifyAsync(entry.Serial, ct).ConfigureAwait(false);
            _current = c;
        }
        finally
        {
            _bus.Release();
        }
        ServiceLog.Info($"[dfu-recovery] {c.Serial}: state={c.State} pid={(c.ProductId is int p ? $"0x{p:X4}" : "-")} " +
                        $"firmware={c.Identity?.FirmwareType ?? "-"}{(c.Reason.Length > 0 ? $" reason={c.Reason}" : "")}");
    }

    private async Task<StrandedDfuDevice> IdentifyAsync(string serial, CancellationToken ct)
    {
        StrandedDfuDevice Unsupported(int? pid, string reason) => new(serial, StrandedDfuState.Unsupported, pid, null, reason);

        await _winusb.EnsureInstalledAsync(ct).ConfigureAwait(false);

        var count = await CountWhenOpenableAsync(ct).ConfigureAwait(false);
        if (count == 0) return Unsupported(null, "dfu-util cannot open the device");
        if (count > 1) return Unsupported(null, $"{count} devices are in update mode");

        var slot = await ReadKeySlotAsync(ct).ConfigureAwait(false);
        if (slot is null) return Unsupported(null, "key read failed");

        var pid = DfuProductKey.ParseProductId(slot);
        if (pid is null) return Unsupported(null, $"no product key ({Convert.ToHexString(slot)})");

        var identity = DfuProductKey.IdentityForProductId(pid.Value);
        if (identity is null) return Unsupported(pid, "unknown or ambiguous product key");
        if (string.IsNullOrEmpty(_catalog.GetLatestVersion(identity.FirmwareType)))
            return Unsupported(pid, $"no bundled image for {identity.FirmwareType}");

        return new StrandedDfuDevice(serial, StrandedDfuState.Ready, pid, identity, "");
    }

    // The driver binds a few seconds after pnputil returns, so poll the list.
    private async Task<int> CountWhenOpenableAsync(CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            var r = await _dfu.ListAsync(ct).ConfigureAwait(false);
            var n = DfuUtil.CountDfuDevices(r.Output);
            if (n > 0) return n;
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        return 0;
    }

    /// <summary>Read the 16-byte boot-flag slot, or null when dfu-util fails.</summary>
    public async Task<byte[]?> ReadKeySlotAsync(CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexus-dfu-key-{Guid.NewGuid():N}.bin");
        try
        {
            var r = await _dfu.UploadAsync(path, DfuUtil.BootFlagAddress, DfuProductKey.SlotLength, ct).ConfigureAwait(false);
            if (!r.Success || !File.Exists(path))
            {
                ServiceLog.Error($"[dfu-recovery] key read failed (exit {r.ExitCode}): {DfuUtil.TailLines(r.Output, 4)}");
                return null;
            }
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            return bytes.Length >= DfuProductKey.SlotLength ? bytes : null;
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
