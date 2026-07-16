using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cooling;
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

    public CompositeFanControlProvider(
        IFanControlProvider motherboard,
        Np50CoolingProvider np50,
        MiniHubCoolingProvider miniHub,
        PluginProviderRegistry registry,
        params FanSource[] extras)
    {
        _motherboard = motherboard;
        _motherboardCooling = motherboard as ICoolingProvider;
        _np50 = np50;
        _miniHub = miniHub;
        _registry = registry;
        _extras = extras ?? Array.Empty<FanSource>();
    }

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
        combined.AddRange(_np50.GetFanChannels());
        combined.AddRange(_miniHub.GetFanChannels());
        foreach (var e in Extras())
            combined.AddRange(e.Provider.GetFanChannels());
        foreach (var ch in combined)
            InferPumpKind(ch);
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

    public IReadOnlyList<TemperatureSource> GetTemperatureSources()
    {
        var combined = new List<TemperatureSource>(_motherboard.GetTemperatureSources());
        combined.AddRange(_np50.GetTemperatureSources());
        combined.AddRange(_miniHub.GetTemperatureSources());
        foreach (var e in Extras())
            combined.AddRange(e.Provider.GetTemperatureSources());
        return combined;
    }

    public float? ReadTemperature(string sensorId)
    {
        if (IsNp50Id(sensorId)) return _np50.ReadTemperature(sensorId);
        if (MiniHubCoolingProvider.IsMiniHubId(sensorId)) return _miniHub.ReadTemperature(sensorId);
        foreach (var e in Extras())
            if (e.Owns(sensorId)) return e.Provider.ReadTemperature(sensorId);
        return _motherboard.ReadTemperature(sensorId);
    }

    // The single fan-write chokepoint: every duty that reaches hardware - from a
    // curve apply, a direct speed call, or a plugin-guided write - is clamped to
    // [0,100] here, so no caller can drive a fan out of range.
    public int SetFanSpeed(string channelId, int dutyPercent)
        => Route(channelId).SetFanSpeed(channelId, CoolingSafety.ClampDuty(dutyPercent));

    public void DriveFanSpeed(string channelId, int dutyPercent)
        => Route(channelId).DriveFanSpeed(channelId, CoolingSafety.ClampDuty(dutyPercent));

    public void ReleaseFan(string channelId)
        => Route(channelId).ReleaseFan(channelId);

    public void ReleaseAll()
    {
        _motherboard.ReleaseAll();
        _np50.ReleaseAll();
        _miniHub.ReleaseAll();
        foreach (var e in Extras())
            e.Provider.ReleaseAll();
    }

    public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
        IReadOnlyList<string> fanIds,
        IProgress<FanCalibrationProgress> progress,
        CancellationToken ct)
    {
        // Motherboard-only. Hub/USB/GPU providers no-op calibration, so filter
        // their ids out before delegating so the motherboard side doesn't fail
        // on unrecognized channels.
        if (fanIds.Count == 0)
            return _motherboard.CalibrateAsync(fanIds, progress, ct);
        var motherboardOnly = fanIds.Where(id => !IsExternalId(id)).ToList();
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
        combined.AddRange(_np50.GetAll());
        combined.AddRange(_miniHub.GetAll());
        foreach (var e in Extras())
        {
            if (e.Provider is ICoolingProvider c)
                combined.AddRange(c.GetAll());
        }
        return combined;
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
