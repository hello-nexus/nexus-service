using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.Corsair.Psu;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>
/// The fan of a digital Corsair PSU as one controllable channel "corsair-psu:{n}". The PSU runs its own fan
/// curve (hardware mode) until Nexus writes a duty, which switches it to software mode; every release path
/// writes hardware mode back. Duty is never written below <see cref="CorsairPsuProtocol.MinDuty"/>.
///
/// Fail closed: a reply that does not echo its command, an unreadable mode or a failed write stops all
/// further writes for the session and hands the fan back to the PSU. Every exchange holds
/// <see cref="CorsairPsuBus.Gate"/> so it cannot interleave with the sensor library's reads.
/// </summary>
public sealed class CorsairPsuCoolingProvider : IFanControlProvider, ICoolingProvider, IDisposable
{
    private const string IdPrefix = "corsair-psu:";
    private const string ChannelId = IdPrefix + "1";
    private const string PortLabel = "PSU Fan";
    private const long ProbeIntervalMs = 10_000;
    private const long RpmIntervalMs = 5_000;

    private readonly ICorsairPsuTransport _transport;
    private readonly Func<bool> _hostReady;
    private readonly Func<long> _nowMs;

    // Written only under CorsairPsuBus.Gate; read by the snapshot methods under _stateLock.
    private readonly object _stateLock = new();
    private CorsairPsuInfo? _device;
    private bool _initialized;
    private bool _startupChecked;
    private bool _softwareMode;
    private bool _reassertMode;
    private bool _failedClosed;
    private int _lastDuty;
    private int _rpm;
    private bool _rpmKnown;
    private long _nextProbeMs;
    private long _nextRpmMs;
    private bool _disposed;

    public CorsairPsuCoolingProvider(ICorsairPsuTransport transport, Func<bool> hostReady, Func<long>? nowMs = null)
    {
        _transport = transport;
        _hostReady = hostReady;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
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

    public IReadOnlyList<TemperatureSource> GetTemperatureSources() => Array.Empty<TemperatureSource>();

    public float? ReadTemperature(string sensorId) => null;

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

    // The mode may not have survived the sleep, so the next write sets it again. It stays recorded as
    // software so a release still writes hardware mode.
    public void RetakeControlAfterResume()
    {
        lock (CorsairPsuBus.Gate)
        {
            _reassertMode = true;
        }
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
        _disposed = true;
        RestoreHardware("dispose");
    }

    // ── internals ──

    private static string ModelName(CorsairPsuInfo dev) =>
        CorsairPsuProtocol.Models.TryGetValue(dev.ProductId, out var name) ? name : "Corsair PSU";

    private bool Ready => _hostReady();

    /// <summary>Tracks presence and reads the fan speed, each on its own interval.</summary>
    private void Refresh()
    {
        if (!Ready)
        {
            return;
        }
        var now = _nowMs();
        lock (CorsairPsuBus.Gate)
        {
            if (now >= _nextProbeMs)
            {
                _nextProbeMs = now + ProbeIntervalMs;
                var found = _transport.Find();
                if (found is null && _device is not null)
                {
                    ServiceLog.Warn("[corsair-psu] device removed");
                    ResetSession();
                }
                else if (found is not null && _device is null)
                {
                    lock (_stateLock)
                    {
                        _device = found;
                    }
                    ServiceLog.Info($"[corsair-psu] found {ModelName(found)}");
                }
            }
            if (_device is null || _failedClosed)
            {
                return;
            }
            if (!EnsureSession())
            {
                return;
            }
            if (now >= _nextRpmMs)
            {
                _nextRpmMs = now + RpmIntervalMs;
                var reply = Exchange(CorsairPsuProtocol.BuildReadFanSpeed());
                if (reply is null)
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
        }
    }

    private int WriteDuty(string channelId, int dutyPercent)
    {
        var duty = CorsairPsuProtocol.ClampDuty(dutyPercent);
        if (!IsCorsairPsuId(channelId))
        {
            return duty;
        }
        if (!Ready)
        {
            ServiceLog.Warn("[corsair-psu] write dropped: sensor library still opening");
            return duty;
        }
        Refresh();
        lock (CorsairPsuBus.Gate)
        {
            if (_disposed || _device is null || _failedClosed)
            {
                return duty;
            }
            if (!EnsureSession())
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
            lock (_stateLock)
            {
                _lastDuty = duty;
            }
        }
        return duty;
    }

    private void RestoreHardware(string why)
    {
        lock (CorsairPsuBus.Gate)
        {
            if (_device is null || !_softwareMode)
            {
                return;
            }
            // The session is already initialized or the mode could not have been set.
            if (WriteHardwareMode())
            {
                _softwareMode = false;
                ServiceLog.Info($"[corsair-psu] fan mode: hardware ({why})");
            }
            else
            {
                _failedClosed = true;
            }
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
        _nextRpmMs = 0;
    }

    /// <summary>Handshake once per device appearance, then hand a fan left in software mode back to the PSU.</summary>
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
