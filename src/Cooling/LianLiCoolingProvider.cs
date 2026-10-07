using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.LianLi;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>
/// Bridges every Lian Li Uni Hub into the fan-control subsystem. Channel ids
/// are "{hubId}:port{N}" per port, so the first hub keeps "lianli:portN".
/// </summary>
public sealed class LianLiCoolingProvider : IFanControlProvider, ICoolingProvider
{
    private readonly LianLiHubSet _hubs;
    private readonly IConfigStore _store;

    // _pendingDuty/_softwareControlled are read on HTTP threads (GetFanChannels,
    // GetAll) and mutated by the curve worker (DriveFanSpeed) plus HTTP setters;
    // guard both behind _ctrlLock like QSeriesCoolerCoolingProvider does.
    private readonly object _ctrlLock = new();
    private readonly int[][] _pendingDuty;
    private readonly int[] _resumeEpochSeen = new int[LianLiHubSet.Capacity];
    private readonly HashSet<string> _softwareControlled = new(StringComparer.Ordinal);

    public LianLiCoolingProvider(LianLiHubSet hubs, IConfigStore store)
    {
        _hubs = hubs;
        _store = store;
        _pendingDuty = new int[LianLiHubSet.Capacity][];
        for (var i = 0; i < LianLiHubSet.Capacity; i++)
        {
            _pendingDuty[i] = new int[LianLiProtocol.PortCount];
        }
    }

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        var result = new List<FanChannel>();
        var devices = _store.Load().Devices;
        for (var slot = 0; slot < LianLiHubSet.Capacity; slot++)
        {
            var hub = _hubs.Hubs[slot];
            if (!hub.IsConnected) continue;
            var fans = LianLiHubSet.FansOf(devices, hub.DeviceId);
            var modelLabel = ModelLabel(hub, slot);
            var deviceLabel = $"Lian Li {modelLabel}";
            for (var p = 0; p < LianLiProtocol.PortCount; p++)
            {
                // A port with no fans set on the device page is an empty header, not a controllable channel.
                if (fans.GetFans(p) <= 0) continue;
                var id = ChannelId(hub, p);
                var rpm = hub.State.Rpm[p];
                int duty;
                bool sw;
                lock (_ctrlLock)
                {
                    duty = _pendingDuty[slot][p];
                    sw = _softwareControlled.Contains(id);
                }
                result.Add(new FanChannel
                {
                    Id = id,
                    Name = $"{modelLabel} Port {p + 1}",
                    DutyPercent = duty,
                    Rpm = rpm >= 0 ? rpm : 0,
                    Mode = sw ? FanModes.Manual : FanModes.Auto,
                    DeviceId = hub.DeviceId,
                    DeviceName = deviceLabel,
                    PortLabel = $"Port {p + 1}",
                    FanModel = null,
                    Orientation = null,
                });
            }
        }
        return result;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();

