using System;
using System.Collections.Generic;
using System.Text;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Platform;

namespace Nexus.Service.Sensors.Astral;

/// <summary>
/// Per-adapter gate + cache for the Astral 12VHPWR supplement. Detection is
/// list-free: any NVIDIA GPU whose PCI subsystem vendor is ASUS gets probed
/// for the Astral power-monitor IC; a failed probe (non-Astral ASUS card, or
/// any NVAPI error) leaves the adapter with no synthesized sensors. AIB name
/// enrichment (<see cref="AstralAibVendors"/>) applies to every recognized
/// vendor regardless of whether the Astral probe ever succeeds.
///
/// State is keyed by the adapter index LibreHardwareMonitor's NvidiaGroup
/// assigns (parsed out of the LHM hardware Identifier, "/gpu-nvidia/{index}")
/// - the same index this instance's IAstralNvApiClient uses against its own
/// NvAPI_EnumPhysicalGPUs call, since LHM's public IHardware surface exposes
/// no PCI ids to match on directly.
/// </summary>
internal sealed class AstralGpuSupplement : IDisposable
{
    // The G-sensor retry delay doubles per miss up to this many doublings.
    private const int MaxGSensorBackoffShift = 6;
    // The GPU list counts as driving the read while it probed within this many intervals.
    private const int ExternalSilenceIntervals = 3;
    // A card that has not answered for this many intervals drops the fallback to the slow retry.
    private const int LapseIntervals = 60;
    private static readonly TimeSpan SlowRetry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TickWarnEvery = TimeSpan.FromMinutes(1);

    private readonly IAstralNvApiClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _probeInterval;
    private readonly AstralMonitor? _monitor;
    private readonly object _lock = new();
    private ITimer? _fallback;
    private bool _disposed;
    private bool _slow;
    private bool _lapseLogged;
    private DateTimeOffset _lastTickWarn = DateTimeOffset.MinValue;
    private readonly Dictionary<int, GpuState> _states = new();

    public AstralGpuSupplement(IAstralNvApiClient client, AstralMonitor? monitor = null)
        : this(client, TimeProvider.System, TimeSpan.FromSeconds(1), monitor)
    {
    }

    internal AstralGpuSupplement(IAstralNvApiClient client, TimeProvider timeProvider, TimeSpan probeInterval, AstralMonitor? monitor = null)
    {
        _monitor = monitor;
        _client = client;
        _timeProvider = timeProvider;
        _probeInterval = probeInterval;
    }

    private sealed class GpuState
    {
        public int AdapterIndex;
        public bool HasSubSystemId;
        public uint SubSystemId;
        public DateTimeOffset NextProbeUtc;
        public bool LoggedFirstAttempt;
        public bool LoggedFirstGSensor;
        public int GSensorMisses;
        public DateTimeOffset NextGSensorUtc;
        public string HwId = "";
        public string HwName = "";
        public string? Name;
        public string? CalibrationKey;
        public DateTimeOffset DrivenUtc;
        public DateTimeOffset LastGoodUtc;
        public AstralTelemetryParser.AstralReadout? LastReadout;
    }

    /// <summary>
    /// AIB-enriched display name for the NVIDIA GPU at <paramref name="hwIdentifier"/>.
    /// Returns <paramref name="lhmName"/> unchanged when the subsystem vendor
    /// can't be read or isn't recognized.
    /// </summary>
    public string EnrichName(string hwIdentifier, string lhmName)
    {
        // LibreHardwareSensorProvider is a DI singleton; GetGpus() is called
        // from both the monitoring broadcaster's background loop and HTTP
        // request threads, so all reads/writes of _states go through this lock.
        lock (_lock)
        {
            if (!TryGetState(hwIdentifier, out var state))
            {
                return lhmName;
            }
            return AstralAibVendors.EnrichName(lhmName, state.SubSystemId, state.LastReadout.HasValue);
        }
    }

