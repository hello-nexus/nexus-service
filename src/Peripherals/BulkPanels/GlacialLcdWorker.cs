using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>
/// Opens the Glacial LCD's HID channel while the panel is present and Nexus Control is on,
/// re-applies the saved state the way MB Manager does at start, then applies each change
/// at once and keeps the hardware-monitor widgets fresh. HID only: the bulk interface is
/// for picture uploads, which this build does not do.
/// </summary>
public sealed class GlacialLcdWorker : BackgroundService
{
    public const string HandlerId = "asus-glacial-lcd";

    private const int MissesToDetach = 2;
    private const int PresencePollMs = 2000;
    private const int LoopMs = 250;

    private readonly IHidEnumerator _hid;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;
    private readonly IConfigStore _store;
    private readonly ISensorProvider _sensors;
    private readonly int _connectPollMs;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile bool _connected;

    public GlacialLcdWorker(
        IHidEnumerator hid, DeviceControlGate gate, HardwarePresence presence, IConfigStore store, ISensorProvider sensors,
        int connectPollMs = 5000)
    {
        _hid = hid;
        _gate = gate;
        _presence = presence;
        _store = store;
        _sensors = sensors;
        _connectPollMs = connectPollMs;
    }

    public bool IsConnected => _connected;

    /// <summary>Tells the worker the saved state changed; it applies the difference on its next pass.</summary>
    public void RequestApply()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled(HandlerId) || !_presence.UsbPresent(0x0B05, GlacialLcdProtocol.ProductId))
                {
                    await Task.Delay(_connectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                using var hid = OpenHid();
                if (hid is null)
                {
                    await Task.Delay(_connectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                var screen = new GlacialLcdScreen(
                    hid,
                    () => _store.Load().Devices.GlacialLcd,
                    (slot, index) => GlacialLcdSensors.Read(_sensors, slot, index));
                if (!screen.Open())
                {
                    ServiceLog.Warn($"[{HandlerId}] the panel did not take the opening reports; retrying");
                    await Task.Delay(_connectPollMs, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                ServiceLog.Info($"[{HandlerId}] connected");
                _connected = true;
                try
                {
                    await RunSession(screen, stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    _connected = false;
                    ServiceLog.Info($"[{HandlerId}] disconnected");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[{HandlerId}] worker error: {ex.Message}");
                await Task.Delay(_connectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunSession(GlacialLcdScreen screen, CancellationToken stoppingToken)
    {
        int misses = 0;
        long nextPresenceMs = Environment.TickCount64 + PresencePollMs;
        while (!stoppingToken.IsCancellationRequested && _gate.IsEnabled(HandlerId))
        {
            await _wake.WaitAsync(LoopMs, stoppingToken).ConfigureAwait(false);
            if (!screen.Reconcile() || !screen.Tick())
            {
                ServiceLog.Warn($"[{HandlerId}] a report was refused; reopening");
                return;
            }
            if (Environment.TickCount64 >= nextPresenceMs)
            {
                nextPresenceMs = Environment.TickCount64 + PresencePollMs;
                misses = _presence.UsbPresent(0x0B05, GlacialLcdProtocol.ProductId) ? 0 : misses + 1;
                if (misses >= MissesToDetach)
                {
                    return;
                }
            }
        }
    }

    private IHidDevice? OpenHid()
    {
        HidDeviceInfo? best = null;
        foreach (var info in _hid.Find(0x0B05, GlacialLcdProtocol.ProductId))
        {
            if (best == null || info.OutputReportByteLength > best.OutputReportByteLength)
            {
                best = info;
            }
        }
        // Overlapped, so the state-reply reads honor their timeout on Windows.
        return best == null ? null : _hid.Open(best.Path, forInput: true);
    }
}
