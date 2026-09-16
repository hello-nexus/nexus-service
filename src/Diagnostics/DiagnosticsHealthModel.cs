using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Diagnostics.SystemInfo;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Diagnostics;

/// <summary>Wire status values shared by the overall health value and every component's status.</summary>
public static class HealthStatuses
{
    public const string Ok = "ok";
    public const string Watch = "watch";
    public const string Act = "act";
    public const string Unknown = "unknown";
}

public sealed record DiagnosticsHealthResponse
{
    public DateTime GeneratedAt { get; init; }
    public bool Supported { get; init; }
    public string Overall { get; init; } = HealthStatuses.Unknown;
    public IReadOnlyList<HealthComponent> Components { get; init; } = Array.Empty<HealthComponent>();
    /// <summary>False when the Diagnostics feature pillar is off. BuildHealth is never called in that case, so every other field stays at its default.</summary>
    public bool Enabled { get; init; } = true;
}

public sealed record HealthComponent
{
    /// <summary>kind:stableKey, e.g. "storage:S6Z1NX0T123456".</summary>
    public string Id { get; init; } = "";
    /// <summary>storage | memory | gpu | cooling | system.</summary>
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string Status { get; init; } = HealthStatuses.Unknown;
    public IReadOnlyList<HealthComponentReason> Reasons { get; init; } = Array.Empty<HealthComponentReason>();
}

public sealed record HealthComponentReason(string Code, string Severity, string Summary, string Detail);