    /// <summary>
    /// Appends the Astral 12VHPWR sensors to <paramref name="result"/> if this
    /// adapter's subsystem vendor is ASUS and a probe has ever succeeded.
    /// Probes at most once per <see cref="_probeInterval"/> per adapter.
    /// </summary>
    public void AppendSensors(string hwIdentifier, string hwId, string hwName, List<HardwareSensor> result)
    {
        Span<float> amps = stackalloc float[AstralTelemetryParser.PinCount];
        Delivery delivery = default;
        lock (_lock)
        {
            if (!TryGetState(hwIdentifier, out var state))
            {
                return;
            }

            if ((state.SubSystemId & 0xFFFF) != AstralAibVendors.AsusVendorId)
            {
                return;
            }

            (state.HwId, state.HwName) = (hwId, hwName);
            var now = _timeProvider.GetUtcNow();
            if (now >= state.NextProbeUtc)
            {
                state.NextProbeUtc = now + _probeInterval;
                state.DrivenUtc = now;
                delivery = ReadAndPrepare(state, now, amps);
            }

            if (state.LastReadout is { } readout)
            {
                AstralSensorBuilder.Append(hwId, hwName, readout, result);
                _monitor?.AppendLevelSensors(hwId, hwName, result);
            }
        }

        // The monitor writes settings and raises alerts, so it runs outside the lock.
        Deliver(delivery, amps);
    }

    // Everything the monitor is handed after a read, resolved under the lock.
    private readonly record struct Delivery(
        string HwId, string Name, string Key, bool Pins, AstralAngles? Angles);

    // Probes the card. Only a successful read is handed on: a failed one must not
    // replay the retained readout as a fresh sample. The G-sensor backs off after misses.
    private Delivery ReadAndPrepare(GpuState state, DateTimeOffset now, Span<float> amps)
    {
        if (!Probe(state, state.AdapterIndex, out var readout) || _monitor is null)
        {
            return default;
        }

        state.LastGoodUtc = now;
        EnsureFallback();
        state.Name ??= AstralAibVendors.EnrichName(state.HwName, state.SubSystemId, true);
        state.CalibrationKey ??= CalibrationKey(state.AdapterIndex, state.SubSystemId);
        for (var i = 0; i < amps.Length; i++)
        {
            amps[i] = readout.Pins[i].CurrentAmps;
        }

        AstralAngles? angles = null;
        if (now >= state.NextGSensorUtc)
        {
            var ok = _client.TryReadGSensorBlock(state.AdapterIndex, out var block);
            angles = ok ? AstralGSensorParser.Parse(block) : null;
            if (!state.LoggedFirstGSensor)
            {
                state.LoggedFirstGSensor = true;
                ServiceLog.Info($"[astral] gpu{state.AdapterIndex} first G-sensor read {(angles is null ? "failed" : "ok")}, raw={Convert.ToHexString(block)}");
            }
            if (angles is null)
            {
                state.GSensorMisses = Math.Min(state.GSensorMisses + 1, MaxGSensorBackoffShift);
                state.NextGSensorUtc = now + TimeSpan.FromSeconds(1 << state.GSensorMisses);
            }
            else
            {
                state.GSensorMisses = 0;
            }
        }
        return new Delivery(state.HwId, state.Name, state.CalibrationKey, true, angles);
    }

    private void Deliver(Delivery d, ReadOnlySpan<float> amps)
    {
        if (!d.Pins || _monitor is null)
        {
            return;
        }
        _monitor.Feed(d.HwId, d.Name, d.Key, amps);
        if (d.Angles is { } a)
        {
            _monitor.FeedAngles(d.HwId, a);
        }
    }

