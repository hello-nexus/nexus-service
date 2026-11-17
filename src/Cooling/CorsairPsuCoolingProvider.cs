using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Corsair.Psu;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>One PSU temperature sensor as LHM last read it.</summary>
public readonly record struct PsuTemperature(string Name, float Celsius);

/// <summary>The fan of a digital Corsair PSU as channel "corsair-psu:1"; hardware mode until a duty is written, floor 30, fails closed to hardware mode.</summary>
public sealed class CorsairPsuCoolingProvider : IFanControlProvider, ICoolingProvider, IDisposable
{
    private const string IdPrefix = "corsair-psu:";
    private const string ChannelId = IdPrefix + "1";
    private const string PortLabel = "PSU Fan";
    private const long ProbeIntervalMs = 10_000;
    private const long RpmIntervalMs = 5_000;
    private const int DefaultGateWaitMs = 1_500;

    // LHM does not expose the PSU's own critical temperature, so software mode ends at a fixed margin.
    private const float ThermalTripC = 75f;
    private const float ThermalClearC = 65f;

    private readonly ICorsairPsuTransport _transport;
    private readonly Func<bool> _hostReady;
    private readonly Func<long> _nowMs;
    private readonly Func<IReadOnlyList<PsuTemperature>>? _psuTemperatures;
    private readonly int _gateWaitMs;

    // Mutated under CorsairPsuBus.Gate. _device, _lastDuty, _rpm and _rpmKnown are also written under
    // _stateLock because GetFanChannels snapshots them without the gate.
    private readonly object _stateLock = new();
    private CorsairPsuInfo? _device;
    private volatile bool _initialized;
    private bool _startupChecked;
    private volatile bool _softwareMode;
    private volatile bool _reassertMode;
    private volatile bool _failedClosed;
    private volatile bool _restoreWanted;
    private volatile bool _thermalHold;
    private volatile bool _notReadyLogged;
    private int _lastDuty;
    private int _rpm;
    private bool _rpmKnown;
    private long _nextProbeMs;
    private long _nextRpmMs;
    private long _nextRestoreMs;
    private volatile bool _disposed;

    public CorsairPsuCoolingProvider(
        ICorsairPsuTransport transport,
        Func<bool> hostReady,
        Func<long>? nowMs = null,
        Func<IReadOnlyList<PsuTemperature>>? psuTemperatures = null,
        int gateWaitMs = DefaultGateWaitMs)
    {
        _transport = transport;
        _hostReady = hostReady;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _psuTemperatures = psuTemperatures;
        _gateWaitMs = gateWaitMs;
    }

