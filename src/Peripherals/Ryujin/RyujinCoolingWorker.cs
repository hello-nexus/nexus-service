using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Ryujin;

/// <summary>
/// Keeps the Ryujin III Extreme's cooler channel open while its Nexus Control toggle is on, and
/// runs Armoury Crate's cycle: resend the duty, then request status and duty. Independent of
/// the LCD worker, so an LCD that never opens does not take pump and fan control with it.
/// </summary>
public sealed class RyujinCoolingWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int MaxConsecutiveFailures = 3;

    private readonly IHidEnumerator _hid;
    private readonly RyujinCoolingHub _hub;
    private readonly RyujinCoolingProvider _cooling;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;

    public RyujinCoolingWorker(
        IHidEnumerator hid, RyujinCoolingHub hub, RyujinCoolingProvider cooling, DeviceControlGate gate, HardwarePresence presence)
    {
        _hid = hid;
        _hub = hub;
        _cooling = cooling;
        _gate = gate;
        _presence = presence;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled(RyujinCoolingProtocol.HandlerId)
                    || !_presence.UsbPresent(RyujinCoolingProtocol.VendorId, RyujinCoolingProtocol.ProductId))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var device = FindAndOpen();
                if (device is null)
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                _hub.Attach(device);
                ServiceLog.Info("[asus-ryujin-cooling] connected");
                try
                {
                    int failures = 0;
                    while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled(RyujinCoolingProtocol.HandlerId))
                    {
                        _cooling.ReassertControl();
                        if (_hub.Poll())
                        {
                            failures = 0;
                        }
                        else if (++failures >= MaxConsecutiveFailures)
                        {
                            break;
                        }
                        await Task.Delay(RyujinCoolingProtocol.KeepaliveMs, stoppingToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    // Best effort: a held channel goes back to the safe duty before the handle closes.
                    try { _cooling.ReleaseAll(); }
                    catch (Exception ex) { ServiceLog.Warn($"[asus-ryujin-cooling] release on disconnect failed: {ex.Message}"); }
                    _hub.Detach();
                    ServiceLog.Info("[asus-ryujin-cooling] disconnected");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[asus-ryujin-cooling] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private IHidDevice? FindAndOpen()
    {
        HidDeviceInfo? best = null;
        foreach (var info in _hid.Find(RyujinCoolingProtocol.VendorId, RyujinCoolingProtocol.ProductId))
        {
            if (info.OutputReportByteLength < RyujinCoolingProtocol.ReportLength || info.InputReportByteLength <= 0)
            {
                continue;
            }
            if (best is null || info.OutputReportByteLength > best.OutputReportByteLength)
            {
                best = info;
            }
        }
        // Overlapped so Read honors its timeout on Windows.
        return best is null ? null : _hid.Open(best.Path, forInput: true);
    }
}