/// <summary>
/// Aggregates every diagnostics module into the GET /diagnostics/health payload.
/// <see cref="Compute"/> is a pure function of plain snapshot DTOs so it is
/// testable on any platform without the Windows-only monitor classes;
/// <see cref="BuildHealth"/> is the DI-facing instance wrapper that gathers
/// those snapshots and caches the result for 30s (the health page and the
/// alert service both poll through this).
/// </summary>
public sealed class DiagnosticsHealthModel
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EventWindow = TimeSpan.FromDays(30);

    // Wider than the recency window Compute checks, so an episode that
    // started before that window but still overlaps it is not truncated.
    private static readonly TimeSpan TempEpisodeLookback = TimeSpan.FromHours(48);

    private readonly SmartHealthMonitor _smart;
    private readonly CoolingStallDetector _cooling;
    private readonly GpuHealthMonitor _gpu;
    private readonly EventLogMonitor _events;
    private readonly MemoryDiagnosticOrchestrator _memDiag;
    private readonly PnpProblemScanner _pnp;
    private readonly ISensorProvider _sensors;
    private readonly IMetricsHistoryStore _tempStore;
    private readonly IConfigStore _store;

    private readonly object _gate = new();
    private DiagnosticsHealthResponse? _cached;
    private DateTime _cachedAtUtc = DateTime.MinValue;
    // The ignore list the cached result was computed with, so an Ignore /
    // Include click bypasses the 30s cache on the next call. Compared per
    // call rather than hooked on IConfigStore.OnChanged: that fires for every
    // unrelated preference write (and not at all for Reload), while only this
    // list changes the answer.
    private IReadOnlyList<string> _cachedIgnored = Array.Empty<string>();

    public DiagnosticsHealthModel(
        SmartHealthMonitor smart,
        CoolingStallDetector cooling,
        GpuHealthMonitor gpu,
        EventLogMonitor events,
        MemoryDiagnosticOrchestrator memDiag,
        PnpProblemScanner pnp,
        ISensorProvider sensors,
        IMetricsHistoryStore tempStore,
        IConfigStore store)
    {
        _smart = smart;
        _cooling = cooling;
        _gpu = gpu;
        _events = events;
        _memDiag = memDiag;
        _pnp = pnp;
        _sensors = sensors;
        _tempStore = tempStore;
        _store = store;
    }

    /// <summary>forceRefresh bypasses this model's own cache; module caches are
    /// unaffected - each module snapshot still goes through its normal
    /// Snapshot() call.</summary>
    public DiagnosticsHealthResponse BuildHealth(bool forceRefresh = false)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            // Single settings snapshot for this whole computation - thresholds
            // and the episode recency window both derive from it.
            var diagnostics = _store.Load().Diagnostics;
            // An explicit JSON null in settings.json survives the initializer.
            var ignoredNow = diagnostics.IgnoredComponents ?? new List<string>();
            if (!forceRefresh && _cached is not null && now - _cachedAtUtc < CacheTtl
                && _cachedIgnored.SequenceEqual(ignoredNow, StringComparer.Ordinal))
            {
                return _cached;
            }
            var thresholdOverrides = new Dictionary<string, double>
            {
                ["cpu"] = diagnostics.Thresholds.CpuC,
                ["gpu"] = diagnostics.Thresholds.GpuC,
                ["storage"] = diagnostics.Thresholds.StorageC,
                ["ram"] = diagnostics.Thresholds.RamC,
            };

            var result = Compute(
                smart: _smart.Snapshot(),
                cooling: _cooling.Snapshot(),
                gpu: _gpu.Snapshot(),
                counts30d: _events.CountsSince(EventWindow),
                lastMemoryTest: _memDiag.LastResult(),
                pnp: _pnp.Snapshot(),
                knownGpuModels: _sensors.GetGpuModels(),
                windowsSupported: OperatingSystem.IsWindows(),
                generatedAtUtc: now,
                tempEpisodes: TemperatureInsights.DetectEpisodes(QueryTempRows(now), thresholdOverrides),
                diagnostics: diagnostics);

            _cached = result;
            _cachedAtUtc = now;
            _cachedIgnored = ignoredNow.ToList();
            return result;
        }
    }

    private IReadOnlyList<TemperatureBucketRow> QueryTempRows(DateTime nowUtc)
    {
        try
        {
            var toMs = new DateTimeOffset(nowUtc).ToUnixTimeMilliseconds();
            var fromMs = new DateTimeOffset(nowUtc - TempEpisodeLookback).ToUnixTimeMilliseconds();
            return _tempStore.QueryTemperatureBuckets(fromMs, toMs);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[diagnostics-health] temperature query failed: {ex.Message}");
            return Array.Empty<TemperatureBucketRow>();
        }
    }

    /// <summary>
    /// Pure aggregation. Storage (SMART), GPU, and cooling are evaluated on
    /// every platform - each self-guards on its own snapshot's Supported flag,
    /// so on Linux/macOS they contribute only when the underlying source
    /// (smartctl, NVML, IFanControlProvider) produced real data. Memory and
    /// system/pnp are Windows-only diagnostics (Windows Memory Diagnostic,
    /// Win32_PnPEntity) with no cross-platform data, so they stay behind
    /// <paramref name="windowsSupported"/>.
    ///
    /// Every component reflects current state only: SMART classification,
    /// active GPU throttle, last memory test result, live pnp problems, and
    /// cooling stall detection. 30-day event history (TDRs, driver errors,
    /// bugchecks, dirty shutdowns, WHEA) never feeds a status here - it stays
    /// on GET /diagnostics/system and /diagnostics/gpu and the PDF report's
    /// stability grid, both informational-only. <paramref name="counts30d"/>
    /// is accepted so the "history never affects status" invariant is testable
    /// by construction; nothing in this method reads it.
    /// </summary>
    public static DiagnosticsHealthResponse Compute(
        SmartSnapshot smart,
        CoolingStallSnapshot cooling,
        GpuHealthSnapshot gpu,
        IReadOnlyDictionary<string, int> counts30d,
        MemoryTestResult? lastMemoryTest,
        PnpProblemSnapshot pnp,
        IReadOnlyList<string> knownGpuModels,
        bool windowsSupported,
        DateTime generatedAtUtc,
        IReadOnlyList<TemperatureEpisode>? tempEpisodes = null,
        DiagnosticsSettings? diagnostics = null)
    {
        var diag = diagnostics ?? new DiagnosticsSettings();
        var ignored = new HashSet<string>(diag.IgnoredComponents ?? new List<string>(), StringComparer.Ordinal);
        var components = new List<HealthComponent>();

        // Storage surfaces off Windows too: AddStorageComponents returns early
        // on !smart.Supported, so it contributes only where a real source
        // produced data (LHM on Windows, smartctl on Linux).
        if (diag.Components.Storage)
        {
            AddStorageComponents(components, smart);
        }
        // GPU off Windows only when a live health source (NVML) reported.
        // AddGpuComponents' known-models fallback adds an Unknown placeholder
        // tile whenever a model name exists (system_profiler on every Mac,
        // lspci on any Linux with a display adapter) - a meaningless tile off
        // Windows, so require gpu.Supported there and keep the Windows path
        // (placeholder included) unchanged.
        if (diag.Components.Gpu && (windowsSupported || gpu.Supported))
        {
            AddGpuComponents(components, gpu, knownGpuModels);
        }
        // Memory and system/pnp have no non-Windows data source and their
        // Add* helpers do not self-guard, so keep them Windows-only.
        if (windowsSupported)
        {
            if (diag.Components.Ram)
            {
                AddMemoryComponent(components, lastMemoryTest);
            }
            if (diag.Components.System)
            {
                AddSystemComponent(components, pnp);
            }
        }
        AddCoolingComponents(components, cooling, tempEpisodes ?? Array.Empty<TemperatureEpisode>(), generatedAtUtc, diag, ignored);

        // Windows always reports the grid (its per-domain scanners exist even
        // when a domain is empty). Elsewhere the grid is meaningful only when
        // a working sub-domain (SMART, GPU, cooling) produced a component -
        // otherwise the tab is hidden client-side. Counted before the ignore
        // filter: ignoring every device is still a working grid.
        var supported = windowsSupported || components.Count > 0;

        // User-ignored devices drop out here, after every module has run, so
        // Overall, the alert service, the widget and the report grid all see
        // the same filtered list.
        components.RemoveAll(c => ignored.Contains(c.Id));

        return new DiagnosticsHealthResponse
        {
            GeneratedAt = generatedAtUtc,
            Supported = supported,
            Overall = WorstStatus(components.Select(c => c.Status)),
            Components = components,
        };
    }

    private static void AddStorageComponents(List<HealthComponent> components, SmartSnapshot smart)
    {
        if (!smart.Supported)
        {
            return;
        }

        foreach (var drive in smart.Drives)
        {
            var reasons = drive.DetailedReasons
                .Select(r => new HealthComponentReason(r.Code, MapReasonSeverity(r.Severity), r.Summary, r.Detail))
                .ToList();
            var status = MapDriveStatus(drive.Status);
            // A drive that reports no SMART (USB bridge, RAID-hidden) explains
            // itself the same way the GPU placeholder does, so an unmeasured
            // component is never silently folded into a healthy tile.
            if (status == HealthStatuses.Unknown && reasons.Count == 0)
            {
                var label = string.IsNullOrWhiteSpace(drive.Name) ? "this drive" : drive.Name;
                reasons.Add(new HealthComponentReason("storage.noSmartSource", HealthStatuses.Unknown,
                    $"SMART data cannot be read for {label}",
                    "The drive does not expose SMART data, which USB enclosures and drives behind a RAID controller commonly do not, so its health cannot be assessed."));
            }
            components.Add(new HealthComponent
            {
                Id = drive.Id,
                Kind = "storage",
                Name = drive.Name,
                Status = status,
                Reasons = reasons,
            });
        }
    }

    private static string MapDriveStatus(string status) => status switch
    {
        "good" => HealthStatuses.Ok,
        "caution" => HealthStatuses.Watch,
        "warning" => HealthStatuses.Act,
        "bad" => HealthStatuses.Act,
        _ => HealthStatuses.Unknown,
    };

    private static string MapReasonSeverity(ReasonSeverity severity) =>
        severity == ReasonSeverity.Act ? HealthStatuses.Act : HealthStatuses.Watch;

    /// <summary>
    /// A temperature episode counts as a current warning only while it is
    /// still ongoing or just barely ended: recency window is the wider of one
    /// bucket's width (accounts for the latest bucket not having flushed yet)
    /// and the user's configured linger. Default linger 0 means only an
    /// episode that is ongoing right now (or ended within one bucket) shows a
    /// warning; the 24h+ episode history stays on GET /diagnostics/temperatures,
    /// which calls TemperatureInsights.DetectEpisodes directly and never
    /// passes through this recency filter.
    ///
    /// An episode whose ComponentId is an ignored component id is dropped
    /// too: storage episodes carry the drive's own "storage:&lt;serial&gt;" id,
    /// so an ignored drive that runs hot stays silent. (GPU episodes use the
    /// adapter id, not the "gpu:&lt;n&gt;" health id, so they never match.)
    /// </summary>
    private static void AddCoolingComponents(
        List<HealthComponent> components,
        CoolingStallSnapshot cooling,
        IReadOnlyList<TemperatureEpisode> tempEpisodes,
        DateTime generatedAtUtc,
        DiagnosticsSettings diagnostics,
        IReadOnlySet<string> ignored)
    {
        var recencyMinutes = Math.Max(TemperatureInsights.NativeBucketMinutes, diagnostics.WarningLingerMinutes);
        var cutoffUtc = generatedAtUtc.AddMinutes(-recencyMinutes);
        var recentEpisodes = tempEpisodes
            .Where(e => e.EndUtc >= cutoffUtc && e.StartUtc <= generatedAtUtc)
            .Where(e => IsTempKindEnabled(e.Kind, diagnostics.Components))
            .Where(e => !ignored.Contains(e.ComponentId))
            .ToList();

        var stallEligible = diagnostics.Components.Cooling && cooling.Devices.Count > 0;
        if (!stallEligible && recentEpisodes.Count == 0)
        {
            return;
        }

        if (diagnostics.Components.Cooling)
        {
            foreach (var device in cooling.Devices)
            {
                if (device.Status != CoolingStallStatuses.Stalled && device.Status != CoolingStallStatuses.Suspect)
                {
                    continue;
                }

                var isPump = string.Equals(device.Type, "pump", StringComparison.OrdinalIgnoreCase);
                var code = isPump ? "cooling.pumpStall" : "cooling.fanStall";
                var severity = device.Status == CoolingStallStatuses.Stalled ? HealthStatuses.Act : HealthStatuses.Watch;
                var summary = device.Status == CoolingStallStatuses.Stalled
                    ? $"{device.Name} reports 0 RPM while driven"
                    : $"{device.Name} reports 0 RPM at low duty";
                var detail = $"rpm={Fmt(device.Rpm)} targetDuty={Fmt(device.TargetDutyPercent)}% since={device.SinceUtc:O}";

                components.Add(new HealthComponent
                {
                    Id = $"cooling:{device.Id}",
                    Kind = "cooling",
                    Name = device.Name,
                    Status = severity,
                    Reasons = new List<HealthComponentReason> { new(code, severity, summary, detail) },
                });
            }
        }

        var reasons = recentEpisodes.Select(ep => new HealthComponentReason(
            "cooling.sustainedHighTemp",
            HealthStatuses.Watch,
            $"{ep.Name} recently ran above {ep.ThresholdC:0} C",
            $"componentId={ep.ComponentId} peakC={ep.PeakC:0.0} start={ep.StartUtc:O} end={ep.EndUtc:O}")).ToList();

        if (stallEligible || reasons.Count > 0)
        {
            components.Add(new HealthComponent
            {
                Id = "cooling",
                Kind = "cooling",
                Name = $"Cooling ({cooling.Devices.Count})",
                Status = WorstReasonStatus(reasons, HealthStatuses.Ok),
                Reasons = reasons,
            });
        }
    }

    private static bool IsTempKindEnabled(string kind, DiagnosticsComponents components) => kind switch
    {
        "cpu" => components.Cpu,
        "gpu" => components.Gpu,
        "storage" => components.Storage,
        "ram" => components.Ram,
        _ => true,
    };

    private static string Fmt(double? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";

    private static void AddGpuComponents(
        List<HealthComponent> components,
        GpuHealthSnapshot gpu,
        IReadOnlyList<string> knownGpuModels)
    {
        if (gpu.Supported)
        {
            for (var i = 0; i < gpu.Gpus.Count; i++)
            {
                var info = gpu.Gpus[i];
                var reasons = BuildGpuReasons(info.Throttle);
                components.Add(new HealthComponent
                {
                    Id = $"gpu:{i}",
                    Kind = "gpu",
                    Name = info.Name,
                    Status = WorstReasonStatus(reasons, HealthStatuses.Ok),
                    Reasons = reasons,
                });
            }
            return;
        }

        // Placeholder only when sensor detection knows a GPU exists; its
        // unknown-severity reason explains the gap and cannot notify or log.
        if (knownGpuModels.Count == 0)
        {
            return;
        }

        var name = knownGpuModels.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) ?? "GPU";
        components.Add(new HealthComponent
        {
            Id = "gpu:0",
            Kind = "gpu",
            Name = name,
            Status = HealthStatuses.Unknown,
            Reasons = new List<HealthComponentReason>
            {
                new("gpu.noHealthSource", HealthStatuses.Unknown,
                    $"Throttle state cannot be read for {name}",
                    "Live GPU health readings come from NVML, the library NVIDIA drivers install. It is not available on this system, so this GPU reports no health signal either way."),
            },
        });
    }

    private static List<HealthComponentReason> BuildGpuReasons(GpuThrottleInfo throttle)
    {
        var reasons = new List<HealthComponentReason>();

        if (throttle.Active.Contains("hwThermal") || throttle.Active.Contains("hwPowerBrake"))
        {
            reasons.Add(new HealthComponentReason("gpu.thermalThrottle", HealthStatuses.Watch,
                "GPU is hardware throttling",
                "The GPU reports an active hardware thermal or power-brake throttle, both driven by the board directly rather than software policy."));
        }

        return reasons;
    }

    private static void AddMemoryComponent(List<HealthComponent> components, MemoryTestResult? lastMemoryTest)
    {
        var reasons = new List<HealthComponentReason>();

        if (lastMemoryTest is { Result: MemoryTestResult.Failed })
        {
            reasons.Add(new HealthComponentReason("memory.testFailed", HealthStatuses.Act,
                "The last Windows Memory Diagnostic run reported errors",
                lastMemoryTest.Detail ?? "Microsoft-Windows-MemoryDiagnostics-Results logged a failed run."));
        }

        components.Add(new HealthComponent
        {
            Id = "memory",
            Kind = "memory",
            Name = "Memory",
            Status = WorstReasonStatus(reasons, HealthStatuses.Ok),
            Reasons = reasons,
        });
    }

    private static void AddSystemComponent(List<HealthComponent> components, PnpProblemSnapshot pnp)
    {
        var reasons = new List<HealthComponentReason>();

        if (pnp.Devices.Count >= 1)
        {
            reasons.Add(new HealthComponentReason("system.pnpProblems", HealthStatuses.Watch,
                $"{pnp.Devices.Count} device(s) reporting a Device Manager problem",
                "Windows Device Manager reports a non-zero ConfigManagerErrorCode for at least one device."));
        }

        components.Add(new HealthComponent
        {
            Id = "system",
            Kind = "system",
            Name = "System",
            Status = WorstReasonStatus(reasons, HealthStatuses.Ok),
            Reasons = reasons,
        });
    }

    private static string WorstReasonStatus(IReadOnlyList<HealthComponentReason> reasons, string baseline)
    {
        if (reasons.Any(r => r.Severity == HealthStatuses.Act))
        {
            return HealthStatuses.Act;
        }
        if (reasons.Any(r => r.Severity == HealthStatuses.Watch))
        {
            return HealthStatuses.Watch;
        }
        return baseline;
    }

    /// <summary>Worst status across components; "unknown" is the absence of a
    /// signal, so it carries only when no component produced a real one.</summary>
    private static string WorstStatus(IEnumerable<string> statuses)
    {
        var list = statuses.ToList();
        if (list.Contains(HealthStatuses.Act))
        {
            return HealthStatuses.Act;
        }
        if (list.Contains(HealthStatuses.Watch))
        {
            return HealthStatuses.Watch;
        }
        if (list.Count == 0 || list.Contains(HealthStatuses.Ok))
        {
            return HealthStatuses.Ok;
        }
        return HealthStatuses.Unknown;
    }
}
