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
    // A full HID enumeration opens every controller, so steady state skips it and only rescans on this cadence or when the USB count changes.
    private const long RescanMs = 30_000;
    private long _lastScanMs = long.MinValue / 2;
    // Windows reports identical controllers as one USB entry, so only a change in the count (not its size) can mean a replug.
    private int _lastUsbCount = -1;
    private int _zeroUsbPolls;
    private const int MissedScansToDetach = 2;
    // Keeps a transient enumeration failure from restarting a healthy unit.
    private readonly int[] _missedScans = new int[StrimerUnits.MaxUnits];
    private HashSet<string> _prevPresent = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ignoredPaths = new(StringComparer.Ordinal);
    private readonly HidDeviceInfo?[] _attachedInfo = new HidDeviceInfo?[StrimerUnits.MaxUnits];
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
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!_gate.IsEnabled("strimer"))
                    {
                        DetachAll();
                    }
                    else
                    {
                        Poll(_presence.UsbCount(StrimerProtocol.VendorId, StrimerProtocol.ProductId), Environment.TickCount64);
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
        }
        finally
        {
            DetachAll();
        }
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

    /// <param name="usbCount">Controllers the cheap USB probe sees right now.</param>
    internal void Poll(int usbCount, long nowMs)
    {
        foreach (var hub in _units.All)
        {
            if (hub.IsConnected && hub.ConsecutiveWriteFailures >= MaxConsecutiveFailures) DetachUnit(hub);
        }
        // One empty probe can be an enumerator hiccup; two in a row is an unplug.
        if (usbCount <= 0)
        {
            _lastUsbCount = usbCount;
            if (++_zeroUsbPolls >= 2) DetachAll();
            return;
        }
        _zeroUsbPolls = 0;

        var countChanged = usbCount != _lastUsbCount;
        _lastUsbCount = usbCount;
        var due = nowMs - _lastScanMs >= RescanMs;
        if (_units.Connected().Count > 0 && !countChanged && !due) return;
        _lastScanMs = nowMs;

        var known = new HashSet<string>(_ignoredPaths, StringComparer.Ordinal);
        foreach (var hub in _units.All)
        {
            var path = hub.AttachedPath;
            if (path != null) known.Add(path);
        }
        var (paths, scanned) = _hid.Scan(StrimerProtocol.VendorId, StrimerProtocol.ProductId, known);
        var present = new HashSet<string>(paths, StringComparer.Ordinal);
        _ignoredPaths.IntersectWith(present);

        var fresh = new List<HidDeviceInfo>();
        foreach (var info in scanned)
        {
            if (info.UsagePage == StrimerProtocol.VendorUsagePage && info.Usage == StrimerProtocol.VendorUsage) fresh.Add(info);
            else _ignoredPaths.Add(info.Path);
        }

        // A missing path is forgiven once. It frees its slot at once only when a new path of the same controller
        // replaces it (macOS paths change on replug): no more paths than attached units, none of them seen before.
        var attachedBefore = _units.Connected().Count;
        foreach (var hub in _units.All)
        {
            if (!hub.IsConnected) continue;
            var path = hub.AttachedPath;
            if (path != null && present.Contains(path))
            {
                _missedScans[hub.Unit] = 0;
                continue;
            }
            var serial = _attachedInfo[hub.Unit]?.Serial;
            var replaced = present.Count <= attachedBefore && fresh.Exists(f =>
                !_prevPresent.Contains(f.Path)
                && (string.IsNullOrEmpty(serial) || string.IsNullOrEmpty(f.Serial) || serial == f.Serial));
            if (++_missedScans[hub.Unit] >= MissedScansToDetach || replaced)
            {
                DetachUnit(hub);
            }
        }
        _prevPresent = present;

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
        void Note(string? serial)
        {
            if (string.IsNullOrEmpty(serial) || !serials.Add(serial)) useSerial = false;
        }
        foreach (var hub in _units.All)
        {
            if (hub.IsConnected) Note(_attachedInfo[hub.Unit]?.Serial);
        }
        foreach (var i in fresh) Note(i.Serial);
        var order = new List<(string Key, HidDeviceInfo Info)>();
        foreach (var i in fresh)
        {
            order.Add((useSerial ? i.Serial! : i.Path, i));
        }
        order.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        foreach (var (key, info) in order)
        {
            if (!_slotByKey.TryGetValue(key, out var slot) || slot >= taken.Length || taken[slot])
            {
                slot = Array.IndexOf(taken, false);
                if (slot < 0) break;
            }
            // Reserved before the open, so a controller that is busy keeps its slot against the ones sorted after it.
            taken[slot] = true;
            _slotByKey[key] = slot;
            var device = _hid.Open(info.Path);
            if (device == null) continue;
            var hub = _units.All[slot];
            hub.Attach(device);
            _attachedInfo[slot] = info;
            _missedScans[slot] = 0;
            if (!_firstAttach)
            {
                _firstAttach = true;
                ServiceLog.Info("[strimer] attached (untested - verification pending)");
            }
            ServiceLog.Info($"[strimer] unit {slot + 1} connected");
            _lighting.OnHubStateUpdated();
        }
    }
}
