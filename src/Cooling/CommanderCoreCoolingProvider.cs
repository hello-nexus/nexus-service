using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Cooling;

/// <summary>
/// Commander Core family cooling through the bundled OpenRGB daemon, the only process that opens the
/// device. Ids are "cmdcore:{device}:ch{N}" and ":t{N}" with N the device's own port or probe index.
/// Port 0 is the pump except on the Core XT; probe 0 is the coolant sensor where there is a pump.
/// Once a channel is commanded the whole table is written, uncommanded connected ports at a safe default.
/// </summary>
public sealed class CommanderCoreCoolingProvider : IFanControlProvider, ICoolingProvider
{
    public const string IdPrefix = "cmdcore:";

    private const string NamePrefix = "Corsair Commander Core";
    private const int CoreXtProductId = 0x0C2A;
    private const int DefaultFanDuty = 50;
    private const int DefaultPumpDuty = 70;
    private const int PumpFloor = 50;
    private static readonly TimeSpan StatusStaleAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RepushAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ApplySettle = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WarnEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UnsupportedRetry = TimeSpan.FromMinutes(1);

    public readonly record struct Endpoint(string StableId, string Name, int Address);

    public interface ISource
    {
        IReadOnlyList<Endpoint> Endpoints();
    }

    private sealed class Device
    {
        public required string Key { get; init; }
        public required string Id { get; init; }
        public string Name = "";
        public int Address = -1;
        public CommanderCoreStatus? Status;
        public DateTime StatusAtUtc = DateTime.MinValue;
        public DateTime UnsupportedAtUtc = DateTime.MinValue;
        public bool Unsupported;
        public bool Dirty;
        public DateTime PushedAtUtc = DateTime.MinValue;
        public DateTime WarnedAtUtc = DateTime.MinValue;
    }

    private readonly ISource _source;
    private readonly INexusCoolingTransport _transport;
    private readonly IConfigStore? _config;
    private readonly Func<DateTime> _now;
    private readonly SemaphoreSlim _wake = new(0, 1);

    private readonly object _lock = new();
    private Dictionary<string, Device> _devices = new(StringComparer.Ordinal);
    // Keyed by channel id so a duty survives a daemon restart that renumbers the controller.
    private readonly Dictionary<string, int> _desired = new(StringComparer.Ordinal);
    // Devices (by StableId) whose daemon holds a commanded table not yet handed back; outlives a device dropping out of the list.
    private readonly HashSet<string> _commanded = new(StringComparer.Ordinal);