    public static bool IsCorsairPsuId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        Refresh();
        lock (_stateLock)
        {
            if (_device is not { } dev)
            {
                return Array.Empty<FanChannel>();
            }
            return new[]
            {
                new FanChannel
                {
                    Id = ChannelId,
                    Name = PortLabel,
                    DutyPercent = _softwareMode ? _lastDuty : 0,
                    Rpm = _rpmKnown ? _rpm : 0,
                    Mode = _softwareMode ? FanModes.Manual : FanModes.Auto,
                    Kind = FanKinds.Fan,
                    MinDuty = CorsairPsuProtocol.MinDuty,
                    RpmUnavailable = !_rpmKnown,
                    ReadOnly = _failedClosed,
                    DeviceId = ChannelId,
                    DeviceName = ModelName(dev),
                    PortLabel = PortLabel,
                },
            };
        }
    }

    // Curve inputs only: no DeviceId, so monitoring keeps showing these under LHM's own PSU group.
    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        if (!_hostReady())
        {
            return Array.Empty<TemperatureSource>();
        }
        var sources = new List<TemperatureSource>();
        foreach (var (id, t) in IdentifiedTemperatures())
        {
            sources.Add(new TemperatureSource
            {
                Id = id,
                Name = t.Name.StartsWith("PSU", StringComparison.OrdinalIgnoreCase) ? t.Name : "PSU " + t.Name,
                Category = "PSU",
                Value = t.Celsius,
            });
        }
        return sources;
    }

    public float? ReadTemperature(string sensorId)
    {
        if (!IsCorsairPsuId(sensorId) || !_hostReady())
        {
            return null;
        }
        foreach (var (id, t) in IdentifiedTemperatures())
        {
            if (string.Equals(id, sensorId, StringComparison.Ordinal))
            {
                return t.Celsius;
            }
        }
        return null;
    }

    private IEnumerable<(string Id, PsuTemperature Sensor)> IdentifiedTemperatures()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in ReadPsuTemperatures())
        {
            if (!float.IsFinite(t.Celsius))
            {
                continue;
            }
            var slug = new string(Array.FindAll(t.Name.ToLowerInvariant().ToCharArray(), char.IsAsciiLetterOrDigit));
            var id = $"{IdPrefix}1:temp:{(slug.Length == 0 ? "sensor" : slug)}";
            for (var n = 2; !seen.Add(id); n++)
            {
                id = $"{IdPrefix}1:temp:{slug}-{n}";
            }
            yield return (id, t);
        }
    }

    public int SetFanSpeed(string channelId, int dutyPercent) => WriteDuty(channelId, dutyPercent);

    public void DriveFanSpeed(string channelId, int dutyPercent) => WriteDuty(channelId, dutyPercent);

    public void ReleaseFan(string channelId)
    {
        if (IsCorsairPsuId(channelId))
        {
            RestoreHardware("release");
        }
    }

    public bool ReleaseRestoresHardwareControl(string channelId) => IsCorsairPsuId(channelId);

    public void ReleaseAll() => RestoreHardware("release all");

    // The mode and the session may not survive sleep. Stays recorded as software so a release still restores hardware mode.
    public void RetakeControlAfterResume()
    {
        _reassertMode = true;
        _initialized = false;
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());

    // ── ICoolingProvider ──

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var channels = GetFanChannels();
        if (channels.Count == 0)
        {
            return Array.Empty<CoolingComponent>();
        }
        var c = channels[0];
        return new[]
        {
            new CoolingComponent
            {
                Id = c.DeviceId ?? ChannelId,
                Name = c.DeviceName ?? "Corsair PSU",
                Type = "CorsairPsu",
                Devices = new List<CoolingDevice>
                {
                    new CoolingDevice
                    {
                        Id = c.Id,
                        Name = c.Name,
                        Type = "Fan",
                        Rpm = c.Rpm,
                        Pwm = c.DutyPercent,
                    },
                },
            },
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        RestoreHardware("dispose");
        _disposed = true;
    }

    // ── internals ──

    private static string ModelName(CorsairPsuInfo dev) =>
        CorsairPsuProtocol.Models.TryGetValue(dev.ProductId, out var name) ? name : "Corsair PSU";

    private bool TryEnterGate() => Monitor.TryEnter(CorsairPsuBus.Gate, _gateWaitMs);

    private static void ExitGate() => Monitor.Exit(CorsairPsuBus.Gate);

    private bool NeedsRestore => _softwareMode && (_failedClosed || _restoreWanted || _thermalHold);

    /// <summary>Tracks presence, retries a pending hardware-mode restore, and reads the fan speed, each on its own interval.</summary>
    private void Refresh()
    {
        if (!_hostReady() || _disposed || !TryEnterGate())
        {
            return;
        }
        try
        {
            var now = _nowMs();
            if (now >= _nextProbeMs)
            {
                _nextProbeMs = now + ProbeIntervalMs;
                Probe();
            }
            if (_device is null)
            {
                return;
            }
            CheckThermal();
            if (NeedsRestore && now >= _nextRestoreMs)
            {
                _nextRestoreMs = now + ProbeIntervalMs;
                TryRestoreHardware("retry");
            }
            if (_failedClosed || !EnsureSession() || now < _nextRpmMs)
            {
                return;
            }
            _nextRpmMs = now + RpmIntervalMs;
            if (Exchange(CorsairPsuProtocol.BuildReadFanSpeed()) is not { } reply)
            {
                return;
            }
            var rpm = CorsairPsuProtocol.ParseRpm(reply);
            lock (_stateLock)
            {
                _rpmKnown = rpm is not null;
                _rpm = rpm ?? 0;
            }
        }
        finally
        {
            ExitGate();
        }
    }

    private void Probe()
    {
        var found = _transport.Find();
        if (found is null)
        {
            if (_device is not null)
            {
                ServiceLog.Warn("[corsair-psu] device removed");
                ResetSession();
            }
            return;
        }
        if (_device is not null && !string.Equals(_device.Path, found.Path, StringComparison.Ordinal))
        {
            ServiceLog.Warn("[corsair-psu] device re-enumerated under a new path");
            ResetSession();
        }
        if (_device is null)
        {
            lock (_stateLock)
            {
                _device = found;
            }
            ServiceLog.Info($"[corsair-psu] found {ModelName(found)}");
        }
    }

    private IReadOnlyList<PsuTemperature> ReadPsuTemperatures()
    {
        try
        {
            return _psuTemperatures?.Invoke() ?? Array.Empty<PsuTemperature>();
        }
        catch (Exception)
        {
            return Array.Empty<PsuTemperature>();
        }
    }

    private float? PsuTemperatureC()
    {
        float? max = null;
        foreach (var t in ReadPsuTemperatures())
        {
            if (float.IsFinite(t.Celsius) && (max is null || t.Celsius > max))
            {
                max = t.Celsius;
            }
        }
        return max;
    }

    /// <summary>Hands the fan to the PSU while it runs hot, and holds writes off until it cools.</summary>
    private void CheckThermal()
    {
        if (PsuTemperatureC() is not { } temp)
        {
            return;
        }
        if (!_thermalHold && temp >= ThermalTripC)
        {
            _thermalHold = true;
            ServiceLog.Warn($"[corsair-psu] PSU temperature {temp:0} C reached the guard, returning the fan to the PSU");
            TryRestoreHardware("thermal guard");
        }
        else if (_thermalHold && temp <= ThermalClearC)
        {
            _thermalHold = false;
            ServiceLog.Info("[corsair-psu] PSU temperature back in range, software control allowed");
        }
    }

    private int WriteDuty(string channelId, int dutyPercent)
    {
        var duty = CorsairPsuProtocol.ClampDuty(dutyPercent);
        if (!IsCorsairPsuId(channelId))
        {
            return duty;
        }
        if (!_hostReady())
        {
            if (!_notReadyLogged)
            {
                _notReadyLogged = true;
                ServiceLog.Warn("[corsair-psu] writes dropped until the sensor library has opened");
            }
            return duty;
        }
        Refresh();
        if (!TryEnterGate())
        {
            return duty;
        }
        try
        {
            if (_disposed || _device is null || _failedClosed)
            {
                return duty;
            }
            CheckThermal();
            if (_thermalHold || !EnsureSession())
            {
                return duty;
            }
            if (!_softwareMode || _reassertMode)
            {
                if (Exchange(CorsairPsuProtocol.BuildSetSoftwareMode()) is null)
                {
                    return duty;
                }
                if (!ReadMode(out var software) || !software)
                {
                    FailClosed("software mode did not take");
                    return duty;
                }
                _softwareMode = true;
                _reassertMode = false;
                ServiceLog.Info("[corsair-psu] fan mode: software");
            }
            if (Exchange(CorsairPsuProtocol.BuildSetDuty(duty)) is null)
            {
                return duty;
            }
            _restoreWanted = false;
            lock (_stateLock)
            {
                _lastDuty = duty;
            }
        }
        finally
        {
            ExitGate();
        }
        return duty;
    }

    private void RestoreHardware(string why)
    {
        _restoreWanted = true;
        if (!TryEnterGate())
        {
            ServiceLog.Warn($"[corsair-psu] hardware mode restore ({why}) waiting on the PSU bus, will retry");
            return;
        }
        try
        {
            TryRestoreHardware(why);
        }
        finally
        {
            ExitGate();
        }
    }

    /// <summary>Caller holds the gate. Leaves the restore pending when the write does not read back.</summary>
    private void TryRestoreHardware(string why)
    {
        if (_device is null || !_softwareMode)
        {
            _restoreWanted = false;
            return;
        }
        if (WriteHardwareMode())
        {
            _softwareMode = false;
            _restoreWanted = false;
            ServiceLog.Info($"[corsair-psu] fan mode: hardware ({why})");
        }
        else
        {
            _restoreWanted = true;
            _failedClosed = true;
        }
    }

    private void ResetSession()
    {
        lock (_stateLock)
        {
            _device = null;
            _rpmKnown = false;
            _rpm = 0;
        }
        _initialized = false;
        _startupChecked = false;
        _softwareMode = false;
        _reassertMode = false;
        _failedClosed = false;
        _restoreWanted = false;
        _thermalHold = false;
        _nextRpmMs = 0;
    }

    /// <summary>Handshake once per session, then hand a fan left in software mode back to the PSU.</summary>
    private bool EnsureSession()
    {
        if (_device is null || _failedClosed)
        {
            return false;
        }
        if (!_initialized)
        {
            // The init reply carries no echo and is discarded.
            if (_transport.Exchange(_device, CorsairPsuProtocol.BuildInit()) is null)
            {
                FailClosed("no reply to init");
                return false;
            }
            _initialized = true;
        }
        if (!_startupChecked)
        {
            if (!ReadMode(out var software))
            {
                return false;
            }
            if (software)
            {
                ServiceLog.Warn("[corsair-psu] fan found in software mode at start, restoring hardware mode");
                if (!WriteHardwareMode())
                {
                    _failedClosed = true;
                    return false;
                }
            }
            _startupChecked = true;
        }
        return true;
    }

    private bool ReadMode(out bool software)
    {
        software = false;
        var reply = Exchange(CorsairPsuProtocol.BuildReadMode());
        if (reply is null)
        {
            return false;
        }
        if (CorsairPsuProtocol.ParseSoftwareMode(reply) is not { } mode)
        {
            FailClosed("unreadable fan mode");
            return false;
        }
        software = mode;
        return true;
    }

    /// <summary>Writes hardware mode and reads it back. Never recurses into <see cref="FailClosed"/>.</summary>
    private bool WriteHardwareMode()
    {
        var device = _device;
        if (device is null)
        {
            return false;
        }
        var cmd = CorsairPsuProtocol.BuildSetHardwareMode();
        var reply = _transport.Exchange(device, cmd);
        if (reply is null || !CorsairPsuProtocol.IsEchoOf(cmd, reply))
        {
            ServiceLog.Error("[corsair-psu] hardware mode write failed");
            return false;
        }
        var read = CorsairPsuProtocol.BuildReadMode();
        var check = _transport.Exchange(device, read);
        if (check is null || !CorsairPsuProtocol.IsEchoOf(read, check) || CorsairPsuProtocol.ParseSoftwareMode(check) is not false)
        {
            ServiceLog.Error("[corsair-psu] hardware mode did not read back");
            return false;
        }
        return true;
    }

    /// <summary>One validated exchange. A missing or non-echoing reply fails closed and returns null.</summary>
    private byte[]? Exchange(byte[] command)
    {
        var device = _device;
        if (device is null)
        {
            return null;
        }
        var reply = _transport.Exchange(device, command);
        if (reply is null)
        {
            FailClosed("no reply");
            return null;
        }
        if (!CorsairPsuProtocol.IsEchoOf(command, reply))
        {
            FailClosed("reply did not echo the command");
            return null;
        }
        return reply;
    }

    private void FailClosed(string why)
    {
        if (_failedClosed)
        {
            return;
        }
        _failedClosed = true;
        ServiceLog.Error($"[corsair-psu] {why}; writes stopped for this session");
        if (_softwareMode && WriteHardwareMode())
        {
            _softwareMode = false;
            ServiceLog.Info("[corsair-psu] fan mode: hardware (fail closed)");
        }
    }
}
