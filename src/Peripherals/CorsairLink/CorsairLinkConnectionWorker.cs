using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>
/// Discovers the iCUE LINK System Hub, opens its command interface, takes the hub
/// into software mode, and polls speed/temperature telemetry while connected.
/// </summary>
public sealed class CorsairLinkConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int PollMs = 2000;
    private const int MaxConsecutiveFailures = 3;

    private readonly IHidEnumerator _hid;
    private readonly CorsairLinkHub _hub;
    private readonly CorsairLinkLightingDeviceProvider _lighting;
    private readonly CorsairLinkCoolingProvider _cooling;
    private readonly CorsairLinkLcd _lcd;
    private readonly DeviceControlGate _gate;

    public CorsairLinkConnectionWorker(
        IHidEnumerator hid,
        CorsairLinkHub hub,
        CorsairLinkLightingDeviceProvider lighting,
        CorsairLinkCoolingProvider cooling,
        CorsairLinkLcd lcd,
        DeviceControlGate gate)
    {
        _hid = hid;
        _hub = hub;
        _lighting = lighting;
        _cooling = cooling;
        _lcd = lcd;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled("corsair"))
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

                if (!_hub.Initialize())
                {
                    // Detach clears Firmware, so read it before. Empty means the
                    // firmware read itself never landed a full reply - the hub went
                    // silent (or answered short) before the device-list read.
                    var fw = _hub.State.Firmware;
                    ServiceLog.Warn($"[corsair] initialize failed, retrying (fw={(string.IsNullOrEmpty(fw) ? "none" : fw)})");
                    _hub.Detach();
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                ServiceLog.Info($"[corsair] connected fw={_hub.State.Firmware} devices={_hub.State.Devices.Count}");
                if (_hub.State.HasLcd)
                {
                    _lcd.DiscoverAndAttach(_hub.State.Devices, _hid);
                }
                _lighting.OnHubStateUpdated();
                _cooling.ReassertControl();
                try
                {
                    var failures = 0;
                    while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled("corsair"))
                    {
                        if (_hub.Poll())
                        {
                            failures = 0;
                            _cooling.ReassertControl();
                        }
                        else
                        {
                            failures++;
                            if (failures >= MaxConsecutiveFailures) break;
                        }
                        _lighting.OnHubStateUpdated();
                        await Task.Delay(PollMs, stoppingToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _lcd.Detach();
                    _hub.Detach();
                    ServiceLog.Info("[corsair] disconnected");
                    _lighting.OnHubStateUpdated();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[corsair] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private IHidDevice? FindAndOpen()
    {
        var infos = _hid.Find(CorsairLinkProtocol.VendorId, CorsairLinkProtocol.ProductId);
        foreach (var info in infos)
        {
            if (info.UsagePage == CorsairLinkProtocol.VendorUsagePage
                && info.Usage == CorsairLinkProtocol.VendorUsage)
            {
                // forInput: interrupt-IN reads (the hub answers every write with a report).
                return _hid.Open(info.Path, forInput: true);
            }
        }
        return null;
    }
}
