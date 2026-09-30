using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Nzxt;

public sealed class KrakenConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int StatusPollMs = 1000;
    private const int MaxConsecutiveFailures = 3;

    private readonly IHidEnumerator _hid;
    private readonly KrakenHub _hub;
    private readonly KrakenLightingDeviceProvider _lighting;
    private readonly KrakenCoolingProvider _cooling;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;

    public KrakenConnectionWorker(
        IHidEnumerator hid,
        KrakenHub hub,
        KrakenLightingDeviceProvider lighting,
        KrakenCoolingProvider cooling,
        DeviceControlGate gate,
        HardwarePresence presence)
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
                if (!_gate.IsEnabled(KrakenHub.DeviceId))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!_presence.UsbPresent(KrakenProtocol.VendorId, KrakenProtocol.ProductIds))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var found = FindAndOpen();
                if (found == null)
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                _hub.Attach(found.Value.Device, found.Value.Model, found.Value.ReportLength);
                bool started = false;
                try
                {
                    if (_hub.Connect())
                    {
                        started = true;
                        var snap = _hub.Snapshot;
                        ServiceLog.Info(
                            $"[nzxt-kraken] connected: {_hub.ModelName}, firmware {snap.FirmwareVersion}, " +
                            $"{snap.Channels.Count} RGB channel(s), LCD bulk {(_hub.HasLcd ? "open" : "unavailable")}");
                        _lighting.OnHubStateUpdated();

                        int failures = 0;
                        while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled(KrakenHub.DeviceId))
                        {
                            if (_hub.Poll())
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
                            await Task.Delay(StatusPollMs, stoppingToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    // Runs whenever Attach ran, including when Connect throws.
                    _hub.Detach();
                    if (started)
                    {
                        ServiceLog.Info("[nzxt-kraken] disconnected");
                    }
                    _lighting.OnHubStateUpdated();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[nzxt-kraken] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Smallest report that can still carry a command: the 0x22 colour tables are 4 header
    /// bytes plus 60 of colour. Anything shorter is one of the cooler's other HID
    /// collections, not the vendor interface.
    /// </summary>
    private const int MinUsableReportLength = 64;

    private (IHidDevice Device, KrakenModel Model, int ReportLength)? FindAndOpen()
    {
        HidDeviceInfo? best = null;
        KrakenModel? bestModel = null;
        foreach (var model in KrakenModel.All)
        {
            foreach (var info in _hid.Find(KrakenProtocol.VendorId, model.ProductId))
            {
                // The cooler exposes a single vendor-defined HID interface; anything whose
                // reports are too short to carry a command is the wrong one. Report size is
                // per model - 512 bytes on the Elite V2, 64 on everything before it - so it
                // comes from the descriptor rather than a constant.
                if (info.OutputReportByteLength < MinUsableReportLength
                    || info.InputReportByteLength < MinUsableReportLength)
                {
                    continue;
                }
                if (best == null || info.InputReportByteLength > best.InputReportByteLength)
                {
                    best = info;
                    bestModel = model;
                }
            }
        }
        if (best == null || bestModel == null)
        {
            return null;
        }
        // forInput so Read honors its timeout instead of busy-spinning on Windows.
        var device = _hid.Open(best.Path, forInput: true);
        return device == null ? null : (device, bestModel, best.OutputReportByteLength);
    }
}
