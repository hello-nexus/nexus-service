using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Cooling;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLi;

/// <summary>
/// Attaches every Uni hub present to a slot of <see cref="LianLiHubSet"/> and
/// polls each attached one. A hub keeps its slot across restarts and replugs:
/// its key (USB serial, or path without one) is pinned to the slot in settings.
/// A hub with an unknown key takes over the first slot whose hub is gone, with
/// that slot's fan counts, lighting and fan curves.
/// </summary>
public sealed class LianLiConnectionWorker : BackgroundService
{
    private const int ConnectPollMs = 5000;
    // Each scan opens every Uni hub interface, so with a hub attached the look for another one is slower.
    private const int ExtraHubScanMs = 30_000;
    private const int RpmPollMs = 2000;
    private const int MaxConsecutiveFailures = 3;
    // A hub that never answers the version query stops being asked; each ask holds the hub lock the frame writer needs.
    private const int MaxFirmwareReads = 5;

    private readonly IHidEnumerator _hid;
    private readonly LianLiHubSet _hubs;
    private readonly LianLiLightingDeviceProvider _lighting;
    private readonly LianLiCoolingProvider _cooling;
    private readonly DeviceControlGate _gate;
    private readonly HardwarePresence _presence;
    private readonly IConfigStore _store;
    private readonly int[] _failures = new int[LianLiHubSet.Capacity];
    private readonly int[] _firmwareReads = new int[LianLiHubSet.Capacity];
    private readonly bool[] _wasEnabled = new bool[LianLiHubSet.Capacity];
    private long _nextScanMs;

    public LianLiConnectionWorker(
        IHidEnumerator hid, LianLiHubSet hubs, LianLiLightingDeviceProvider lighting, LianLiCoolingProvider cooling,
        DeviceControlGate gate, HardwarePresence presence, IConfigStore store)
    {
        _hid = hid;
        _hubs = hubs;
        _lighting = lighting;
        _cooling = cooling;
        _gate = gate;
        _presence = presence;
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var attached = false;
                try
                {
                    attached = Tick(Environment.TickCount64);
                }
                catch (Exception ex)
                {
                    ServiceLog.Error($"[lianli] worker error: {ex.Message}");
                }
                await Task.Delay(attached ? RpmPollMs : ConnectPollMs, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            for (var slot = 0; slot < LianLiHubSet.Capacity; slot++)
            {
                if (_hubs.Hubs[slot].IsConnected) Detach(slot, "disconnected");
            }
        }
    }

    /// <summary>One pass: drop hubs whose control was turned off, attach new ones, poll the rest. True while any hub is attached.</summary>
    internal bool Tick(long nowMs)
    {
        for (var slot = 0; slot < LianLiHubSet.Capacity; slot++)
        {
            var hub = _hubs.Hubs[slot];
            var enabled = _gate.IsEnabled(hub.DeviceId);
            if (hub.IsConnected && !enabled) Detach(slot, "released");
            // A hub turned back on attaches now rather than at the next slow scan.
            else if (enabled && !_wasEnabled[slot] && hub.Present) _nextScanMs = 0;
            _wasEnabled[slot] = enabled;
        }
        if (nowMs >= _nextScanMs)
        {
            ScanAndAttach();
            _nextScanMs = nowMs + (_hubs.AnyConnected ? ExtraHubScanMs : ConnectPollMs);
        }
        var any = false;
        for (var slot = 0; slot < LianLiHubSet.Capacity; slot++)
        {
            if (!_hubs.Hubs[slot].IsConnected) continue;
            any = true;
            Poll(slot);
        }
        // Picks up fan-count edits (zone LED counts) without waiting for the RgbBridge periodic poll.
        _lighting.OnHubStateUpdated();
        return any;
    }

