using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.LianLiCp;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiTl;

public sealed class TlFanConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int RpmPollMs = 2000;
    private const int MaxConsecutiveFailures = 3;

    private readonly IHidEnumerator _hid;
    private readonly TlFanHub _hub;
    private readonly LianLiTlCoolingProvider _cooling;
    private readonly DeviceControlGate _gate;

    public TlFanConnectionWorker(IHidEnumerator hid, TlFanHub hub, LianLiTlCoolingProvider cooling, DeviceControlGate gate)
    {
        _hid = hid;
        _hub = hub;
        _cooling = cooling;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled("lianli-tl"))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var device = FindAndOpen();
                if (device != null)
                {
                    _hub.Attach(device);
                    bool started = false;
                    try
                    {
                        if (_hub.DiscoverFans())
                        {
                            started = true;
                            // untested - verification pending
                            ServiceLog.Info("[lianli-tl] connected");
                            int failures = 0;
                            while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled("lianli-tl"))
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
                        // Detach always runs when Attach was called, even if DiscoverFans throws.
                        _hub.Detach();
                        if (started)
                        {
                            ServiceLog.Info("[lianli-tl] disconnected");
                        }
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
                ServiceLog.Error($"[lianli-tl] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private IHidDevice? FindAndOpen()
    {
        var infos = _hid.Find(TlFanProtocol.VendorId, TlFanProtocol.ProductId);

        // Select the interface that supports both output (Write) and input (Read).
        // If multiple qualify, prefer the one with the largest InputReportByteLength.
        // Verification pending: no hardware available to confirm interface selection.
        HidDeviceInfo? best = null;
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

        if (best == null)
        {
            return null;
        }

        // forInput=true for overlapped I/O so Read honors its timeout on Windows.
        return _hid.Open(best.Path, forInput: true);
    }
}
