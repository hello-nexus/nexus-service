using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Lighting;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Connects the SLV3 dongles and drives the hub on L-Connect's cadence
/// (MasterDevice.Run): <see cref="Slv3Hub.PollTick"/> every
/// <see cref="PollPeriodMs"/> and <see cref="Slv3Hub.DriveTick"/> on every
/// <see cref="DriveEveryTicks"/>th one.
/// </summary>
public sealed class Slv3ConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    // GetDev period measured on L-Connect 2.1.29; DriveEveryTicks of them is its once-a-second block.
    private const int PollPeriodMs = 336;
    private const int DriveEveryTicks = 3;
    private const int MaxConsecutiveFailures = 3;

    private readonly Slv3Hub _hub;
    private readonly Slv3LightingDeviceProvider _lighting;
    private readonly DeviceControlGate _gate;

    public Slv3ConnectionWorker(
        Slv3Hub hub, Slv3LightingDeviceProvider lighting, DeviceControlGate gate)
    {
        _hub = hub;
        _lighting = lighting;
        _gate = gate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled("lianli-wireless"))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (_hub.EnsureConnected())
                {
                    ServiceLog.Info("[lianli-wireless] connected");
                    _lighting.OnHubStateUpdated();
                    try
                    {
                        var failures = 0;
                        var tick = 0;
                        while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled("lianli-wireless"))
                        {
                            var ok = tick++ % DriveEveryTicks != 0 ? _hub.PollTick() : _hub.DriveTick();
                            if (ok)
                            {
                                failures = 0;
                            }
                            else
                            {
                                failures++;
                                if (failures >= MaxConsecutiveFailures)
                                {
                                    break;
                                }
                            }
                            // Picks up newly bound/unbound fan chains without
                            // waiting for the RgbBridge periodic poll. Cooling
                            // needs no call here: CurveEngine replays persisted
                            // manual duties as bound chains surface in
                            // GetFanChannels.
                            _lighting.OnHubStateUpdated();
                            await Task.Delay(PollPeriodMs, stoppingToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _hub.Disconnect();
                        ServiceLog.Info("[lianli-wireless] disconnected");
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
                ServiceLog.Error($"[lianli-wireless] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
