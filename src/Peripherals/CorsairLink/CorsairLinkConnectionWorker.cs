using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Conflicts;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
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
    // Each failed init flips the hub to software mode and back; repeated failures back off to this.
    private const int MaxInitRetryMs = 60_000;
    // Longer than iCUE's telemetry read interval.
    private const int ListenMs = 2500;
    // Longer than iCUE's keep-alive ping interval to its Bragi devices.
    private const long QuietAfterOtherHostMs = 60_000;

    private readonly IHidEnumerator _hid;
    private readonly CorsairLinkHub _hub;
    private readonly CorsairLinkLightingDeviceProvider _lighting;
    private readonly CorsairLinkCoolingProvider _cooling;
    private readonly CorsairLinkLcd _lcd;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;
    private readonly IConflictDetector? _conflicts;

    public CorsairLinkConnectionWorker(
        IHidEnumerator hid,
        CorsairLinkHub hub,
        CorsairLinkLightingDeviceProvider lighting,
        CorsairLinkCoolingProvider cooling,
        CorsairLinkLcd lcd,
        DeviceControlGate gate,
        HardwarePresence presence,
        IConflictDetector? conflicts = null)
    {
        _hid = hid;
        _hub = hub;
        _lighting = lighting;
        _cooling = cooling;
        _lcd = lcd;
        _gate = gate;
        _presence = presence;
        _conflicts = conflicts;
    }

    // A running vendor app takes the hub once Nexus lets go, paused or not;
    // switching the hub to hardware mode would cut that app off.
    private bool HandBack() =>
        _gate.PausedByApp("corsair") is null
        && !(DeviceControlPolicy.ConflictAppFor("corsair") is { } app && _conflicts?.IsAppRunning(app) == true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first listen blocks; yield so it does not hold up host startup.
        await Task.Yield();
        var initRetryMs = ConnectPollMs;
        // When another program last answered on the hub; null once Nexus holds it again.
        long? otherHostHeardMs = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled("corsair"))
                {
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!_presence.UsbPresent(CorsairLinkProtocol.VendorId, CorsairLinkProtocol.ProductId))
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

                var listenStartMs = Environment.TickCount64;
                if (CorsairLinkHub.HearsAnotherHost(device, ListenMs))
                {
                    device.Dispose();
                    if (otherHostHeardMs is null)
                    {
                        ServiceLog.Info("[corsair] another program is driving the hub; waiting for it to stop");
                    }
                    otherHostHeardMs = Environment.TickCount64;
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                // Listens back to back until the hub has been silent long enough: a ping
                // falling between listens would otherwise go unheard.
                if (otherHostHeardMs is { } heard && Environment.TickCount64 - heard < QuietAfterOtherHostMs)
                {
                    device.Dispose();
                    // A failed read returns before the listen window ends; pace that path.
                    if (Environment.TickCount64 - listenStartMs < ListenMs)
                    {
                        await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    }
                    continue;
                }

                _hub.Attach(device);

                if (!_hub.Initialize())
                {
                    if (_hub.ForeignHostSeen)
                    {
                        if (otherHostHeardMs is null)
                        {
                            ServiceLog.Warn("[corsair] another program answered during initialize; releasing the hub");
                        }
                        otherHostHeardMs = Environment.TickCount64;
                        _hub.Detach(handBack: false);
                        continue;
                    }
                    // Detach clears Firmware, so read it before. Empty means the
                    // firmware read itself never landed a full reply - the hub went
                    // silent (or answered short) before the device-list read.
                    var fw = _hub.State.Firmware;
                    ServiceLog.Warn($"[corsair] initialize failed, retrying in {initRetryMs / 1000}s (fw={(string.IsNullOrEmpty(fw) ? "none" : fw)})");
                    _hub.Detach(handBack: HandBack());
                    await Task.Delay(initRetryMs, stoppingToken).ConfigureAwait(false);
                    initRetryMs = Math.Min(initRetryMs * 2, MaxInitRetryMs);
                    continue;
                }
                initRetryMs = ConnectPollMs;
                otherHostHeardMs = null;

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
                    while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled("corsair") && !_hub.ForeignHostSeen)
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
                    if (_hub.ForeignHostSeen)
                    {
                        ServiceLog.Warn("[corsair] another program answered on the hub; stopped writing to it");
                        otherHostHeardMs = Environment.TickCount64;
                    }
                    var handBack = HandBack() && !_hub.ForeignHostSeen;
                    _lcd.Detach(handBack);
                    _hub.Detach(handBack);
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