    public float? ReadTemperature(string sensorId) => null;

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyChannelWrite(channelId, clamped);
        return clamped;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = Math.Clamp(dutyPercent, 0, 100);
        ApplyChannelWrite(channelId, clamped);
    }

    public void ReleaseFan(string channelId)
    {
        if (!TryResolve(channelId, out var slot, out var port)) return;
        lock (_ctrlLock)
        {
            _softwareControlled.Remove(channelId);
            // Released to firmware control; drop the manual duty so GetAll reports Auto.
            _pendingDuty[slot][port] = 0;
        }
        var hub = _hubs.Hubs[slot];
        if (hub.IsConnected) hub.SetReleaseMode(port);
    }

    public void ReleaseAll()
    {
        lock (_ctrlLock)
        {
            _softwareControlled.Clear();
            foreach (var duties in _pendingDuty) Array.Clear(duties);
        }
        foreach (var hub in _hubs.Hubs)
        {
            if (!hub.IsConnected) continue;
            for (var p = 0; p < LianLiProtocol.PortCount; p++)
            {
                hub.SetReleaseMode(p);
            }
        }
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    // ── ICoolingProvider ──

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var components = new List<CoolingComponent>();
        var settings = _store.Load().Devices;
        for (var slot = 0; slot < LianLiHubSet.Capacity; slot++)
        {
            var hub = _hubs.Hubs[slot];
            if (!hub.IsConnected) continue;
            var fans = LianLiHubSet.FansOf(settings, hub.DeviceId);
            var modelLabel = ModelLabel(hub, slot);
            var devices = new List<CoolingDevice>(LianLiProtocol.PortCount);
            for (var p = 0; p < LianLiProtocol.PortCount; p++)
            {
                // Only ports with fans are surfaced (matches GetFanChannels).
                if (fans.GetFans(p) <= 0) continue;
                var rpm = hub.State.Rpm[p];
                int duty;
                lock (_ctrlLock) duty = _pendingDuty[slot][p];
                devices.Add(new CoolingDevice
                {
                    Id = ChannelId(hub, p),
                    Name = $"{modelLabel} Port {p + 1}",
                    Type = "Fan",
                    Rpm = rpm >= 0 ? rpm : 0,
                    Pwm = duty,
                });
            }
            components.Add(new CoolingComponent
            {
                Id = hub.DeviceId,
                Name = $"Lian Li {modelLabel}",
                Type = "LianLiHub",
                Devices = devices,
            });
        }
        return components;
    }

    // ── Internals ──

    public static bool IsLianLiId(string id) => LianLiHubSet.SlotOf(id) >= 0 && id.Contains(':', StringComparison.Ordinal);

    private static string ChannelId(LianLiHub hub, int port) => $"{hub.DeviceId}:port{port}";

    // The model name; a hub past the first carries its slot number so two hubs of one model stay apart.
    private static string ModelLabel(LianLiHub hub, int slot)
    {
        var model = hub.ModelName.Length > 0 ? hub.ModelName : "SL-Infinity";
        return slot == 0 ? model : $"{model} {slot + 1}";
    }

    private void ApplyChannelWrite(string channelId, int dutyPercent)
    {
        if (!TryResolve(channelId, out var slot, out var port)) return;
        var hub = _hubs.Hubs[slot];
        if (!hub.IsConnected)
        {
            ServiceLog.Warn($"[lianli-cooling] write to {channelId} dropped: hub not connected");
            return;
        }
        bool wasControlled;
        lock (_ctrlLock)
        {
            wasControlled = _softwareControlled.Contains(channelId);
            _softwareControlled.Add(channelId);
            _pendingDuty[slot][port] = dutyPercent;
        }
        // SetSpeed re-enters manual mode (mode write + settle + duty write): use it
        // only on the first write to take the port off mobo PWM. Subsequent writes
        // use SetDuty (duty write only) so re-entry does not reset the fan to default.
        var ok = wasControlled
            ? hub.SetDuty(port, dutyPercent)
            : hub.SetSpeed(port, dutyPercent);
        if (!ok)
        {
            ServiceLog.Warn($"[lianli-cooling] {(wasControlled ? "SetDuty" : "SetSpeed")} {channelId} duty {dutyPercent} returned false");
        }
    }

    /// <summary>Re-sends the duty of every software-controlled port on <paramref name="hub"/>; the connection worker calls it each poll.</summary>
    public void ReassertControl(LianLiHub hub)
    {
        if (!hub.IsConnected) return;
        var slot = LianLiHubSet.SlotOf(hub.DeviceId);
        if (slot < 0) return;
        // Across sleep the hub drops manual mode, so the first re-assert after a
        // resume re-enters it; every other one is speed-only, as SetSpeed's
        // re-entry resets the fan to its default each tick. A resume is consumed
        // only once every port took the re-entry.
        var resumeEpoch = hub.ResumeEpoch;
        var ports = new List<int>(LianLiProtocol.PortCount);
        var duties = new List<int>(LianLiProtocol.PortCount);
        lock (_ctrlLock)
        {
            for (var p = 0; p < LianLiProtocol.PortCount; p++)
            {
                if (!_softwareControlled.Contains(ChannelId(hub, p))) continue;
                ports.Add(p);
                duties.Add(_pendingDuty[slot][p]);
            }
            if (ports.Count == 0)
            {
                // Nothing to re-enter: a port taken over later starts with SetSpeed anyway.
                _resumeEpochSeen[slot] = resumeEpoch;
                return;
            }
        }
        var reenter = resumeEpoch != _resumeEpochSeen[slot];
        var allOk = true;
        for (var i = 0; i < ports.Count; i++)
        {
            var ok = reenter ? hub.SetSpeed(ports[i], duties[i]) : hub.SetDuty(ports[i], duties[i]);
            allOk &= ok;
            if (!ok)
            {
                ServiceLog.Warn($"[lianli-cooling] ReassertControl {ChannelId(hub, ports[i])} duty {duties[i]} returned false");
            }
        }
        if (allOk)
        {
            _resumeEpochSeen[slot] = resumeEpoch;
        }
    }

    // channelId shape: "{hubId}:port{N}". The curve path (DriveFanSpeed) can hand
    // a stale id like "lianli:port7" that the routes never validate, so the
    // port is bounds-checked here.
    private static bool TryResolve(string channelId, out int slot, out int port)
    {
        port = 0;
        slot = LianLiHubSet.SlotOf(channelId);
        if (slot < 0) return false;
        var portIdx = channelId.LastIndexOf(':');
        if (portIdx < 0) return false;
        var seg = channelId.AsSpan(portIdx + 1);
        if (!seg.StartsWith("port", StringComparison.Ordinal)) return false;
        if (!int.TryParse(seg.Slice(4), out port)) return false;
        return port >= 0 && port < LianLiProtocol.PortCount;
    }
}
