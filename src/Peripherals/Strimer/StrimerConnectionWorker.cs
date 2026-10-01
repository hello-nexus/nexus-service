using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Strimer;

public sealed class StrimerConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int MaxConsecutiveFailures = 3;

    private readonly IHidEnumerator _hid;
    private readonly StrimerHub _hub;
    private readonly StrimerLightingDeviceProvider _lighting;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;
    private bool _firstAttach;

    public StrimerConnectionWorker(IHidEnumerator hid, StrimerHub hub, StrimerLightingDeviceProvider lighting, DeviceControlGate gate, HardwarePresence presence)
    {
        _hid = hid;
        _hub = hub;
        _lighting = lighting;
        _gate = gate;
        _presence = presence;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled("strimer"))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!_presence.UsbPresent(StrimerProtocol.VendorId, StrimerProtocol.ProductId))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var device = FindAndOpen();
                if (device != null)
                {
                    _hub.Attach(device);
                    if (!_firstAttach)
                    {
                        _firstAttach = true;
                        ServiceLog.Info("[strimer] attached (untested - verification pending)");
                    }
                    ServiceLog.Info("[strimer] connected");
                    _lighting.OnHubStateUpdated();
                    try
                    {
                        while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled("strimer"))
                        {
                            await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                            if (_hub.ConsecutiveWriteFailures >= MaxConsecutiveFailures)
                            {
                                break;
                            }
                        }
                    }
                    finally
                    {
                        _hub.Detach();
                        ServiceLog.Info("[strimer] disconnected");
                        _lighting.OnHubStateUpdated();
                    }
                }
                else
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[strimer] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private IHidDevice? FindAndOpen()
    {
        var infos = _hid.Find(StrimerProtocol.VendorId, StrimerProtocol.ProductId);
        foreach (var info in infos)
        {
            if (info.UsagePage == StrimerProtocol.VendorUsagePage
                && info.Usage == StrimerProtocol.VendorUsage)
            {
                var device = _hid.Open(info.Path);
                if (device != null) return device;
            }
        }
        return null;
    }
}
