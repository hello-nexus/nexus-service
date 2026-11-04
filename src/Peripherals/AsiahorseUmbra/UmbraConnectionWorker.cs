using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.AsiahorseUmbra;

public sealed class UmbraConnectionWorker : BackgroundService
{
    public const string HandlerId = "asiahorse-umbra";

    private const int ConnectPollMs = 5000;
    private const int InfoPollMs = 2000;
    // While a restart is pending the device drops within a fraction of a second; poll fast to notice.
    private const int RestartPollMs = 250;
    private const int MaxConsecutiveFailures = 3;

    private readonly IHidEnumerator _hid;
    private readonly UmbraHub _hub;
    private readonly AsiahorseUmbraLightingDeviceProvider _lighting;
    private readonly AsiahorseUmbraCoolingProvider _cooling;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;
    private readonly IConfigStore _store;
    private readonly FeatureGates _gates;
    // Set once a pillar was on during this connection, so HandBack only releases what Nexus may have touched.
    private bool _lightingTouched;
    private bool _coolingTouched;
    // True while the fan mode and duty are under Nexus: set by the connect-time apply, cleared on a Cooling switch-off.
    private bool _coolingApplied;
    // Released on a control change for this handler so a release stops the stream at once, not on the next poll.
    private readonly SemaphoreSlim _gateChanged = new(0);