    private void ScanAndAttach()
    {
        var free = false;
        foreach (var hub in _hubs.Hubs)
        {
            free |= !hub.IsConnected;
        }
        if (!free) return;
        if (!_presence.UsbPresent(LianLiProtocol.VendorId, LianLiFanProfiles.AllProductIds))
        {
            foreach (var hub in _hubs.Hubs) hub.Present = false;
            return;
        }

        var present = Candidates();
        var presentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in present) presentKeys.Add(c.Key);
        foreach (var (info, profile, key) in present)
        {
            if (IsAttached(info.Path)) continue;
            var slot = SlotFor(key, presentKeys);
            if (slot < 0)
            {
                ServiceLog.Warn($"[lianli] no free hub slot for {key}");
                continue;
            }
            var hub = _hubs.Hubs[slot];
            if (hub.IsConnected || !_gate.IsEnabled(hub.DeviceId)) continue;
            var device = _hid.Open(info.Path);
            if (device == null) continue;
            hub.Attach(device, profile, info.Path);
            _failures[slot] = 0;
            _firmwareReads[slot] = 0;
            ServiceLog.Info($"[lianli] connected {profile.ModelName} as {hub.DeviceId} pid={info.ProductId:X4} rpt={info.InputReportByteLength}/{info.OutputReportByteLength}/{info.FeatureReportByteLength}");
            _lighting.OnHubStateUpdated();
        }
        var keys = _store.Load().Devices.LianLiHubKeys;
        for (var i = 0; i < LianLiHubSet.Capacity; i++)
        {
            var hub = _hubs.Hubs[i];
            hub.Present = hub.IsConnected || (i < keys.Count && presentKeys.Contains(keys[i]));
        }
    }

    private void Poll(int slot)
    {
        var hub = _hubs.Hubs[slot];
        // The hub can answer the version query empty for a while after attach.
        if (string.IsNullOrEmpty(hub.State.FirmwareVersion) && _firmwareReads[slot]++ < MaxFirmwareReads
            && hub.ReadFirmwareVersion(out var familyId))
        {
            ServiceLog.Info($"[lianli] {hub.DeviceId} firmware {hub.State.FirmwareVersion} (family 0x{familyId:X2})");
        }
        if (hub.ReadRpm())
        {
            _failures[slot] = 0;
            _cooling.ReassertControl(hub);
        }
        else if (++_failures[slot] >= MaxConsecutiveFailures)
        {
            hub.Present = false;
            Detach(slot, "disconnected");
        }
    }

    private void Detach(int slot, string why)
    {
        var hub = _hubs.Hubs[slot];
        hub.Detach();
        ServiceLog.Info($"[lianli] {hub.DeviceId} {why}");
        _lighting.OnHubStateUpdated();
    }

    private bool IsAttached(string path)
    {
        foreach (var hub in _hubs.Hubs)
        {
            if (hub.IsConnected && hub.AttachedPath == path) return true;
        }
        return false;
    }

    // Every vendor-interface Uni hub present, in product-id order, with its key.
    private List<(HidDeviceInfo Info, LianLiFanProfile Profile, string Key)> Candidates()
    {
        var found = new List<(HidDeviceInfo Info, LianLiFanProfile Profile, string Key)>();
        var keyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pid in LianLiFanProfiles.AllProductIds)
        {
            if (!LianLiFanProfiles.TryGet(pid, out var profile)) continue;
            foreach (var info in _hid.Find(LianLiProtocol.VendorId, pid))
            {
                if (info.UsagePage == LianLiProtocol.VendorUsagePage && info.Usage == LianLiProtocol.VendorUsage)
                {
                    var key = KeyOf(info);
                    keyCounts[key] = keyCounts.GetValueOrDefault(key) + 1;
                    found.Add((info, profile, key));
                }
            }
        }
        // Hubs sharing a serial cannot be told apart by it, so each keys on its path.
        for (var i = 0; i < found.Count; i++)
        {
            var (info, profile, key) = found[i];
            if (keyCounts[key] > 1) found[i] = (info, profile, "path:" + info.Path);
        }
        return found;
    }

    internal static string KeyOf(HidDeviceInfo info) =>
        string.IsNullOrEmpty(info.Serial) ? "path:" + info.Path : "sn:" + info.Serial;

    /// <summary>
    /// The slot pinned to <paramref name="key"/>, pinning a new key to the
    /// first slot whose pinned hub is neither attached nor present (a
    /// serial-less hub's path changes with its port), else to the first unused
    /// slot; -1 when every slot belongs to a hub that is here.
    /// </summary>
    private int SlotFor(string key, HashSet<string> presentKeys)
    {
        var keys = _store.Load().Devices.LianLiHubKeys;
        var pinned = keys.IndexOf(key);
        if (pinned >= 0) return pinned < LianLiHubSet.Capacity ? pinned : -1;

        var slot = -1;
        for (var i = 0; i < Math.Min(keys.Count, LianLiHubSet.Capacity); i++)
        {
            if (!_hubs.Hubs[i].IsConnected && !presentKeys.Contains(keys[i]))
            {
                slot = i;
                break;
            }
        }
        if (slot < 0 && keys.Count < LianLiHubSet.Capacity) slot = keys.Count;
        if (slot < 0) return -1;
        _store.Update(s =>
        {
            var next = new List<string>(s.Devices.LianLiHubKeys);
            while (next.Count <= slot) next.Add("");
            next[slot] = key;
            s.Devices.LianLiHubKeys = next;
        });
        return slot;
    }
}