    // A card is protected only while its pins are read. The GPU list drives the read when
    // something asks for it; this timer covers the rest. It exists only once a card has
    // answered, reads nothing while the list is driving, and slows to a retry once the card
    // stops answering so protection resumes on its own.
    private void EnsureFallback()
    {
        if (_disposed)
        {
            return;
        }
        if (_fallback is null)
        {
            _fallback = _timeProvider.CreateTimer(_ => FallbackTick(), null, _probeInterval, _probeInterval);
        }
        else if (_slow)
        {
            _slow = false;
            _fallback.Change(_probeInterval, _probeInterval);
            if (_lapseLogged)
            {
                _lapseLogged = false;
                ServiceLog.Info("[astral] card answers again, power protection resumed");
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _fallback?.Dispose();
            _fallback = null;
        }
    }

    internal void FallbackTick()
    {
        try
        {
            TickCore();
        }
        catch (Exception ex)
        {
            var now = _timeProvider.GetUtcNow();
            if (now - _lastTickWarn >= TickWarnEvery)
            {
                _lastTickWarn = now;
                ServiceLog.Warn($"[astral] fallback read failed: {ex.Message}");
            }
        }
    }

    private void TickCore()
    {
        Span<float> amps = stackalloc float[AstralTelemetryParser.PinCount];
        while (true)
        {
            Delivery delivery;
            lock (_lock)
            {
                var now = _timeProvider.GetUtcNow();
                var state = NextFallbackState(now);
                if (state is null)
                {
                    if (_fallback is not null && !_slow && !AnyAnswering(now))
                    {
                        _slow = true;
                        _fallback.Change(SlowRetry, SlowRetry);
                        if (_monitor?.ShutdownEnabled() == true)
                        {
                            _lapseLogged = true;
                            ServiceLog.Warn("[astral] card stopped answering: power protection is paused until it answers again");
                        }
                    }
                    return;
                }
                state.NextProbeUtc = now + _probeInterval;
                delivery = ReadAndPrepare(state, now, amps);
            }
            Deliver(delivery, amps);
        }
    }

    private GpuState? NextFallbackState(DateTimeOffset now)
    {
        foreach (var state in _states.Values)
        {
            // A tick that fires a little early still reads; the margin is less than one interval so a read never repeats within a tick.
            if (state.LastReadout is not null && now + _probeInterval / 2 >= state.NextProbeUtc && now - state.DrivenUtc >= _probeInterval * ExternalSilenceIntervals)
            {
                return state;
            }
        }
        return null;
    }

    private bool AnyAnswering(DateTimeOffset now)
    {
        foreach (var state in _states.Values)
        {
            if (state.LastReadout is not null && now - state.LastGoodUtc < _probeInterval * LapseIntervals)
            {
                return true;
            }
        }
        return false;
    }

    internal static string CalibrationKey(int adapterIndex, uint subSystemId) => $"gpu{adapterIndex}:{subSystemId:X8}";

    private bool TryGetState(string hwIdentifier, out GpuState state)
    {
        state = null!;
        if (!TryParseAdapterIndex(hwIdentifier, out var index))
        {
            return false;
        }

        if (!_states.TryGetValue(index, out var existing))
        {
            existing = new GpuState { AdapterIndex = index };
            _states[index] = existing;
        }
        state = existing;

        if (!state.HasSubSystemId && _client.TryGetPciSubsystemId(index, out var subSystemId))
        {
            state.HasSubSystemId = true;
            state.SubSystemId = subSystemId;
        }

        return state.HasSubSystemId;
    }

    private bool Probe(GpuState state, int adapterIndex, out AstralTelemetryParser.AstralReadout readout)
    {
        var ok = _client.TryReadAstralBlock(adapterIndex, out var block);
        AstralTelemetryParser.AstralReadout? parsed = null;
        if (ok)
        {
            try
            {
                parsed = AstralTelemetryParser.Parse(block);
            }
            catch (ArgumentException)
            {
                ok = false;
            }
        }

        if (!state.LoggedFirstAttempt)
        {
            state.LoggedFirstAttempt = true;
            LogFirstAttempt(adapterIndex, ok, block, parsed);
        }

        if (ok)
        {
            state.LastReadout = parsed;
        }
        readout = parsed.GetValueOrDefault();
        return ok;
    }

    private static void LogFirstAttempt(
        int adapterIndex, bool ok, byte[] block, AstralTelemetryParser.AstralReadout? readout)
    {
        var hex = Convert.ToHexString(block);
        if (!ok || readout is not { } r)
        {
            ServiceLog.Info($"[astral] gpu{adapterIndex} first 0x56 read failed, raw={hex}");
            return;
        }

        var summary = new StringBuilder();
        for (var i = 0; i < r.Pins.Length; i++)
        {
            var pin = r.Pins[i];
            summary.Append($"pin{i + 1}={pin.VoltageVolts:F3}V/{pin.CurrentAmps:F3}A ");
        }
        summary.Append($"connector={r.ConnectorCurrentAmps:F3}A/{r.ConnectorPowerWatts:F1}W");

        ServiceLog.Info($"[astral] gpu{adapterIndex} first 0x56 read ok, raw={hex}, {summary}");
    }

    private static bool TryParseAdapterIndex(string hwIdentifier, out int index)
    {
        index = -1;
        if (string.IsNullOrEmpty(hwIdentifier))
        {
            return false;
        }
        var slash = hwIdentifier.LastIndexOf('/');
        var tail = slash >= 0 ? hwIdentifier[(slash + 1)..] : hwIdentifier;
        return int.TryParse(tail, out index);
    }
}
