using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>
/// Opens the RGB & Fan Controller's HID interface next to OpenRGB's handle and keeps the
/// fan status flowing. Fans only: no lighting report is ever written.
/// </summary>
public sealed class NzxtFanControllerConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int MaxConsecutiveFailures = 3;

    // Smallest report that carries a fan command; the device's other HID collections are shorter.
    private const int MinUsableReportLength = NzxtFanControllerProtocol.ReportLength;

    private readonly IHidEnumerator _hid;
    private readonly NzxtFanControllerHub _hub;
    private readonly NzxtFanControllerCoolingProvider _cooling;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;

    public NzxtFanControllerConnectionWorker(
        IHidEnumerator hid,
        NzxtFanControllerHub hub,
        NzxtFanControllerCoolingProvider cooling,
        DeviceControlGate gate,
        HardwarePresence presence)
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
                if (!_gate.IsEnabled(NzxtFanControllerHub.DeviceId)
                    || !_presence.UsbPresent(NzxtFanControllerProtocol.VendorId, NzxtFanControllerProtocol.ProductId))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var device = FindAndOpen();
                if (device == null)
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                _hub.Attach(device);
                bool started = false;
                try
                {
                    if (_hub.Connect())
                    {
                        started = true;
                        ServiceLog.Info($"[nzxt-fan-controller] connected: {_hub.Fans.Length} fan channel(s)");
                        int failures = 0;
                        while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled(NzxtFanControllerHub.DeviceId))
                        {
                            if (_hub.Poll())
                            {
                                failures = 0;
                                _cooling.ReassertControl();
                            }
                            else if (++failures >= MaxConsecutiveFailures)
                            {
                                break;
                            }
                        }
                    }
                    else
                    {
                        await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _hub.Detach();
                    if (started)
                    {
                        ServiceLog.Info("[nzxt-fan-controller] disconnected");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[nzxt-fan-controller] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private IHidDevice? FindAndOpen()
    {
        HidDeviceInfo? best = null;
        foreach (var info in _hid.Find(NzxtFanControllerProtocol.VendorId, NzxtFanControllerProtocol.ProductId))
        {
            if (info.OutputReportByteLength < MinUsableReportLength
                || info.InputReportByteLength < MinUsableReportLength)
            {
                continue;
            }
            if (best == null || info.InputReportByteLength > best.InputReportByteLength)
            {
                best = info;
            }
        }
        // forInput so Read honors its timeout instead of busy-spinning on Windows.
        return best == null ? null : _hid.Open(best.Path, forInput: true);
    }
}
