using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Peripherals.Ryujin;

/// <summary>
/// Keeps the Ryujin III Extreme open while its Nexus Control toggle is on, and runs Armoury
/// Crate's cycle: resend the duty, request status, send the sensor text slots for the built-in
/// hardware-monitor screen, request duty. This thread is the one writer, so cooling, sensor and
/// display reports never interleave. Nothing goes out while Nexus Control is off.
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
    private readonly IConfigStore _store;
    private readonly ISensorProvider _sensors;

    private int _appliedBrightness;
    private int _appliedRotation;

    /// <summary>Gate decisions made so far; a test waits on it instead of sleeping.</summary>
    internal int GateChecks => Volatile.Read(ref _gateChecks);

    private int _gateChecks;

    internal Func<DateTime> Now { get; set; } = () => DateTime.Now;

    public RyujinCoolingWorker(
        IHidEnumerator hid, RyujinCoolingHub hub, RyujinCoolingProvider cooling, DeviceControlGate gate, HardwarePresence presence,
        IConfigStore store, ISensorProvider sensors)
    {
        _hid = hid;
        _hub = hub;
        _cooling = cooling;
        _gate = gate;
        _presence = presence;
        _store = store;
        _sensors = sensors;
        // A control change stops the writes at once instead of on the next keepalive.
        _gate.Changed += (handler, _) =>
        {
            if (string.Equals(handler, RyujinCoolingProtocol.HandlerId, StringComparison.OrdinalIgnoreCase))
            {
                _hub.Wake.Release();
            }
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Interlocked.Increment(ref _gateChecks);
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
                    await RunSessionAsync(stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    // Best effort: a pump Nexus drove goes back to the safe duty before the handle closes.
                    try
                    {
                        _cooling.ReleaseForDisconnect();
                        _hub.Flush();
                    }
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

    private async Task RunSessionAsync(CancellationToken ct)
    {
        var display = _store.Load().Devices.RyujinDisplay;
        _appliedBrightness = display.Brightness;
        _appliedRotation = display.Rotation;
        if (!_hub.Send(RyujinMonitorProtocol.EncodeStartup(Now(), display.Rotation, display.Brightness)))
        {
            ServiceLog.Warn("[asus-ryujin-cooling] startup sequence write failed");
            return;
        }
        ServiceLog.Info("[asus-ryujin-cooling] hardware-monitor layout selected");

        int failures = 0;
        long nextCycle = 0;
        while (!ct.IsCancellationRequested && _gate.IsEnabled(RyujinCoolingProtocol.HandlerId))
        {
            ApplyDisplayChange();
            if (Environment.TickCount64 >= nextCycle)
            {
                _cooling.ReassertControl();
                nextCycle = Environment.TickCount64 + RyujinCoolingProtocol.KeepaliveMs;
                if (_hub.Flush() && _hub.Poll(BuildSensorFrames))
                {
                    failures = 0;
                }
                else if (++failures >= MaxConsecutiveFailures)
                {
                    return;
                }
            }
            else if (!_hub.Flush() && ++failures >= MaxConsecutiveFailures)
            {
                return;
            }
            int wait = (int)Math.Max(1, nextCycle - Environment.TickCount64);
            await _hub.Wake.WaitAsync(wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Sends the settings the user changed since the last send: a rotation change re-selects the layout, a brightness change is the burst of three settings reports, each behind a DC.</summary>
    private void ApplyDisplayChange()
    {
        var wanted = _store.Load().Devices.RyujinDisplay;
        if (wanted.Rotation != _appliedRotation)
        {
            if (_hub.Send(RyujinMonitorProtocol.EncodeRotationChange(Now(), wanted.Rotation, wanted.Brightness)))
            {
                ServiceLog.Info($"[asus-ryujin-cooling] display rotation {_appliedRotation} -> {wanted.Rotation}, brightness {_appliedBrightness} -> {wanted.Brightness}");
                _appliedRotation = wanted.Rotation;
                _appliedBrightness = wanted.Brightness;
            }
        }
        else if (wanted.Brightness != _appliedBrightness)
        {
            if (_hub.Send(RyujinMonitorProtocol.EncodeBrightnessBurst(_appliedRotation, _appliedBrightness, wanted.Brightness)))
            {
                ServiceLog.Info($"[asus-ryujin-cooling] display brightness {_appliedBrightness} -> {wanted.Brightness}");
                _appliedBrightness = wanted.Brightness;
            }
        }
    }

    private IReadOnlyList<byte[]> BuildSensorFrames()
    {
        var snap = _hub.Snapshot;
        return RyujinMonitorFrames.Build(
            ReadSummary(SummarySensorKind.CpuTemp),
            ReadSummary(SummarySensorKind.GpuTemp),
            _hub.StatusFresh ? snap.PumpRpm : null);
    }

    private float? ReadSummary(SummarySensorKind kind)
    {
        try
        {
            return SummarySensors.Value(_sensors, kind);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[asus-ryujin-cooling] sensor read failed: {ex.GetType().Name}: {ex.Message}");
            return null;
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
