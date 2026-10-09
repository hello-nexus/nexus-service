using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;
using Nexus.Service.Plugins;

namespace Nexus.Service.Cooling;

/// <summary>
/// Aggregates the motherboard fan provider (LibreHardwareMonitor on Windows,
/// hwmon on Linux, stub on macOS) with the HYTE NP50 and MiniHub hub providers,
/// plus any platform "extra" sources (Linux adds liquidctl USB coolers and
/// NVIDIA GPU fans). Acts as the single <see cref="IFanControlProvider"/> +
/// <see cref="ICoolingProvider"/> the rest of the service consumes, so the curve
/// engine, REST routes, and WebSocket broadcasts stay unchanged.
///
/// Routing is by channel-id prefix: <c>np50:</c> → NP50, <c>minihub:</c> →
/// MiniHub, each extra source owns its own prefix (<c>liquidctl:</c>,
/// <c>nvidia:</c>); everything else goes to the motherboard provider.
///
/// Calibration only runs against the motherboard side - hub / USB-cooler / GPU
/// fans report stable duty and don't benefit from the ramp/hold dance.
/// </summary>
public sealed class CompositeFanControlProvider : IFanControlProvider, ICoolingProvider
{
    /// <summary>An extra prefixed fan source layered on top of the motherboard provider.</summary>
    public readonly record struct FanSource(Func<string, bool> Owns, IFanControlProvider Provider);

    private readonly IFanControlProvider _motherboard;
    private readonly ICoolingProvider? _motherboardCooling;
    private readonly Np50CoolingProvider _np50;
    private readonly MiniHubCoolingProvider _miniHub;
    private readonly FanSource[] _extras;
    private readonly PluginProviderRegistry _registry;
    private readonly IConfigStore? _store;
    private readonly ConditionalWeakTable<IFanControlProvider, SourceGate> _gates = new();

    /// <summary>How long a hub or USB cooler may take to answer before callers stop waiting on it.</summary>
    internal TimeSpan CallBudget { get; set; } = TimeSpan.FromSeconds(5);

    public CompositeFanControlProvider(
        IFanControlProvider motherboard,
        Np50CoolingProvider np50,
        MiniHubCoolingProvider miniHub,
        PluginProviderRegistry registry,
        params FanSource[] extras)
        : this(motherboard, np50, miniHub, registry, (IConfigStore?)null, extras)
    {
    }

    public CompositeFanControlProvider(
        IFanControlProvider motherboard,
        Np50CoolingProvider np50,
        MiniHubCoolingProvider miniHub,
        PluginProviderRegistry registry,
        IConfigStore? store,
        params FanSource[] extras)
    {
        _motherboard = motherboard;
        _motherboardCooling = motherboard as ICoolingProvider;
        _np50 = np50;
        _miniHub = miniHub;
        _registry = registry;
        _store = store;
        _extras = extras ?? Array.Empty<FanSource>();
    }

    /// <summary>
    /// A channel the user marked not controlled takes no duty write from any
    /// caller. Enforced here rather than per-provider so a curve apply, a REST
    /// speed call, a deck action and a plugin all hit the same gate - the same
    /// reason the [0,100] clamp lives here. Release still passes: handing the
    /// channel back to the motherboard is the state this flag asks for.
    /// </summary>
    private bool IsUncontrolled(string channelId)
        => _store is not null && !FanControlledState.IsControlled(channelId, _store.Load());

    /// <summary>
    /// All extra prefixed sources layered on the motherboard: the platform's
    /// first-party extras (SmartHub / liquidctl / NVIDIA, fixed at construction)
    /// followed by the registry's plugin sources (a lock-free snapshot, empty
    /// until the broker registers one). Plugin sources are prefix-scoped, so a
    /// plugin can never route to a first-party channel.
    /// </summary>
    private IEnumerable<FanSource> Extras()
    {
        foreach (var e in _extras) yield return e;
        foreach (var e in _registry.FanSources) yield return e;
    }