    public CommanderCoreCoolingProvider(ISource source, INexusCoolingTransport transport, IConfigStore? config = null, Func<DateTime>? now = null)
    {
        _source = source;
        _transport = transport;
        _config = config;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public static bool IsCommanderCoreId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(IdPrefix, StringComparison.Ordinal);

    public static bool IsCommanderCoreName(string name) =>
        name.StartsWith(NamePrefix, StringComparison.Ordinal);

    public bool HasDevices
    {
        get
        {
            lock (_lock) return _devices.Count > 0;
        }
    }

    // ── polling (the hosted worker calls these) ──

    /// <summary>Completes after <paramref name="delay"/> or as soon as a duty write is waiting to be sent.</summary>
    public async Task WaitForWorkAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        { await _wake.WaitAsync(delay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        var endpoints = _source.Endpoints();
        Device[] devices;
        lock (_lock)
        {
            var next = new Dictionary<string, Device>(StringComparer.Ordinal);
            foreach (var ep in endpoints)
            {
                if (!_devices.TryGetValue(ep.StableId, out var dev))
                {
                    dev = new Device { Key = ep.StableId, Id = IdPrefix + ShortHash(ep.StableId) };
                }
                if (dev.Address != ep.Address)
                {
                    // A restarted daemon forgets what it was told.
                    dev.Address = ep.Address;
                    dev.Unsupported = false;
                    dev.Status = null;
                    dev.Dirty = true;
                }
                dev.Name = ep.Name;
                next[ep.StableId] = dev;
            }
            _devices = next;
            devices = new Device[next.Count];
            next.Values.CopyTo(devices, 0);
        }

        foreach (var dev in devices)
        {
            ct.ThrowIfCancellationRequested();
            await PollDeviceAsync(dev, ct).ConfigureAwait(false);
        }
    }

    private async Task PollDeviceAsync(Device dev, CancellationToken ct)
    {
        var now = _now();
        if (dev.Unsupported)
        {
            if (now - dev.UnsupportedAtUtc < UnsupportedRetry)
            {
                return;
            }
            dev.Unsupported = false;
        }

        var result = await _transport.GetCoolingStatusAsync(dev.Address, ct).ConfigureAwait(false);
        now = _now();
        lock (_lock)
        {
            switch (result.Outcome)
            {
                case NexusCoolingOutcome.Ok when result.Status is { Valid: true } status:
                    dev.Status = status;
                    dev.StatusAtUtc = now;
                    break;
                case NexusCoolingOutcome.Unsupported:
                    dev.Unsupported = true;
                    dev.UnsupportedAtUtc = now;
                    dev.Status = null;
                    break;
                default:
                    if (now - dev.StatusAtUtc > StatusStaleAfter)
                    {
                        dev.Status = null;
                    }
                    break;
            }
        }

        if (dev.Status is not null)
        {
            await PushDutiesAsync(dev, ct).ConfigureAwait(false);
        }
    }

    // The daemon rewrites an unchanged table on its own; a mismatch with its applied duty (restart, lost
    // device) is re-sent here at most every RepushAfter.
    private async Task PushDutiesAsync(Device dev, CancellationToken ct)
    {
        List<(int Channel, int Duty)> table;
        var handBack = false;
        lock (_lock)
        {
            if (dev.Status is not { } status)
            {
                return;
            }
            table = BuildTable(dev, status, out var anyControlled);
            var now = _now();
            if (!anyControlled)
            {
                // Nothing is commanded: an empty table hands back whatever the daemon still holds, whether this
                // service commanded it or the daemon reports a duty that is not the hand-back one.
                var owed = _commanded.Contains(dev.Key);
                var anyApplied = false;
                var stray = false;
                for (var i = 0; i < status.Channels.Count; i++)
                {
                    if (!status.Channels[i].Connected || status.Channels[i].AppliedDuty is not { } applied)
                    {
                        continue;
                    }
                    anyApplied = true;
                    stray |= applied != (IsPump(status, i) ? DefaultPumpDuty : DefaultFanDuty);
                }
                if (owed && !anyApplied)
                {
                    // Once the daemon has had time to apply the table, nothing applied means it restarted and
                    // forgot it; the device runs its own configuration.
                    if (now - dev.PushedAtUtc >= ApplySettle)
                    {
                        _commanded.Remove(dev.Key);
                    }
                    return;
                }
                if (!(owed && dev.Dirty) && !(stray && now - dev.PushedAtUtc >= RepushAfter))
                {
                    return;
                }
                handBack = true;
            }
            else
            {
                var mismatch = false;
                foreach (var (channel, duty) in table)
                {
                    if (status.Channels[channel].AppliedDuty != duty)
                    {
                        mismatch = true;
                        break;
                    }
                }
                if (!dev.Dirty && !(mismatch && now - dev.PushedAtUtc >= RepushAfter))
                {
                    return;
                }
            }
            dev.Dirty = false;
            dev.PushedAtUtc = now;
        }

        var outcome = await _transport.SetCoolingDutiesAsync(dev.Address, handBack ? new List<(int Channel, int Duty)>() : table, ct).ConfigureAwait(false);
        lock (_lock)
        {
            if (outcome == NexusCoolingOutcome.Ok)
            {
                if (handBack)
                {
                    _commanded.Remove(dev.Key);
                }
                else
                {
                    _commanded.Add(dev.Key);
                }
                return;
            }
            dev.Dirty = true;
            if (_now() - dev.WarnedAtUtc < WarnEvery)
            {
                return;
            }
            dev.WarnedAtUtc = _now();
        }
        ServiceLog.Warn($"[cmdcore-cooling] duty write to {dev.Id} not accepted ({outcome})");
    }

    private List<(int Channel, int Duty)> BuildTable(Device dev, CommanderCoreStatus status, out bool anyControlled)
    {
        anyControlled = false;
        var table = new List<(int, int)>();
        for (var i = 0; i < status.Channels.Count; i++)
        {
            if (!status.Channels[i].Connected)
            {
                continue;
            }
            var pump = IsPump(status, i);
            if (_desired.TryGetValue(ChannelId(dev, i), out var duty))
            {
                anyControlled = true;
                if (pump)
                {
                    duty = Math.Max(duty, PumpFloor);
                }
            }
            else
            {
                duty = pump ? DefaultPumpDuty : DefaultFanDuty;
            }
            table.Add((i, duty));
        }
        return table;
    }

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        var channels = new List<FanChannel>();
        var curveBound = CurveModes.BoundFanIds(_config);
        lock (_lock)
        {
            foreach (var dev in _devices.Values)
            {
                if (dev.Status is not { } status || Stale(dev))
                {
                    continue;
                }
                for (var i = 0; i < status.Channels.Count; i++)
                {
                    var ch = status.Channels[i];
                    if (!ch.Connected)
                    {
                        continue;
                    }
                    var id = ChannelId(dev, i);
                    var pump = IsPump(status, i);
                    var commanded = _desired.TryGetValue(id, out var wanted);
                    channels.Add(new FanChannel
                    {
                        Id = id,
                        Name = ChannelName(status, i),
                        DutyPercent = commanded ? wanted : ch.AppliedDuty ?? (pump ? DefaultPumpDuty : DefaultFanDuty),
                        Rpm = ch.Rpm,
                        Mode = curveBound.Contains(id) ? FanModes.Curve : (commanded ? FanModes.Manual : FanModes.Auto),
                        Kind = pump ? FanKinds.Pump : FanKinds.Fan,
                        MinDuty = pump ? PumpFloor : null,
                        DeviceId = dev.Id,
                        DeviceName = dev.Name,
                        PortLabel = PortLabel(status, i),
                    });
                }
            }
        }
        return channels;
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        var sources = new List<TemperatureSource>();
        lock (_lock)
        {
            foreach (var dev in _devices.Values)
            {
                if (dev.Status is not { } status || Stale(dev))
                {
                    continue;
                }
                for (var i = 0; i < status.Temperatures.Count; i++)
                {
                    if (!status.Temperatures[i].Connected)
                    {
                        continue;
                    }
                    sources.Add(new TemperatureSource
                    {
                        Id = TemperatureId(dev, i),
                        Name = TemperatureName(status, i),
                        Category = "Hub",
                        Value = status.Temperatures[i].Celsius,
                        DeviceId = dev.Id,
                        DeviceName = dev.Name,
                    });
                }
            }
        }
        return sources;
    }

    public float? ReadTemperature(string sensorId)
    {
        if (!IsCommanderCoreId(sensorId))
        {
            return null;
        }
        lock (_lock)
        {
            foreach (var dev in _devices.Values)
            {
                if (dev.Status is not { } status || Stale(dev))
                {
                    continue;
                }
                for (var i = 0; i < status.Temperatures.Count; i++)
                {
                    if (status.Temperatures[i].Connected && sensorId == TemperatureId(dev, i))
                    {
                        return status.Temperatures[i].Celsius;
                    }
                }
            }
        }
        return null;
    }

    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var duty = Command(channelId, dutyPercent);
        _config?.Update(s => s.Cooling.ManualSpeeds[channelId] = duty);
        return duty;
    }

