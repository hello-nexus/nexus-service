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
/// Bridges the Lian Li Uni Hub into the fan-control subsystem.
/// Channel IDs are "lianli:port{N}" for ports 0..3.
/// </summary>
public sealed class LianLiCoolingProvider : IFanControlProvider, ICoolingProvider
{
    private readonly LianLiHub _hub;
    private readonly IConfigStore _store;

    // _pendingDuty/_softwareControlled are read on HTTP threads (GetFanChannels,
    // GetAll) and mutated by the curve worker (DriveFanSpeed) plus HTTP setters;
    // guard both behind _ctrlLock like QSeriesCoolerCoolingProvider does.
    private readonly object _ctrlLock = new();
    private readonly int[] _pendingDuty = new int[LianLiProtocol.PortCount];
    private int _resumeEpochSeen;
    private readonly HashSet<string> _softwareControlled = new(StringComparer.Ordinal);

    public LianLiCoolingProvider(LianLiHub hub, IConfigStore store)
    {
        _hub = hub;
        _store = store;
    }

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        if (!_hub.IsConnected) return Array.Empty<FanChannel>();
        var lianli = _store.Load().Devices.LianLi;
        var result = new List<FanChannel>(LianLiProtocol.PortCount);
        var deviceId = _hub.DeviceId;
        var modelLabel = _hub.ModelName.Length > 0 ? _hub.ModelName : "SL-Infinity";
        var deviceLabel = $"Lian Li {modelLabel}";
        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            // A port with no fans set on the device page is an empty header, not a controllable channel.
            if (lianli.GetFans(p) <= 0) continue;
            var id = $"lianli:port{p}";
            var rpm = _hub.State.Rpm[p];
            int duty;
            bool sw;
            lock (_ctrlLock)
            {
                duty = _pendingDuty[p];
                sw = _softwareControlled.Contains(id);
            }
            result.Add(new FanChannel
            {
                Id = id,
                Name = $"{modelLabel} Port {p + 1}",
                DutyPercent = duty,
                Rpm = rpm >= 0 ? rpm : 0,
                Mode = sw ? FanModes.Manual : FanModes.Auto,
                DeviceId = deviceId,
                DeviceName = deviceLabel,
                PortLabel = $"Port {p + 1}",
                FanModel = null,
                Orientation = null,
            });
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
        if (!IsLianLiId(channelId)) return;
        var hasPort = TryParsePort(channelId, out var port);
        lock (_ctrlLock)
        {
            _softwareControlled.Remove(channelId);
            // Released to firmware control; drop the manual duty so GetAll reports Auto.
            if (hasPort) _pendingDuty[port] = 0;
        }
        if (!_hub.IsConnected) return;
        if (hasPort)
        {
            _hub.SetReleaseMode(port);
        }
    }

    public void ReleaseAll()
    {
        lock (_ctrlLock)
        {
            _softwareControlled.Clear();
            System.Array.Clear(_pendingDuty);
        }
        if (!_hub.IsConnected) return;
        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            _hub.SetReleaseMode(p);
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
        if (!_hub.IsConnected) return Array.Empty<CoolingComponent>();
        var lianli = _store.Load().Devices.LianLi;
        var deviceId = _hub.DeviceId;
        var modelLabel = _hub.ModelName.Length > 0 ? _hub.ModelName : "SL-Infinity";
        var devices = new List<CoolingDevice>(LianLiProtocol.PortCount);
        for (var p = 0; p < LianLiProtocol.PortCount; p++)
        {
            // Only ports with fans are surfaced (matches GetFanChannels).
            if (lianli.GetFans(p) <= 0) continue;
            var rpm = _hub.State.Rpm[p];
            int duty;
            lock (_ctrlLock) duty = _pendingDuty[p];
            devices.Add(new CoolingDevice
            {
                Id = $"lianli:port{p}",
                Name = $"{modelLabel} Port {p + 1}",
                Type = "Fan",
                Rpm = rpm >= 0 ? rpm : 0,
                Pwm = duty,
            });
        }
        return new[]
        {
            new CoolingComponent
            {
                Id = deviceId,
                Name = $"Lian Li {modelLabel}",
                Type = "LianLiHub",
                Devices = devices,
            },
        };
    }

    // ── Internals ──

    public static bool IsLianLiId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("lianli:", StringComparison.Ordinal);

    private void ApplyChannelWrite(string channelId, int dutyPercent)
    {
        if (!IsLianLiId(channelId)) return;
        if (!_hub.IsConnected)
        {
            ServiceLog.Warn($"[lianli-cooling] write to {channelId} dropped: hub not connected");
            return;
        }
        if (!TryParsePort(channelId, out var port)) return;
        bool wasControlled;
        lock (_ctrlLock)
        {
            wasControlled = _softwareControlled.Contains(channelId);
            _softwareControlled.Add(channelId);
            _pendingDuty[port] = dutyPercent;
        }
        // SetSpeed re-enters manual mode (mode write + settle + duty write): use it
        // only on the first write to take the port off mobo PWM. Subsequent writes
        // use SetDuty (duty write only) so re-entry does not reset the fan to default.
        var ok = wasControlled
            ? _hub.SetDuty(port, dutyPercent)
            : _hub.SetSpeed(port, dutyPercent);
        if (!ok)
        {
            ServiceLog.Warn($"[lianli-cooling] {(wasControlled ? "SetDuty" : "SetSpeed")} port {port} duty {dutyPercent} returned false");
        }
    }

    public void ReassertControl()
    {
        if (!_hub.IsConnected) return;
        // Across sleep the hub drops manual mode, so the first re-assert after a
        // resume re-enters it; every other one is speed-only, as SetSpeed's
        // re-entry resets the fan to its default each tick. A resume is consumed
        // only once every port took the re-entry.
        var resumeEpoch = _hub.ResumeEpoch;
        int[] ports;
        int[] duties;
        lock (_ctrlLock)
        {
            if (_softwareControlled.Count == 0)
            {
                // Nothing to re-enter: a port taken over later starts with SetSpeed anyway.
                _resumeEpochSeen = resumeEpoch;
                return;
            }
            ports = new int[_softwareControlled.Count];
            duties = new int[_softwareControlled.Count];
            var idx = 0;
            foreach (var id in _softwareControlled)
            {
                if (TryParsePort(id, out var p))
                {
                    ports[idx] = p;
                    duties[idx] = _pendingDuty[p];
                    idx++;
                }
            }
            if (idx < ports.Length)
            {
                Array.Resize(ref ports, idx);
                Array.Resize(ref duties, idx);
            }
        }
        var reenter = resumeEpoch != _resumeEpochSeen;
        var allOk = true;
        for (var i = 0; i < ports.Length; i++)
        {
            var ok = reenter ? _hub.SetSpeed(ports[i], duties[i]) : _hub.SetDuty(ports[i], duties[i]);
            allOk &= ok;
            if (!ok)
            {
                ServiceLog.Warn($"[lianli-cooling] ReassertControl port {ports[i]} duty {duties[i]} returned false");
            }
        }
        if (allOk)
        {
            _resumeEpochSeen = resumeEpoch;
        }
    }

    private static bool TryParsePort(string channelId, out int port)
    {
        port = 0;
        // channelId shape: "lianli:port{N}"
        var portIdx = channelId.LastIndexOf(':');
        if (portIdx < 0) return false;
        var seg = channelId.AsSpan(portIdx + 1);
        if (!seg.StartsWith("port", StringComparison.Ordinal)) return false;
        if (!int.TryParse(seg.Slice(4), out port)) return false;
        // Guard the int[]/SetSpeed indexers: the curve path (DriveFanSpeed) can
        // hand a stale id like "lianli:port7" that the routes never validate.
        return port >= 0 && port < LianLiProtocol.PortCount;
    }
}