    // ── IFanControlProvider ──

    public IReadOnlyList<FanChannel> GetFanChannels()
    {
        var combined = new List<FanChannel>(_motherboard.GetFanChannels());
        foreach (var source in GatedSources())
            combined.AddRange(Call(source, "a fan read", source.GetFanChannels, Array.Empty<FanChannel>()));
        foreach (var ch in combined)
            InferPumpKind(ch);
        MarkAioDevices(combined);
        return combined;
    }

    // A generic provider (motherboard) surfaces an AIO pump head as an ordinary
    // channel without classifying it; a hardware-reported name containing "pump"
    // marks it a pump. Runs on the hardware name, before the route overlays a
    // user rename, so renaming a fan to "pump" does not flip its kind.
    private static void InferPumpKind(FanChannel ch)
    {
        if (ch.Kind == FanKinds.Fan
            && !string.IsNullOrEmpty(ch.Name)
            && ch.Name.Contains("pump", StringComparison.OrdinalIgnoreCase))
        {
            ch.Kind = FanKinds.Pump;
        }
    }

    // A device that exposes a pump head is an AIO, so every channel on it -
    // the radiator fans included - belongs to the cooler and is left to the
    // cooler's own curve. Runs after InferPumpKind so a name-inferred pump
    // counts. Motherboard channels carry no DeviceId, which is what keeps a
    // header called "pump" from dragging every case fan in with it.
    internal static void MarkAioDevices(List<FanChannel> channels)
    {
        var aioDevices = channels
            .Where(c => c.Kind == FanKinds.Pump && !string.IsNullOrEmpty(c.DeviceId))
            .Select(c => c.DeviceId!)
            .ToHashSet(StringComparer.Ordinal);
        if (aioDevices.Count == 0) return;
        foreach (var ch in channels)
        {
            if (ch.DeviceId is { } id && aioDevices.Contains(id)) ch.IsAio = true;
        }
    }

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        var combined = new List<TemperatureSource>(_motherboard.GetTemperatureSources());
        combined.AddRange(GetDeviceTemperatureSources());
        return combined;
    }

    /// <summary>
    /// Skips the motherboard provider, whose <see cref="GetTemperatureSources"/> refreshes the
    /// platform sensor library and then yields only sources this caller discards.
    /// </summary>
    public IReadOnlyList<TemperatureSource> GetDeviceTemperatureSources()
    {
        var combined = new List<TemperatureSource>();
        foreach (var source in GatedSources())
            combined.AddRange(Call(source, "a sensor read", source.GetTemperatureSources, Array.Empty<TemperatureSource>()));
        return combined;
    }

    public IReadOnlyList<FanChannel> GetDevicePumpChannels()
    {
        var combined = new List<FanChannel>(_np50.GetFanChannels());
        combined.AddRange(_miniHub.GetFanChannels());
        foreach (var e in Extras())
            combined.AddRange(e.Provider.GetFanChannels());
        foreach (var ch in combined)
            InferPumpKind(ch);
        combined.RemoveAll(c => c.Kind != FanKinds.Pump || string.IsNullOrEmpty(c.DeviceId));
        return combined;
    }

    public float? ReadTemperature(string sensorId)
    {
        var source = Route(sensorId);
        return Call(source, "a sensor read", () => source.ReadTemperature(sensorId), (float?)null);
    }

    // The single fan-write chokepoint: every duty that reaches hardware - from a
    // curve apply, a direct speed call, or a plugin-guided write - is clamped to
    // [0,100] here, so no caller can drive a fan out of range.
    public int SetFanSpeed(string channelId, int dutyPercent)
    {
        var clamped = CoolingSafety.ClampDuty(dutyPercent);
        // Report the clamped value back rather than the live duty: the caller
        // asked for it, nothing rejected it, and the channel simply is not ours
        // to drive. A read of the channel still shows what the hardware does.
        if (IsUncontrolled(channelId)) return clamped;
        var source = Route(channelId);
        return Call(source, "a fan write", () => source.SetFanSpeed(channelId, clamped), clamped, gate => SupersedeRelease(gate, channelId));
    }

    public void DriveFanSpeed(string channelId, int dutyPercent)
    {
        if (IsUncontrolled(channelId)) return;
        var source = Route(channelId);
        var clamped = CoolingSafety.ClampDuty(dutyPercent);
        Run(source, "a fan write", () => source.DriveFanSpeed(channelId, clamped), gate => SupersedeRelease(gate, channelId));
    }

    public void ReleaseFan(string channelId)
    {
        var source = Route(channelId);
        Run(source, "a fan release", () => source.ReleaseFan(channelId),
            gate => (gate.ReleasesMissed ??= new HashSet<string>(StringComparer.Ordinal)).Add(channelId));
    }

    // Every provider is attempted: one that throws must not leave the others holding a
    // Nexus duty. The first failure is logged and nothing is rethrown.
    public void ReleaseAll()
    {
        var failureLogged = false;
        void Attempt(string what, Action release)
        {
            try { release(); }
            catch (Exception ex)
            {
                if (!failureLogged)
                {
                    failureLogged = true;
                    Console.Error.WriteLine($"[cooling] release of {what} failed: {ex.Message}");
                }
            }
        }
        Attempt("the motherboard fans", _motherboard.ReleaseAll);
        List<IFanControlProvider> sources;
        try { sources = GatedSources().ToList(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[cooling] listing providers to release failed: {ex.Message}");
            return;
        }
        foreach (var source in sources)
            Attempt(source.GetType().Name, () => Run(source, "a release", source.ReleaseAll, gate => gate.ReleaseAllMissed = true));
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        // Motherboard-only. Hub/USB/GPU providers no-op calibration, so filter
        // their ids out before delegating so the motherboard side doesn't fail
        // on unrecognized channels.
        // Calibration ramps duty 100%->0%, which is a write like any other: a
        // channel the user handed to the motherboard must not be spun by it.
        // An empty request means "everything", so that case has to be expanded
        // before it can be filtered.
        var uncontrolled = _store?.Load().Cooling.UncontrolledFanChannels;
        if (fanIds.Count == 0)
        {
            if (uncontrolled is null || uncontrolled.Count == 0)
                return _motherboard.CalibrateAsync(fanIds, progress, ct);
            fanIds = _motherboard.GetFanChannels().Select(c => c.Id).ToList();
        }
        var motherboardOnly = fanIds
            .Where(id => !IsExternalId(id) && (uncontrolled is null || !uncontrolled.Contains(id)))
            .ToList();
        // Empty means "all" to the motherboard provider, so a request naming only
        // hub fans must stop here: falling through would calibrate every
        // motherboard fan instead of none.
        if (motherboardOnly.Count == 0)
            return Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
        return _motherboard.CalibrateAsync(motherboardOnly, progress, ct);
    }

    // ── ICoolingProvider ──

    public IReadOnlyList<CoolingComponent> GetAll()
    {
        var combined = new List<CoolingComponent>(
            _motherboardCooling?.GetAll() ?? Array.Empty<CoolingComponent>());
        foreach (var source in GatedSources())
        {
            if (source is ICoolingProvider c)
                combined.AddRange(Call(source, "a status read", c.GetAll, Array.Empty<CoolingComponent>()));
        }
        return combined;
    }

    // ── stall isolation ──

    // A hub or USB cooler can block its caller until Windows removes the device (a wedged
    // WinUSB transfer), so its calls run off the caller's thread. One that overruns
    // CallBudget leaves the source reading as disconnected until every overrun call has
    // returned and the releases skipped meanwhile have run; the curve engine then re-drives
    // the channels as they reappear.
    private sealed class SourceGate
    {
        public readonly object Lock = new();
        public int Overruns;
        public bool Replaying;
        public bool ReleaseAllMissed;
        public HashSet<string>? ReleasesMissed;
    }

    // A write skipped after a skipped release is the later intent; the engine re-drives it on recovery.
    private static void SupersedeRelease(SourceGate gate, string channelId) => gate.ReleasesMissed?.Remove(channelId);

    private IEnumerable<IFanControlProvider> GatedSources()
    {
        yield return _np50;
        yield return _miniHub;
        foreach (var e in Extras()) yield return e.Provider;
    }

    private void Run(IFanControlProvider source, string what, Action action, Action<SourceGate>? onSkipped = null)
        => Call(source, what, () => { action(); return true; }, false, onSkipped);

    /// <summary>Runs <paramref name="call"/> within <see cref="CallBudget"/>, or returns <paramref name="whileStalled"/> for a source that has not answered.</summary>
    private T Call<T>(IFanControlProvider source, string what, Func<T> call, T whileStalled, Action<SourceGate>? onSkipped = null)
    {
        // A stalled motherboard path must stall the tick, so the watchdog hands those fans to the BIOS.
        if (ReferenceEquals(source, _motherboard))
            return call();
        var gate = _gates.GetValue(source, _ => new SourceGate());
        lock (gate.Lock)
        {
            if (gate.Overruns > 0 || gate.Replaying)
            {
                onSkipped?.Invoke(gate);
                return whileStalled;
            }
        }
        var startedMs = Environment.TickCount64;
        var task = Task.Run(call);
        bool done;
        try { done = task.Wait(CallBudget); }
        catch (AggregateException) { done = true; }
        if (done)
            return task.GetAwaiter().GetResult();

        bool first;
        lock (gate.Lock) { first = ++gate.Overruns == 1 && !gate.Replaying; }
        var name = source.GetType().Name;
        if (first)
            Console.Error.WriteLine($"[cooling] {name} did not answer {what} within {CallBudget.TotalSeconds:0.#} s; treating it as disconnected until it does");
        task.ContinueWith(t =>
        {
            _ = t.Exception;
            Recover(source, gate, name, startedMs);
        }, TaskScheduler.Default);
        return whileStalled;
    }

    // Runs once an overrun call returns; the last one replays the skipped releases, then reopens the source.
    private static void Recover(IFanControlProvider source, SourceGate gate, string name, long startedMs)
    {
        lock (gate.Lock)
        {
            if (--gate.Overruns > 0)
                return;
            gate.Replaying = true;
        }
        Console.Error.WriteLine($"[cooling] {name} answered again after {(Environment.TickCount64 - startedMs) / 1000.0:0.0} s");
        while (true)
        {
            bool all;
            HashSet<string>? ids;
            lock (gate.Lock)
            {
                all = gate.ReleaseAllMissed;
                ids = gate.ReleasesMissed;
                gate.ReleaseAllMissed = false;
                gate.ReleasesMissed = null;
                if (!all && ids is null)
                {
                    gate.Replaying = false;
                    return;
                }
            }
            try
            {
                if (all)
                    source.ReleaseAll();
                else
                    foreach (var id in ids!) source.ReleaseFan(id);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cooling] {name} missed release replay failed: {ex.Message}");
            }
        }
    }

    // ── routing ──

    private IFanControlProvider Route(string id)
    {
        if (IsNp50Id(id)) return _np50;
        if (MiniHubCoolingProvider.IsMiniHubId(id)) return _miniHub;
        foreach (var e in Extras())
            if (e.Owns(id)) return e.Provider;
        return _motherboard;
    }

    private bool IsExternalId(string id)
    {
        if (IsNp50Id(id) || MiniHubCoolingProvider.IsMiniHubId(id))
            return true;
        foreach (var e in Extras())
            if (e.Owns(id)) return true;
        return false;
    }

    private static bool IsNp50Id(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("np50:", StringComparison.Ordinal);
}