    public void DriveFanSpeed(string channelId, int dutyPercent) => Command(channelId, dutyPercent);

    public void ReleaseFan(string channelId)
    {
        if (!IsCommanderCoreId(channelId))
        {
            return;
        }
        lock (_lock)
        {
            if (_desired.Remove(channelId) && TryFindDevice(channelId, out var dev))
            {
                dev.Dirty = true;
            }
        }
        Wake();
    }

    public void ReleaseAll()
    {
        lock (_lock)
        {
            if (_desired.Count == 0)
            {
                return;
            }
            _desired.Clear();
            foreach (var dev in _devices.Values)
            {
                dev.Dirty = true;
            }
        }
        Wake();
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());

    // ── ICoolingProvider ──

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var components = new List<CoolingComponent>();
        lock (_lock)
        {
            foreach (var dev in _devices.Values)
            {
                if (dev.Status is not { } status || Stale(dev))
                {
                    continue;
                }
                var devices = new List<CoolingDevice>();
                for (var i = 0; i < status.Channels.Count; i++)
                {
                    var ch = status.Channels[i];
                    if (!ch.Connected)
                    {
                        continue;
                    }
                    var id = ChannelId(dev, i);
                    devices.Add(new CoolingDevice
                    {
                        Id = id,
                        Name = ChannelName(status, i),
                        Type = IsPump(status, i) ? "Pump" : "Fan",
                        Rpm = ch.Rpm,
                        Pwm = _desired.TryGetValue(id, out var wanted) ? wanted : ch.AppliedDuty,
                    });
                }
                components.Add(new CoolingComponent
                {
                    Id = dev.Id,
                    Name = dev.Name,
                    Type = "CorsairCommander",
                    Devices = devices,
                });
            }
        }
        return components;
    }

    // ── internals ──

    private int Command(string channelId, int dutyPercent)
    {
        var duty = Math.Clamp(dutyPercent, 0, 100);
        if (!IsCommanderCoreId(channelId))
        {
            return duty;
        }
        lock (_lock)
        {
            if (!TryFindDevice(channelId, out var dev) || dev.Status is not { } status)
            {
                // Not listed yet (service start, a bridge blip): keep the command for the first contact.
                if (!IsChannelId(channelId))
                {
                    return duty;
                }
                // Port 0 is the pump on every model but one; the table build lifts it again where it is not.
                if (channelId.EndsWith(":ch0", StringComparison.Ordinal))
                {
                    duty = Math.Max(duty, PumpFloor);
                }
                _desired[channelId] = duty;
                if (TryFindDevice(channelId, out var known))
                {
                    known.Dirty = true;
                }
                return duty;
            }
            if (!TryChannelIndex(dev, channelId, out var index))
            {
                ServiceLog.Warn($"[cmdcore-cooling] write to {channelId} dropped: no such connected channel");
                return duty;
            }
            if (IsPump(status, index))
            {
                duty = Math.Max(duty, PumpFloor);
            }
            if (_desired.TryGetValue(channelId, out var current) && current == duty)
            {
                return duty;
            }
            _desired[channelId] = duty;
            dev.Dirty = true;
        }
        Wake();
        return duty;
    }

    private void Wake()
    {
        try
        { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private bool Stale(Device dev) => _now() - dev.StatusAtUtc > StatusStaleAfter;

    // "cmdcore:{8 hex}:ch{N}" with N inside the daemon's channel cap.
    private static bool IsChannelId(string id)
    {
        var parts = id.Split(':');
        if (parts.Length != 3 || parts[0] + ":" != IdPrefix || parts[1].Length != 8 || !parts[2].StartsWith("ch", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var c in parts[1])
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }
        var number = parts[2].AsSpan(2);
        return number.Length > 0 && (number.Length == 1 || number[0] != '0')
            && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            && n < NexusCoolingProtocol.MaxChannels;
    }

    private bool TryFindDevice(string channelId, out Device dev)
    {
        foreach (var d in _devices.Values)
        {
            if (channelId.StartsWith(d.Id + ":", StringComparison.Ordinal))
            {
                dev = d;
                return true;
            }
        }
        dev = null!;
        return false;
    }

    private static bool TryChannelIndex(Device dev, string channelId, out int index)
    {
        index = -1;
        var prefix = dev.Id + ":ch";
        return channelId.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(channelId.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out index)
            && dev.Status is { } status
            && index < status.Channels.Count
            && status.Channels[index].Connected;
    }

    private static string ChannelId(Device dev, int index) => $"{dev.Id}:ch{index}";

    private static string TemperatureId(Device dev, int index) => $"{dev.Id}:t{index}";

    private static bool HasPump(CommanderCoreStatus status) => status.ProductId != CoreXtProductId;

    private static bool IsPump(CommanderCoreStatus status, int index) => index == 0 && HasPump(status);

    private static int FanNumber(CommanderCoreStatus status, int index) => HasPump(status) ? index : index + 1;

    private static string ChannelName(CommanderCoreStatus status, int index) =>
        IsPump(status, index) ? "Pump" : $"Fan {FanNumber(status, index)}";

    private static string PortLabel(CommanderCoreStatus status, int index) =>
        IsPump(status, index) ? "AIO port" : $"Fan port {FanNumber(status, index)}";

    private static string TemperatureName(CommanderCoreStatus status, int index) =>
        HasPump(status)
            ? (index == 0 ? "Coolant temperature" : $"Temperature probe {index}")
            : $"Temperature probe {index + 1}";

    // FNV-1a: string.GetHashCode is randomized per process and the id must survive restarts.
    private static string ShortHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }
        return hash.ToString("x8", CultureInfo.InvariantCulture);
    }
}
