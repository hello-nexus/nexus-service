using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.LianLiCp;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Galahad2;

public sealed class Galahad2ConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int RpmPollMs = 2000;
    private const int MaxConsecutiveFailures = 3;

    private readonly IHidEnumerator _hid;
    private readonly Galahad2Hub _hub;
    private readonly Galahad2LightingDeviceProvider _lighting;
    private readonly Galahad2CoolingProvider _cooling;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;

    public Galahad2ConnectionWorker(IHidEnumerator hid, Galahad2Hub hub, Galahad2LightingDeviceProvider lighting, Galahad2CoolingProvider cooling, DeviceControlGate gate, HardwarePresence presence)
    {
        _hid = hid;
        _hub = hub;
        _lighting = lighting;
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
                if (!_gate.IsEnabled("lianli-aio"))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!_presence.UsbPresent(Galahad2Protocol.VendorId, Galahad2Protocol.ProductIdPerformance, Galahad2Protocol.ProductIdRegular))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var device = FindAndOpen(out var pid);
                if (device != null)
                {
                    _hub.Attach(device, pid);
                    bool started = false;
                    try
                    {
                        if (_hub.Connect())
                        {
                            started = true;
                            ServiceLog.Info("[lianli-aio] connected");
                            _lighting.OnHubStateUpdated();
                            int failures = 0;
                            while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled("lianli-aio"))
                            {
                                if (_hub.PollRpm())
                                {
                                    failures = 0;
                                    _cooling.ReassertControl();
                                }
                                else
                                {
                                    failures++;
                                    if (failures >= MaxConsecutiveFailures)
                                    {
                                        break;
                                    }
                                }
                                _lighting.OnHubStateUpdated();
                                await Task.Delay(RpmPollMs, stoppingToken).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        // Detach always runs when Attach was called, even if Connect throws.
                        _hub.Detach();
                        if (started)
                        {
                            ServiceLog.Info("[lianli-aio] disconnected");
                        }
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
                ServiceLog.Error($"[lianli-aio] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private IHidDevice? FindAndOpen(out int productId)
    {
        productId = 0;
        int[] pids = { Galahad2Protocol.ProductIdPerformance, Galahad2Protocol.ProductIdRegular };
        HidDeviceInfo? best = null;
        foreach (int tryPid in pids)
        {
            var infos = _hid.Find(Galahad2Protocol.VendorId, tryPid);
            foreach (var info in infos)
            {
                if (info.OutputReportByteLength < CommandPacket.Length || info.InputReportByteLength <= 0)
                {
                    continue;
                }
                if (best == null || info.InputReportByteLength > best.InputReportByteLength)
                {
                    best = info;
                }
            }
        }
        if (best == null)
        {
            return null;
        }
        productId = best.ProductId;
        // forInput=true for overlapped I/O so Read honors its timeout on Windows.
        return _hid.Open(best.Path, forInput: true);
    }
}