    public UmbraConnectionWorker(IHidEnumerator hid, UmbraHub hub, AsiahorseUmbraLightingDeviceProvider lighting, AsiahorseUmbraCoolingProvider cooling, DeviceControlGate gate, HardwarePresence presence, IConfigStore store, FeatureGates? gates = null)
    {
        _gates = gates ?? FeatureGates.AllEnabled;
        _hid = hid;
        _hub = hub;
        _lighting = lighting;
        _cooling = cooling;
        _gate = gate;
        _presence = presence;
        _store = store;
        _gate.Changed += (handler, _) =>
        {
            if (string.Equals(handler, HandlerId, StringComparison.OrdinalIgnoreCase))
            {
                _gateChanged.Release();
            }
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled(HandlerId) || !_presence.UsbPresent(UmbraProtocol.VendorId, UmbraProtocol.ProductId))
                {
                    await WaitAsync(RetryMs(), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var device = FindAndOpen();
                if (device is null)
                {
                    await WaitAsync(RetryMs(), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                _hub.Attach(device);
                var started = false;
                var reachable = true;
                try
                {
                    if (_hub.Connect())
                    {
                        started = true;
                        ServiceLog.Info("[asiahorse-umbra] connected");
                        _coolingApplied = false;
                        HandBackStalePillars();
                        if (_gates.Cooling)
                        {
                            _coolingTouched = true;
                            _coolingApplied = true;
                            _cooling.OnConnected();
                        }
                        _lighting.OnHubStateUpdated();
                        reachable = await PollAsync(stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await WaitAsync(RetryMs(), stoppingToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (started && reachable && !_hub.IsRestarting)
                    {
                        HandBack();
                    }
                    else
                    {
                        _hub.Detach();
                    }
                    if (started)
                    {
                        ServiceLog.Info("[asiahorse-umbra] disconnected");
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
                ServiceLog.Error($"[asiahorse-umbra] worker error: {ex.Message}");
                await WaitAsync(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private int RetryMs() => _hub.IsRestarting ? RestartPollMs : ConnectPollMs;

    // Wakes early on a control change so turning Nexus Control back on connects at once.
    private async Task WaitAsync(int ms, CancellationToken ct) =>
        await _gateChanged.WaitAsync(ms, ct).ConfigureAwait(false);

    // True when the loop ends with the device still answering (control released or the service stopping).
    private async Task<bool> PollAsync(CancellationToken ct)
    {
        var failures = 0;
        while (_gateChanged.CurrentCount > 0)
        {
            _gateChanged.Wait(0);
        }
        while (!ct.IsCancellationRequested && _gate.IsEnabled(HandlerId))
        {
            if (_hub.PollInfo())
            {
                failures = 0;
                if (_gates.Lighting) _lightingTouched = true;
                // Cooling off: no fan mode or duty writes. The switch-off edge hands the fans back to the PWM header; a later switch-on applies the persisted fan mode again.
                if (!_gates.Cooling)
                {
                    if (_coolingApplied)
                    {
                        _coolingApplied = false;
                        _cooling.ReleaseAll();
                        _hub.SetFanSync(true);
                        _coolingTouched = false;
                    }
                }
                else if (!_coolingApplied)
                {
                    _coolingTouched = true;
                    _coolingApplied = true;
                    _cooling.OnConnected();
                }
                else
                {
                    _cooling.ReassertControl();
                }
            }
            else
            {
                failures++;
                if (failures >= MaxConsecutiveFailures || _hub.IsRestarting)
                {
                    return false;
                }
            }
            await _gateChanged.WaitAsync(_hub.IsRestarting ? RestartPollMs : InfoPollMs, ct).ConfigureAwait(false);
        }
        return true;
    }

    // The frames that leave the controller playing a firmware effect (the stored device effect when Nexus was streaming) and its fans on the PWM header.
    private byte[][] HandBackFrames(bool lighting, bool cooling)
    {
        var settings = _store.Load();
        var ls = settings.Devices.AsiahorseUmbra;
        var plan = UmbraLightingPlan.SourceOf(ls) == UmbraLightingPlan.SourceNexus
            ? new AsiahorseUmbraSettings
            {
                Source = UmbraLightingPlan.SourceFirmware,
                LightOn = ls.LightOn,
                Group = ls.Group,
                Sync = ls.Sync,
                Merge = ls.Merge,
                Async = ls.Async,
                FanSyncMb = ls.FanSyncMb,
            }
            : ls;
        var frames = new List<byte[]>();
        if (lighting)
        {
            var master = MasterBrightness.Resolve(settings);
            frames.AddRange(UmbraLightingPlan.Build(plan, _hub.Snapshot.Ports, master.Level, settings.Devices.LightingDevicePrefs, master.Scales));
        }
        // Fans stop following the software duty and go back to the PWM input header.
        if (cooling && !ls.FanSyncMb)
        {
            frames.Add(UmbraProtocol.EncodeFanSync(true));
        }
        return frames.ToArray();
    }

    // Leaves the controller playing a firmware effect: the stored device effect when Nexus was streaming.
    internal bool HandBack()
    {
        try
        {
            if (!_hub.ReleaseWithHandBack(HandBackFrames(_lightingTouched || _gates.Lighting, _coolingTouched || _gates.Cooling)))
            {
                ServiceLog.Warn("[asiahorse-umbra] hand-back write failed; the next connect retries");
                return false;
            }
            _lightingTouched = false;
            _coolingTouched = false;
            ServiceLog.Info("[asiahorse-umbra] released to the firmware effect");
            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[asiahorse-umbra] hand-back failed: {ex.Message}");
            _hub.Detach();
            return false;
        }
    }

    // A pillar that went off while the device was away: Nexus drove it earlier this session, so hand that part back now.
    internal void HandBackStalePillars()
    {
        var lighting = _lightingTouched && !_gates.Lighting;
        var cooling = _coolingTouched && !_gates.Cooling;
        if (!lighting && !cooling)
        {
            return;
        }
        try
        {
            // A failed write leaves the flags set, so the next connect tries again.
            if (!_hub.SendFrames(HandBackFrames(lighting, cooling)))
            {
                return;
            }
            if (lighting) _lightingTouched = false;
            if (cooling) _coolingTouched = false;
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[asiahorse-umbra] stale-pillar hand-back failed: {ex.Message}");
        }
    }

    private IHidDevice? FindAndOpen()
    {
        HidDeviceInfo? best = null;
        foreach (var info in _hid.Find(UmbraProtocol.VendorId, UmbraProtocol.ProductId))
        {
            if (info.OutputReportByteLength < UmbraProtocol.FrameLength || info.InputReportByteLength <= 0)
            {
                continue;
            }
            if (best is null || info.InputReportByteLength > best.InputReportByteLength)
            {
                best = info;
            }
        }
        // forInput=true for overlapped I/O so Read honors its timeout on Windows.
        return best is null ? null : _hid.Open(best.Path, forInput: true);
    }
}
