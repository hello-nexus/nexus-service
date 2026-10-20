using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
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
/// A card whose 12VHPWR probe succeeds also has its G-sensor read on the same
/// cadence for GPU Tweak III's "Level Sense" (<see cref="AstralLevelSense"/>):
/// how far the card tilted since it was calibrated. A card without a saved
/// calibration calibrates itself on its first readings; the calibration is
/// kept in settings so a restart measures against the same one.
///
/// State is keyed by the adapter index LibreHardwareMonitor's NvidiaGroup
/// assigns (parsed out of the LHM hardware Identifier, "/gpu-nvidia/{index}")
/// - the same index this instance's IAstralNvApiClient uses against its own
/// NvAPI_EnumPhysicalGPUs call, since LHM's public IHardware surface exposes
/// no PCI ids to match on directly.
/// </summary>
internal sealed class AstralGpuSupplement
{
    // Consecutive failed G-sensor reads after which a card that never returned
    // one stops being asked: an Astral without the sensor, or a block that
    // never reads as gravity.
    private const int GSensorGiveUpMisses = 10;

    private readonly IAstralNvApiClient _client;
    private readonly IConfigStore? _store;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _probeInterval;
    private readonly object _lock = new();
    private readonly Dictionary<int, GpuState> _states = new();
    private Dictionary<string, AstralLevelSenseCalibration>? _savedCalibrations;

    public AstralGpuSupplement(IAstralNvApiClient client, IConfigStore? store = null)
        : this(client, TimeProvider.System, TimeSpan.FromSeconds(1), store)
    {
    }

    internal AstralGpuSupplement(
        IAstralNvApiClient client, TimeProvider timeProvider, TimeSpan probeInterval, IConfigStore? store = null)
    {
        _client = client;
        _store = store;
        _timeProvider = timeProvider;
        _probeInterval = probeInterval;
    }

    private sealed class GpuState
    {
        public int AdapterIndex;
        public string HwIdentifier = "";
        public string HwName = "";
        public bool HasSubSystemId;
        public uint SubSystemId;
        public DateTimeOffset NextProbeUtc;
        public bool LoggedFirstAttempt;
        public AstralTelemetryParser.AstralReadout? LastReadout;
        public bool LoggedFirstGSensorAttempt;
        public int GSensorMisses;
        public AstralLevelSense? LevelSense;
    }

    private readonly record struct CalibrationToSave(string Key, AstralLevelSense.Calibration Calibration, long AtMs);

    /// <summary>
    /// The settings key of a card's Level Sense calibration: its adapter index
    /// and PCI subsystem id, so a different card in that slot calibrates anew.
    /// </summary>
    internal static string LevelSenseKey(int adapterIndex, uint subSystemId) => $"gpu{adapterIndex}:{subSystemId:X8}";

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
    /// adapter's subsystem vendor is ASUS and a probe has ever succeeded, and
    /// the Level Sense sensors once the card has a calibration.
    /// Probes at most once per <see cref="_probeInterval"/> per adapter.
    /// </summary>
    public void AppendSensors(string hwIdentifier, string hwId, string hwName, List<HardwareSensor> result)
    {
        // Settings are read and written outside _lock: the store runs its
        // mutators and change handlers under its own lock.
        LoadSavedCalibrations();

        CalibrationToSave? toSave = null;
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
            state.HwName = hwName;

            var now = _timeProvider.GetUtcNow();
            if (now >= state.NextProbeUtc)
            {
                state.NextProbeUtc = now + _probeInterval;
                Probe(state, state.AdapterIndex);
                toSave = ProbeGSensor(state, now);
            }

            if (state.LastReadout is { } readout)
            {
                AstralSensorBuilder.Append(hwId, hwName, readout, result);
            }

            if (state.LevelSense is { Deviation: { } deviation, Posture: { } posture })
            {
                AstralSensorBuilder.AppendLevelSense(
                    hwId, hwName, deviation.X, deviation.Y, posture.Sag, posture.Lean, result);
            }
        }

