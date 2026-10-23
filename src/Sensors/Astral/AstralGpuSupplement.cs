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
internal sealed class AstralGpuSupplement
{
    private readonly IAstralNvApiClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _probeInterval;
    private readonly AstralMonitor? _monitor;
    private readonly object _lock = new();
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

            var now = _timeProvider.GetUtcNow();
            if (now >= state.NextProbeUtc)
            {
                state.NextProbeUtc = now + _probeInterval;
                Probe(state, state.AdapterIndex);
                Feed(state, hwId, hwName, now);
            }

            if (state.LastReadout is { } readout)
            {
                AstralSensorBuilder.Append(hwId, hwName, readout, result);
                _monitor?.AppendLevelSensors(hwId, hwName, result);
            }
        }
    }

    // Hands the fresh readings to the monitor: pin currents, then the G-sensor with
    // backoff (1 s doubling to 60 s) so a card without one is not hammered.
    private void Feed(GpuState state, string hwId, string hwName, DateTimeOffset now)
    {
        if (_monitor is null || state.LastReadout is not { } readout)
        {
            return;
        }

        var name = AstralAibVendors.EnrichName(hwName, state.SubSystemId, true);
        Span<float> amps = stackalloc float[AstralTelemetryParser.PinCount];
        for (var i = 0; i < amps.Length; i++)
        {
            amps[i] = readout.Pins[i].CurrentAmps;
        }
        _monitor.Feed(hwId, name, CalibrationKey(state.AdapterIndex, state.SubSystemId), amps);

        if (now < state.NextGSensorUtc)
        {
            return;
        }
        var ok = _client.TryReadGSensorBlock(state.AdapterIndex, out var block);
        var angles = ok ? AstralGSensorParser.Parse(block) : null;
        if (!state.LoggedFirstGSensor)
        {
            state.LoggedFirstGSensor = true;
            ServiceLog.Info($"[astral] gpu{state.AdapterIndex} first G-sensor read {(angles is null ? "failed" : "ok")}, raw={Convert.ToHexString(block)}");
        }
        if (angles is { } a)
        {
            state.GSensorMisses = 0;
            _monitor.FeedAngles(hwId, a);
            return;
        }
        state.GSensorMisses = Math.Min(state.GSensorMisses + 1, 6);
        state.NextGSensorUtc = now + TimeSpan.FromSeconds(1 << state.GSensorMisses);
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

    private void Probe(GpuState state, int adapterIndex)
    {
        var ok = _client.TryReadAstralBlock(adapterIndex, out var block);
        AstralTelemetryParser.AstralReadout? readout = null;
        if (ok)
        {
            try
            {
                readout = AstralTelemetryParser.Parse(block);
            }
            catch (ArgumentException)
            {
                ok = false;
            }
        }

        if (!state.LoggedFirstAttempt)
        {
            state.LoggedFirstAttempt = true;
            LogFirstAttempt(adapterIndex, ok, block, readout);
        }

        if (ok)
        {
            state.LastReadout = readout;
        }
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
