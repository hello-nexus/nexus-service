using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Strimer;

public sealed class StrimerConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    private const int MaxConsecutiveFailures = 3;

    private readonly IHidEnumerator _hid;
    private readonly StrimerUnits _units;
    private readonly StrimerLightingDeviceProvider _lighting;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;
    private bool _firstAttach;
    // Slot remembered per controller for this process, so one unplugging never renumbers the other.
    private readonly Dictionary<string, int> _slotByKey = new();

    public StrimerConnectionWorker(IHidEnumerator hid, StrimerUnits units, StrimerLightingDeviceProvider lighting, DeviceControlGate gate, HardwarePresence presence)
    {
        _hid = hid;
        _units = units;
        _lighting = lighting;
        _gate = gate;
        _presence = presence;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_gate.IsEnabled("strimer"))
                {
                    DetachAll();
                }
                else if (_presence.UsbPresent(StrimerProtocol.VendorId, StrimerProtocol.ProductId))
                {
                    Poll();
                }
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[strimer] worker error: {ex.Message}");
                await Task.Delay(ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
        DetachAll();
    }

    private void DetachAll()
    {
        foreach (var hub in _units.All)
        {
            if (hub.IsConnected) DetachUnit(hub);
        }
    }

    private void DetachUnit(StrimerHub hub)
    {
        hub.Detach();
        ServiceLog.Info($"[strimer] unit {hub.Unit + 1} disconnected");
        _lighting.OnHubStateUpdated();
    }

    internal void Poll()
    {
        foreach (var hub in _units.All)
        {
            if (hub.IsConnected && hub.ConsecutiveWriteFailures >= MaxConsecutiveFailures) DetachUnit(hub);
        }

        var infos = FindMatching();
        var attached = new HashSet<string>(StringComparer.Ordinal);
        var taken = new bool[_units.All.Count];
        foreach (var hub in _units.All)
        {
            var path = hub.AttachedPath;
            if (path != null)
            {
                attached.Add(path);
                taken[hub.Unit] = true;
            }
        }

        // Serials order units when every controller reports a distinct one; otherwise paths do.
        var useSerial = true;
        var serials = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in infos)
        {
            if (string.IsNullOrEmpty(i.Serial) || !serials.Add(i.Serial)) { useSerial = false; break; }
        }
        var fresh = new List<(string Key, HidDeviceInfo Info)>();
        foreach (var i in infos)
        {
            if (!attached.Contains(i.Path)) fresh.Add((useSerial ? i.Serial! : i.Path, i));
        }
        fresh.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        foreach (var (key, info) in fresh)
        {
            if (!_slotByKey.TryGetValue(key, out var slot) || slot >= taken.Length || taken[slot])
            {
                slot = Array.IndexOf(taken, false);
                if (slot < 0) break;
            }
            var device = _hid.Open(info.Path);
            if (device == null) continue;
            taken[slot] = true;
            _slotByKey[key] = slot;
            var hub = _units.All[slot];
            hub.Attach(device);
            if (!_firstAttach)
            {
                _firstAttach = true;
                ServiceLog.Info("[strimer] attached (untested - verification pending)");
            }
            ServiceLog.Info($"[strimer] unit {slot + 1} connected");
            _lighting.OnHubStateUpdated();
        }
    }

    private List<HidDeviceInfo> FindMatching()
    {
        var list = new List<HidDeviceInfo>();
        foreach (var info in _hid.Find(StrimerProtocol.VendorId, StrimerProtocol.ProductId))
        {
            if (info.UsagePage == StrimerProtocol.VendorUsagePage && info.Usage == StrimerProtocol.VendorUsage) list.Add(info);
        }
        return list;
    }
}
