using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.JpegPanels;

/// <summary>
/// Holds one <see cref="JpegPanelHub"/> attached while its panel is present and Nexus
/// Control is on for it. There is nothing to poll on these devices - no telemetry, no
/// status reports - so this only watches for arrival and departure.
///
/// Departure is detected by the frame path failing, not by enumeration: a panel that
/// stops accepting writes is gone whether or not Windows has retired its HID node yet.
/// </summary>
public sealed class JpegPanelConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int PresencePollMs = 2000;
    private const int PresenceSlackMs = 100;

    private readonly IHidEnumerator _hid;
    private readonly JpegPanelHub _hub;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;
    private readonly AttachBackoff _backoff;

    /// <summary>Latched so a retry every few seconds does not repeat the miss.</summary>
    private bool _missLogged;

    public JpegPanelConnectionWorker(IHidEnumerator hid, JpegPanelHub hub, DeviceControlGate gate, HardwarePresence presence)
    {
        _hid = hid;
        _hub = hub;
        _gate = gate;
        _presence = presence;
        // Only a model with a session has a re-attach loop worth spacing out; the rest keep the fixed retry.
        _backoff = new AttachBackoff(() => Environment.TickCount64, enabled: hub.SettleMs > 0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var model = _hub.Model;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _backoff.WaitAsync(
                    () => !_gate.IsEnabled(model.HandlerId) || !OnBus(),
                    Task.Delay,
                    stoppingToken).ConfigureAwait(false);

                if (!_gate.IsEnabled(model.HandlerId))
                {
                    _backoff.Clear();
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!OnBus())
                {
                    _backoff.Clear();
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var device = FindAndOpen();
                if (device == null)
                {
                    LogMissOnce();
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                _missLogged = false;

                if (_hub.SettleMs > 0)
                {
                    try
                    {
                        await Task.Delay(_hub.SettleMs, stoppingToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        device.Dispose();
                        throw;
                    }
                }

                if (!_hub.Attach(device, stoppingToken))
                {
                    NoteFailedCycle("attach failed");
                    await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                bool quiet = _backoff.Failures > 0;
                if (!quiet)
                {
                    ServiceLog.Info($"[{model.HandlerId}] connected: {model.Name}, {model.Width}x{model.Height}");
                }
                long connectedMs = Environment.TickCount64;
                try
                {
                    int tickMs = _hub.TickIntervalMs;
                    long nextPresenceMs = 0;
                    while (!stoppingToken.IsCancellationRequested
                        && _gate.IsEnabled(model.HandlerId)
                        && _hub.IsConnected
                        && PresenceHolds(ref nextPresenceMs))
                    {
                        await Task.Delay(tickMs > 0 ? tickMs : PresencePollMs, stoppingToken).ConfigureAwait(false);
                        if (tickMs > 0)
                        {
                            _hub.Tick();
                        }
                        if (_hub.IsConnected && Environment.TickCount64 - connectedMs >= AttachBackoff.HealthyAfterMs)
                        {
                            int ended = _backoff.NoteHealthy();
                            if (ended > 0)
                            {
                                ServiceLog.Info($"[{model.HandlerId}] recovered after {ended} failed cycle(s)");
                            }
                        }
                    }
                }
                finally
                {
                    bool dropped = !_hub.IsConnected && !stoppingToken.IsCancellationRequested && _gate.IsEnabled(model.HandlerId);
                    _hub.Detach();
                    if (dropped)
                    {
                        NoteFailedCycle("handle dropped");
                    }
                    else if (!quiet)
                    {
                        ServiceLog.Info($"[{model.HandlerId}] disconnected");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[{_hub.Model.HandlerId}] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
        _hub.Detach();
    }

    private void NoteFailedCycle(string what)
    {
        int report = _backoff.NoteFailure();
        if (report > 0)
        {
            ServiceLog.Warn($"[{_hub.Model.HandlerId}] {what}; {report} failed cycle(s) since the last report, next attempt in {_backoff.DelayMs / 1000} s");
        }
    }

    /// <summary>The presence probe keeps its own cadence even when the loop ticks faster for session traffic.</summary>
    private bool PresenceHolds(ref long nextCheckMs)
    {
        long now = Environment.TickCount64;
        // The loop delay can equal the probe period, and the tick clock is coarse, so a
        // wake a few ms early must still probe rather than wait a whole extra period.
        if (now + PresenceSlackMs < nextCheckMs)
        {
            return true;
        }
        nextCheckMs = now + PresencePollMs;
        return StillPresent();
    }

    private void LogMissOnce()
    {
        if (_missLogged)
        {
            return;
        }
        _missLogged = true;
        var model = _hub.Model;
        var seen = new System.Text.StringBuilder();
        bool usable = false;
        for (int i = 0; i < model.ProductIds.Count; i++)
        {
            foreach (var info in _hid.Find(model.VendorId, model.ProductIds[i]))
            {
                usable |= IsCandidate(model, info, OperatingSystem.IsLinux());
                seen.Append(seen.Length == 0 ? "" : "; ")
                    .Append($"pid=0x{info.ProductId:X4} usage={info.UsagePage:X4}/{info.Usage:X2} out={info.OutputReportByteLength} in={info.InputReportByteLength}");
            }
        }
        ServiceLog.Info(seen.Length == 0
            ? $"[{model.HandlerId}] no HID interface enumerated for this panel"
            : usable
                ? $"[{model.HandlerId}] could not open the panel's HID interface; saw {seen}"
                : model.Handshake != null && OperatingSystem.IsLinux()
                    ? $"[{model.HandlerId}] not attached on Linux (control channel unsupported over hidraw); saw {seen}"
                    : $"[{model.HandlerId}] no interface carries a {model.ReportLength}-byte report; saw {seen}");
    }

    private bool OnBus() => _presence.UsbPresent(_hub.Model.VendorId, _hub.Model.ProductIds);

    // An enumerator that failed reads as an empty bus, so an absence is
    // confirmed against the panel's own HID interfaces before it detaches.
    private bool StillPresent()
    {
        if (OnBus()) return true;
        var model = _hub.Model;
        for (int i = 0; i < model.ProductIds.Count; i++)
        {
            if (_hid.Find(model.VendorId, model.ProductIds[i]).Count > 0) return true;
        }
        return false;
    }

    /// <summary>
    /// A model with a control channel stays unattached on Linux: hidraw read() drops the
    /// report-id byte that the AsRock and ASUS reply parsers index past. A model that only
    /// writes frames has no reply to misparse.
    /// </summary>
    internal static bool IsCandidate(JpegPanelModel model, HidDeviceInfo info, bool isLinux)
    {
        if (isLinux && model.Handshake != null)
        {
            return false;
        }
        return info.OutputReportByteLength >= model.ReportLength;
    }

    private IHidDevice? FindAndOpen()
    {
        var model = _hub.Model;
        HidDeviceInfo? best = null;
        for (int i = 0; i < model.ProductIds.Count; i++)
        {
            foreach (var info in _hid.Find(model.VendorId, model.ProductIds[i]))
            {
                // These coolers expose several HID collections; the panel is the one whose
                // output reports are long enough to carry a frame chunk. Matching on the
                // declared length rather than a usage page keeps this working across the
                // family, whose collections are not consistently tagged.
                if (!IsCandidate(model, info, OperatingSystem.IsLinux()))
                {
                    continue;
                }
                if (best == null || info.OutputReportByteLength < best.OutputReportByteLength)
                {
                    best = info;
                }
            }
        }
        // forInput: a model with a control channel reads the panel's replies, and a
        // non-overlapped handle would block that read with no timeout to bound it.
        return best == null ? null : _hid.Open(best.Path, forInput: true);
    }
}
