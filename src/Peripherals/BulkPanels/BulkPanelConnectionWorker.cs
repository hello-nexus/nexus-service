using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// Opens one bulk-pipe panel while it is present and Nexus Control is on for it.
///
/// Most of these will never open on a stock machine: the bulk endpoints are reachable only
/// where Windows has bound WinUSB, and a cooler still owned by its vendor's driver
/// enumerates without being openable. That is logged once per attempt at Info, not treated
/// as an error.
/// </summary>
public sealed class BulkPanelConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int PresencePollMs = 2000;

    private readonly IHidEnumerator _hid;
    private readonly IBulkUsbPipeFactory _pipes;
    private readonly BulkPanelHub _hub;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;

    public BulkPanelConnectionWorker(
        IHidEnumerator hid, IBulkUsbPipeFactory pipes, BulkPanelHub hub, DeviceControlGate gate, HardwarePresence presence)
    {
        _hid = hid;
        _pipes = pipes;
        _hub = hub;
        _gate = gate;
        _presence = presence;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var driver = _hub.Driver;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled(driver.HandlerId))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!_presence.UsbPresent(driver.VendorId, driver.ProductIds))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!TryOpen(out var pipe, out var hid) || pipe is null)
                {
                    hid?.Dispose();
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!_hub.Attach(pipe, hid))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                ServiceLog.Info($"[{driver.HandlerId}] connected: {driver.Name}, {_hub.Width}x{_hub.Height}");
                try
                {
                    // An unplug fails no call the hub makes on its own, so presence is what ends the session.
                    while (!stoppingToken.IsCancellationRequested
                        && _gate.IsEnabled(driver.HandlerId)
                        && _hub.IsConnected
                        && _presence.UsbPresent(driver.VendorId, driver.ProductIds))
                    {
                        await Task.Delay(PresencePollMs, stoppingToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _hub.Detach();
                    ServiceLog.Info($"[{driver.HandlerId}] disconnected");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[{driver.HandlerId}] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
        _hub.Detach();
    }

    private bool TryOpen(out IBulkUsbPipe? pipe, out IHidDevice? hid)
    {
        pipe = null;
        hid = null;
        var driver = _hub.Driver;

        for (int i = 0; i < driver.ProductIds.Count; i++)
        {
            var pid = driver.ProductIds[i];
            if (driver.NeedsHidChannel)
            {
                // The commit report rides HID, so a panel that needs it is unusable
                // without both channels; open HID first and skip the bulk probe if absent.
                hid = OpenHid(driver.VendorId, pid);
                if (hid is null)
                {
                    continue;
                }
            }
            pipe = _pipes.Open(driver.VendorId, pid, driver.WritePipeId, driver.ReadPipeId);
            if (pipe is not null)
            {
                return true;
            }
            hid?.Dispose();
            hid = null;
        }
        return false;
    }

    private IHidDevice? OpenHid(int vendorId, int productId)
    {
        HidDeviceInfo? best = null;
        foreach (var info in _hid.Find(vendorId, productId))
        {
            if (best == null || info.OutputReportByteLength > best.OutputReportByteLength)
            {
                best = info;
            }
        }
        return best == null ? null : _hid.Open(best.Path);
    }
}