        if (toSave is { } save)
        {
            SaveCalibration(save);
        }
    }

    /// <summary>
    /// Restarts the Level Sense calibration of the GPU at
    /// <paramref name="hwIdentifier"/>, or of every GPU with a G-sensor when it
    /// is null. The next <see cref="AstralLevelSense.CalibrationSamples"/>
    /// readings become the new level; until then the old one stays in use.
    /// Returns the hardware identifiers of the GPUs now calibrating.
    /// </summary>
    public IReadOnlyList<string> RecalibrateLevelSense(string? hwIdentifier)
    {
        var started = new List<string>();
        lock (_lock)
        {
            foreach (var state in _states.Values)
            {
                if (state.LevelSense is not { } levelSense)
                {
                    continue;
                }
                if (hwIdentifier is not null
                    && (!TryParseAdapterIndex(hwIdentifier, out var index) || index != state.AdapterIndex))
                {
                    continue;
                }

                levelSense.StartCalibration();
                started.Add(state.HwIdentifier);
                ServiceLog.Info($"[astral] gpu{state.AdapterIndex} Level Sense recalibration started");
            }
        }
        return started;
    }

    /// <summary>
    /// Every GPU with a G-sensor: whether a calibration is being taken, when the
    /// saved one was taken (null before the first is saved), and the latest
    /// reading against it, the same values its Level Sense sensors carry.
    /// </summary>
    public IReadOnlyList<GpuLevelSenseState> GetLevelSenseStates()
    {
        var cards = new List<(GpuLevelSenseState State, string Key)>();
        lock (_lock)
        {
            foreach (var state in _states.Values)
            {
                if (state.LevelSense is { } levelSense)
                {
                    var deviation = levelSense.Deviation;
                    var posture = levelSense.Posture;
                    cards.Add((new GpuLevelSenseState(
                        state.HwIdentifier,
                        AstralAibVendors.EnrichName(state.HwName, state.SubSystemId, state.LastReadout.HasValue),
                        levelSense.IsCalibrating,
                        null,
                        posture?.Sag, posture?.Lean, deviation?.X, deviation?.Y),
                        LevelSenseKey(state.AdapterIndex, state.SubSystemId)));
                }
            }
        }

        // The store is read outside _lock, as everywhere else here.
        var saved = _store?.Load().AstralLevelSense;
        var result = new List<GpuLevelSenseState>(cards.Count);
        foreach (var (card, key) in cards)
        {
            long? calibratedAt = saved is not null && saved.TryGetValue(key, out var calibration) && calibration is not null
                ? calibration.CalibratedAtMs
                : null;
            result.Add(card with { CalibratedAtMs = calibratedAt });
        }
        return result;
    }

    private bool TryGetState(string hwIdentifier, out GpuState state)
    {
        state = null!;
        if (!TryParseAdapterIndex(hwIdentifier, out var index))
        {
            return false;
        }

        if (!_states.TryGetValue(index, out var existing))
        {
            existing = new GpuState { AdapterIndex = index, HwIdentifier = hwIdentifier };
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

    // Only a confirmed Astral (a 12VHPWR probe has succeeded) is asked for its
    // G-sensor. Returns a calibration this reading completed, for the caller
    // to save once it has left _lock.
    private CalibrationToSave? ProbeGSensor(GpuState state, DateTimeOffset now)
    {
        if (state.LastReadout is null || (state.LevelSense is null && state.GSensorMisses >= GSensorGiveUpMisses))
        {
            return null;
        }

        var ok = _client.TryReadGSensorBlock(state.AdapterIndex, out var block);
        var angles = ok ? AstralGSensorParser.Parse(block) : null;

        if (!state.LoggedFirstGSensorAttempt)
        {
            state.LoggedFirstGSensorAttempt = true;
            var outcome = angles is { } a ? $"ok, x={a.X:F2} y={a.Y:F2} z={a.Z:F2}" : "failed";
            ServiceLog.Info($"[astral] gpu{state.AdapterIndex} first G-sensor read {outcome}, raw={Convert.ToHexString(block)}");
        }

        if (angles is not { } sample)
        {
            state.GSensorMisses++;
            return null;
        }
        state.GSensorMisses = 0;

        var key = LevelSenseKey(state.AdapterIndex, state.SubSystemId);
        if (state.LevelSense is null)
        {
            state.LevelSense = new AstralLevelSense();
            // A calibration saved before Z was kept cannot place the lean, so it is taken again.
            if (_savedCalibrations is { } saved && saved.TryGetValue(key, out var calibration)
                && calibration is { Z: { } z })
            {
                state.LevelSense.Load(new AstralLevelSense.Calibration(calibration.X, calibration.Y, z));
            }
            else
            {
                state.LevelSense.StartCalibration();
            }
        }

        if (state.LevelSense.Add(sample) is not { } completed)
        {
            return null;
        }
        ServiceLog.Info($"[astral] gpu{state.AdapterIndex} Level Sense calibrated: x={completed.X:F2} y={completed.Y:F2} z={completed.Z:F2}");
        return new CalibrationToSave(key, completed, now.ToUnixTimeMilliseconds());
    }

    private void LoadSavedCalibrations()
    {
        if (_store is null || Volatile.Read(ref _savedCalibrations) is not null)
        {
            return;
        }
        var saved = new Dictionary<string, AstralLevelSenseCalibration>(
            _store.Load().AstralLevelSense ?? new Dictionary<string, AstralLevelSenseCalibration>(), StringComparer.Ordinal);
        Interlocked.CompareExchange(ref _savedCalibrations, saved, null);
    }

    private void SaveCalibration(CalibrationToSave save)
    {
        if (_store is null)
        {
            return;
        }
        var record = new AstralLevelSenseCalibration
        {
            X = save.Calibration.X,
            Y = save.Calibration.Y,
            Z = save.Calibration.Z,
            CalibratedAtMs = save.AtMs,
        };
        _store.Update(s =>
        {
            s.AstralLevelSense ??= new Dictionary<string, AstralLevelSenseCalibration>();
            s.AstralLevelSense[save.Key] = record;
        });
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
