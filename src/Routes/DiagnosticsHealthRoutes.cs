using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Activity.Storage;
using Nexus.Service.Auth;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.Cooling;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Diagnostics.Report;
using Nexus.Service.Diagnostics.Storage;
using Nexus.Service.Diagnostics.SystemInfo;
using Nexus.Service.Diagnostics.Temperature;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting;
using Nexus.Service.Models;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Routes;

/// <summary>
/// Diagnostics app REST surface: aggregated health, per-domain detail, memory
/// test scheduling, and the support-bundle download. Contract frozen in
/// .deep-build/diagnostics-contract.md. Only /diagnostics/health,
/// /diagnostics/cooling, and /diagnostics/temperatures carry AllowPanel() -
/// the set the panel widget consumes; every other GET stays on the default
/// token auth. The memory test POST/DELETE also stay on the default auth
/// since scheduling a reboot diagnostic is a dashboard-only action;
/// support-bundle/download is LocalhostOnly like the existing
/// /diagnostics/open-logs route.
/// </summary>
public static class DiagnosticsHealthRoutes
{
    // EventLogMonitor's backfill window is 30 days (DiagnosticEventCatalog),
    // so the store never actually holds more history than that regardless of
    // a larger requested window.
    private const int MaxIncidentDays = 30;

    private const int MinTemperatureHours = 1;
    // get_temperature_history (Nexus.Service.Mcp.Tools.GetTemperatureHistoryTool)
    // reuses this so its own window cap never exceeds the route's.
    internal const int MaxTemperatureHours = 336;
    private const int DefaultTemperatureHours = 168;

    // Defensive backstop only: TemperatureInsights.TierWidthMinutesFor already
    // bounds each series well under this cap for every window up to MaxTemperatureHours.
    // get_temperature_history reuses this value too, for the same reason.
    internal const int MaxPointsPerSeries = 600;

    public static void MapDiagnosticsHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/diagnostics/health", (string? refresh, DiagnosticsHealthModel model, FeatureGates gates) =>
            gates.Diagnostics
                ? model.BuildHealth(IsRefresh(refresh))
                : new DiagnosticsHealthResponse { Enabled = false }).AllowPanel();

        // gpuDriver is excluded from the live feed (too noisy to act on) but
        // still collected internally and still shown in the support bundle's
        // ungrouped incidents.json.
        app.MapGet("/diagnostics/incidents", (int? days, EventLogMonitor events, SteamGameLibraryCache steamCache) =>
            BuildIncidentsResponse(events, steamCache, Math.Clamp(days ?? MaxIncidentDays, 1, MaxIncidentDays), group: true, includeGpuDriver: false));

        app.MapGet("/diagnostics/smart", (string? refresh, SmartHealthMonitor smart) =>
        {
            if (IsRefresh(refresh))
            {
                smart.ForceRefresh();
            }
            return smart.Snapshot();
        });

        app.MapGet("/diagnostics/memory", (string? refresh, MemoryDiagnosticOrchestrator memDiag) =>
        {
            if (IsRefresh(refresh))
            {
                memDiag.ForceRefresh();
            }
            return BuildMemoryResponse(memDiag);
        });

        app.MapPost("/diagnostics/memory/test", (MemoryDiagnosticOrchestrator memDiag, FeatureGates gates) =>
        {
            if (!gates.Diagnostics)
            {
                return Results.Conflict(new FeatureDisabledResponse { Feature = FeatureNames.Diagnostics });
            }
            memDiag.Schedule();
            return Results.Ok(new MemoryTestScheduleResponse { Scheduled = memDiag.IsScheduled(), RequiresReboot = true });
        });

        app.MapDelete("/diagnostics/memory/test", (MemoryDiagnosticOrchestrator memDiag) =>
        {
            memDiag.Cancel();
            return new MemoryTestCancelResponse { Scheduled = memDiag.IsScheduled() };
        });

        app.MapGet("/diagnostics/gpu", (string? refresh, GpuHealthMonitor gpu, EventLogMonitor events) =>
            BuildGpuResponse(gpu, events, IsRefresh(refresh)));

        app.MapGet("/diagnostics/cooling", (CoolingStallDetector cooling) => cooling.Snapshot()).AllowPanel();

        // date (yyyy-MM-dd, service host's local calendar day) takes priority
        // over hours when both are present.
        app.MapGet("/diagnostics/temperatures", (int? hours, string? date, IMetricsHistoryStore tempStore) =>
        {
            if (!TryResolveTemperatureWindow(hours, date, out var fromUtcMs, out var toUtcMs, out var tierWidthMinutes, out var error))
            {
                return Results.BadRequest(ApiResponse.Fail(error!));
            }
            return QueryTemperatures(tempStore, fromUtcMs, toUtcMs, tierWidthMinutes);
        }).AllowPanel();

        // App-usage overlay for the temperature chart: dominant foreground app
        // per tier-width slot on the same window grid. Default (dashboard-token)
        // auth, NOT AllowPanel - app names carry Screen Time's privacy scope and
        // must not reach the phone panel.
        app.MapGet("/diagnostics/temperatures/apps", (int? hours, string? date, IScreenTimeStore screenTime) =>
        {
            if (!TryResolveTemperatureWindow(hours, date, out var fromUtcMs, out var toUtcMs, out var tierWidthMinutes, out var error))
            {
                return Results.BadRequest(ApiResponse.Fail(error!));
            }
            return Results.Ok(BuildTemperatureAppUsage(screenTime, fromUtcMs, toUtcMs, tierWidthMinutes));
        });

        app.MapGet("/diagnostics/system", (string? refresh, PnpProblemScanner pnp, EventLogMonitor events) =>
            BuildSystemResponse(pnp, events, IsRefresh(refresh)));

        // Everything a bug report needs, as one download; loopback-only like open-logs.
        app.MapGet("/diagnostics/support-bundle/download", (
            DiagnosticsHealthModel healthModel,
            EventLogMonitor events,
            SteamGameLibraryCache steamCache,
            SmartHealthMonitor smart,
            MemoryDiagnosticOrchestrator memDiag,
            GpuHealthMonitor gpu,
            CoolingStallDetector cooling,
            PnpProblemScanner pnp,
            Nexus.Service.Persistence.IConfigStore store,
            Nexus.Service.Conflicts.IConflictDetector conflicts,
            IServiceProvider sp) =>
        {
            var bridge = sp.GetService<Nexus.Service.Lighting.Rgb.RgbBridge>();
            var daemon = sp.GetService<Nexus.Service.Lighting.Rgb.OpenRgbProcessManager>();
            var info = new SupportInfo
            {
                Version = BuildInfo.Version,
#if DEV_TOOLS
                DevTools = true,
#endif
                Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                MachineName = Environment.MachineName,
                ExportedAtUtc = DateTime.UtcNow.ToString("O"),
                ServiceUptimeSeconds = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalSeconds,
                ConflictsRunning = conflicts.GetConflicts().Select(c => $"{c.Id} (pid {c.Pid})").ToList(),
                ConflictScanReady = conflicts.DetectionReady,
                LightingBridgeActive = bridge?.IsActive ?? false,
                LightingRescanning = bridge?.IsRescanning ?? false,
                LightingDevices = bridge?.Devices.Select(d => $"[{d.Index}] {d.Name} leds={d.LedCount}").ToList() ?? new(),
                OpenRgbDaemonRunning = daemon?.IsRunning ?? false,
                OpenRgbDaemonUptimeSeconds = daemon?.Uptime.TotalSeconds ?? 0,
            };
            var zipBytes = SupportBundleBuilder.Build(new SupportBundleBuilder.Sources
            {
                LogsDirectory = ServiceLog.LogsDirectory,
                UpdatesDirectory = Nexus.Service.Update.UpdateDownloader.StagingDir,
                OpenRgbConfigDirectory = Nexus.Service.Lighting.Rgb.OpenRgbProcessManager.ResolveConfigDir(),
                Settings = store.Load(),
                Info = info,
                StartupSnapshot = SupportBundleBuilder.ReadStartupSnapshot(),
                Health = healthModel.BuildHealth(),
                Incidents = BuildIncidentsResponse(events, steamCache, MaxIncidentDays, group: false),
                Smart = smart.Snapshot(),
                Memory = BuildMemoryResponse(memDiag),
                Gpu = BuildGpuResponse(gpu, events),
                Cooling = cooling.Snapshot(),
                System = BuildSystemResponse(pnp, events),
            });
            var fileName = $"nexus-support-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
            return Results.File(zipBytes, "application/zip", fileName);
        }).LocalhostOnly();

        app.MapGet("/diagnostics/report.pdf", async (
            DiagnosticsHealthModel healthModel,
            SystemSpecsCollector specs,
            SmartHealthMonitor smart,
            GpuHealthMonitor gpu,
            EventLogMonitor events,
            MemoryDiagnosticOrchestrator memDiag,
            PnpProblemScanner pnp) =>
        {
            var health = healthModel.BuildHealth();
            var smartSnapshot = smart.Snapshot();
            var snapshot = await DiagnosticsReportBuilder.GatherAsync(health, specs, smartSnapshot, gpu, events, memDiag, pnp);
            var pdfBytes = DiagnosticsReportBuilder.Build(snapshot);
            var fileName = $"nexus-diagnostics-report-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}.pdf";
            return Results.File(pdfBytes, "application/pdf", fileName);
        }).LocalhostOnly();

        // Mirrors /diagnostics/open-logs: the service is LocalSystem in Session
        // 0 and cannot show eventvwr.msc itself, so it hands off to the
        // user-session helper over the pipe.
        app.MapPost("/diagnostics/events/open-viewer", (IServiceProvider sp) =>
        {
            try
            {
#if WINDOWS
                var registry = sp.GetRequiredService<Nexus.Service.Helper.HelperRegistry>();
                _ = Nexus.Service.Helper.Domains.DiagnosticsCommands.OpenEventViewerAsync(registry);
                return Results.Ok(new OpenEventViewerResponse { Opened = true });
#else
                return Results.Ok(new OpenEventViewerResponse { Opened = false, Error = "Event Viewer is only available on Windows" });
#endif
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[diagnostics] open-event-viewer failed: {ex.Message}");
                return Results.Ok(new OpenEventViewerResponse { Opened = false, Error = ex.Message });
            }
        }).LocalhostOnly();

        // Mirrors /diagnostics/events/open-viewer: hands off to the
        // user-session helper since the service cannot show devmgmt.msc
        // itself from Session 0.
        app.MapPost("/diagnostics/devices/open-manager", (IServiceProvider sp) =>
        {
            try
            {
#if WINDOWS
                var registry = sp.GetRequiredService<Nexus.Service.Helper.HelperRegistry>();
                _ = Nexus.Service.Helper.Domains.DiagnosticsCommands.OpenDeviceManagerAsync(registry);
                return Results.Ok(new OpenDeviceManagerResponse { Opened = true });
#else
                return Results.Ok(new OpenDeviceManagerResponse { Opened = false, Error = "Device Manager is only available on Windows" });
#endif
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[diagnostics] open-device-manager failed: {ex.Message}");
                return Results.Ok(new OpenDeviceManagerResponse { Opened = false, Error = ex.Message });
            }
        }).LocalhostOnly();

        // Destructive and desktop-only: wipes the System and Application event
        // logs via wevtutil, then resyncs EventLogMonitor's in-memory store so
        // it stops serving now-deleted incidents.
        app.MapPost("/diagnostics/events/clear", async (EventLogMonitor events) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return new EventLogClearResponse { Supported = false };
            }

            const int ClearTimeoutMs = 10_000;
            var systemResult = await DiagnosticsShell.RunAsync("wevtutil.exe", ClearTimeoutMs, "cl", "System");
            var appResult = await DiagnosticsShell.RunAsync("wevtutil.exe", ClearTimeoutMs, "cl", "Application");
            await events.ResetAndBackfillAsync();

            var systemOk = systemResult.ExitCode == 0;
            var appOk = appResult.ExitCode == 0;
            return new EventLogClearResponse
            {
                Supported = true,
                Cleared = systemOk && appOk,
                SystemError = systemOk ? null : DescribeShellFailure(systemResult),
                ApplicationError = appOk ? null : DescribeShellFailure(appResult),
            };
        }).LocalhostOnly();
    }

    // Shared window resolution for the temperatures + app-usage endpoints so
    // both sit on the same [from, to] range and tier width, keeping the app
    // bands aligned to the temperature buckets.
    private static bool TryResolveTemperatureWindow(
        int? hours, string? date,
        out long fromUtcMs, out long toUtcMs, out int tierWidthMinutes, out string? error)
    {
        fromUtcMs = 0;
        toUtcMs = 0;
        tierWidthMinutes = 0;

        if (!string.IsNullOrWhiteSpace(date))
        {
            if (!TemperatureDayWindow.TryResolve(
                    date, DateTimeOffset.UtcNow, TimeZoneInfo.Local, TemperatureInsights.RetentionDays,
                    out fromUtcMs, out toUtcMs, out error))
            {
                return false;
            }
            tierWidthMinutes = TemperatureInsights.TierWidthMinutesFor(24);
            return true;
        }

        var windowHours = Math.Clamp(hours ?? DefaultTemperatureHours, MinTemperatureHours, MaxTemperatureHours);
        toUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        fromUtcMs = toUtcMs - windowHours * 3_600_000L;
        tierWidthMinutes = TemperatureInsights.TierWidthMinutesFor(windowHours);
        error = null;
        return true;
    }

    private static TemperatureAppUsageResponse BuildTemperatureAppUsage(
        IScreenTimeStore screenTime, long fromUtcMs, long toUtcMs, int tierWidthMinutes)
    {
        try
        {
            var sessions = screenTime.QuerySessions(fromUtcMs, toUtcMs);
            var buckets = ScreenTimeUsage.Build(sessions, fromUtcMs, toUtcMs, tierWidthMinutes * 60_000L);
            return new TemperatureAppUsageResponse { Supported = true, BucketMinutes = tierWidthMinutes, Buckets = buckets };
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[diagnostics-temperatures-apps] query failed: {ex.Message}");
            return new TemperatureAppUsageResponse { Supported = false };
        }
    }

    private static IResult QueryTemperatures(
        IMetricsHistoryStore tempStore, long fromUtcMs, long toUtcMs, int tierWidthMinutes)
    {
        try
        {
            var rows = tempStore.QueryTemperatureBuckets(fromUtcMs, toUtcMs);
            return Results.Ok(BuildTemperatureResponse(rows, tierWidthMinutes));
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[diagnostics-temperatures] query failed: {ex.Message}");
            return Results.Ok(new TemperatureHistoryResponse { Supported = false });
        }
    }

    internal static TemperatureHistoryResponse BuildTemperatureResponse(
        IReadOnlyList<TemperatureBucketRow> rows, int tierWidthMinutes)
    {
        var widthMs = tierWidthMinutes * 60_000L;
        var series = rows
            .GroupBy(r => r.ComponentId)
            .Select(g =>
            {
                var ordered = g.OrderBy(r => r.BucketUtcMs).ToList();
                var last = ordered[^1];
                var merged = TemperatureInsights.MergeToWidth(ordered, widthMs);
                return new TemperatureSeriesWire
                {
                    Id = g.Key,
                    Kind = last.Kind,
                    Name = last.Name,
                    Points = TemperatureInsights.Decimate(merged, MaxPointsPerSeries)
                        .Select(p => p with { Avg = Math.Round(p.Avg, 1), Max = Math.Round(p.Max, 1) })
                        .ToList(),
                };
            })
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        var episodes = TemperatureInsights.DetectEpisodes(rows)
            .Select(e => e with { PeakC = Math.Round(e.PeakC, 1) })
            .ToList();

        return new TemperatureHistoryResponse
        {
            Supported = true,
            BucketMinutes = tierWidthMinutes,
            RetentionDays = TemperatureInsights.RetentionDays,
            Series = series,
            Episodes = episodes,
        };
    }

    private static string DescribeShellFailure(ShellResult result) =>
        !string.IsNullOrWhiteSpace(result.Stderr) ? result.Stderr.Trim() : $"wevtutil exited {result.ExitCode}";

    // Accepts "1" or "true" (case-insensitive); bool query binding rejects "1".
    private static bool IsRefresh(string? refresh) =>
        refresh is "1" || string.Equals(refresh, "true", StringComparison.OrdinalIgnoreCase);

    // internal: also called by the get_incidents MCP tool, so it serves the
    // same grouped and game-decorated response the REST route does.
    internal static IncidentsResponse BuildIncidentsResponse(
        EventLogMonitor events, SteamGameLibraryCache steamCache, int windowDays, bool group, bool includeGpuDriver = true)
    {
        IReadOnlyList<DiagnosticIncident> incidents = events.Snapshot(windowDays);
        if (!includeGpuDriver)
        {
            incidents = incidents.Where(i => i.Source != DiagnosticEventCatalog.SourceGpuDriver).ToList();
        }

        var decorated = DecorateGameCrashes(incidents, steamCache);
        return new IncidentsResponse
        {
            Supported = OperatingSystem.IsWindows() || events.IsLinuxSupported,
            WindowDays = windowDays,
            Incidents = group ? GroupRepeats(decorated) : decorated,
        };
    }

    /// <summary>Collapses incidents sharing (Source, Title, Severity, App?.Name) into
    /// one row: the newest occurrence, stamped with RepeatCount and the oldest
    /// occurrence's FirstUtc. Severity is in the key because the same title can
    /// carry different severities (e.g. WHEA severity is level-driven), so an
    /// older higher-severity occurrence must not collapse under a newer lower one.
    /// Does not touch EventLogMonitor's store, so CountsSince health thresholds
    /// keep counting real occurrences.</summary>
    internal static IReadOnlyList<DiagnosticIncident> GroupRepeats(IReadOnlyList<DiagnosticIncident> incidents)
    {
        var groups = new Dictionary<(string Source, string Title, string Severity, string? AppName), List<DiagnosticIncident>>();
        foreach (var incident in incidents)
        {
            var key = (incident.Source, incident.Title, incident.Severity, incident.App?.Name);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<DiagnosticIncident>();
                groups[key] = list;
            }
            list.Add(incident);
        }

        var grouped = new List<DiagnosticIncident>(groups.Count);
        foreach (var list in groups.Values)
        {
            if (list.Count == 1)
            {
                grouped.Add(list[0]);
                continue;
            }

            var newest = list.MaxBy(i => i.TimeUtc)!;
            var oldest = list.MinBy(i => i.TimeUtc)!;
            grouped.Add(newest with { RepeatCount = list.Count, FirstUtc = oldest.TimeUtc });
        }

        return grouped.OrderByDescending(i => i.TimeUtc).ToList();
    }

    private static MemoryHealthResponse BuildMemoryResponse(MemoryDiagnosticOrchestrator memDiag)
    {
        var info = MemoryInfoProvider.GetSnapshot();
        return new MemoryHealthResponse
        {
            Supported = info.Supported,
            Modules = info.Modules,
            XmpLikelyActive = info.XmpLikelyActive,
            LastTest = memDiag.LastResult(),
            TestScheduled = memDiag.IsScheduled(),
        };
    }

    private static GpuHealthResponse BuildGpuResponse(GpuHealthMonitor gpu, EventLogMonitor events, bool forceRefresh = false)
    {
        var counts = events.CountsSince(TimeSpan.FromDays(30));
        var tdr = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceTdr);
        var snapshot = gpu.Snapshot(forceRefresh);

        var gpus = snapshot.Gpus.Select(g => new GpuInfoWire
        {
            Name = g.Name,
            DriverVersion = g.DriverVersion,
            TemperatureC = g.TemperatureC,
            PowerW = g.PowerW,
            Throttle = g.Throttle,
            RecentTdrCount = tdr,
        }).ToList();

        return new GpuHealthResponse { Supported = snapshot.Supported, Gpus = gpus };
    }

    private static SystemDiagnosticsResponse BuildSystemResponse(PnpProblemScanner pnp, EventLogMonitor events, bool forceRefresh = false)
    {
        var counts = events.CountsSince(TimeSpan.FromDays(30));
        var snapshot = pnp.Snapshot(forceRefresh);

        return new SystemDiagnosticsResponse
        {
            Supported = snapshot.Supported,
            PnpProblems = snapshot.Devices,
            Counts30d = new SystemDiagnosticsCounts
            {
                Whea = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceWhea),
                Bugchecks = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceBugcheck),
                DirtyShutdowns = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceDirtyShutdown),
                DiskErrors = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceDisk),
                Tdrs = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceTdr),
                AppCrashes = counts.GetValueOrDefault(DiagnosticEventCatalog.SourceAppCrash),
            },
        };
    }

    private static IReadOnlyList<DiagnosticIncident> DecorateGameCrashes(
        IReadOnlyList<DiagnosticIncident> incidents, SteamGameLibraryCache steamCache)
    {
        var libraries = steamCache.GetLibraryPaths();
        if (libraries.Count == 0)
        {
            return incidents;
        }

        var result = new List<DiagnosticIncident>(incidents.Count);
        foreach (var incident in incidents)
        {
            if (incident.App is { } app && !string.IsNullOrEmpty(app.Path)
                && libraries.Any(lib => IsUnderLibrary(app.Path, lib)))
            {
                result.Add(incident with { App = app with { IsGame = true } });
            }
            else
            {
                result.Add(incident);
            }
        }
        return result;
    }

    // A plain StartsWith would match "D:\SteamLibrary2\..." against library
    // "D:\SteamLibrary" - require the prefix to end exactly at a directory
    // boundary (or the whole path) before declaring the crash under it.
    private static bool IsUnderLibrary(string path, string libraryPath)
    {
        var lib = libraryPath.TrimEnd('\\', '/');
        if (!path.StartsWith(lib, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return path.Length == lib.Length || path[lib.Length] is '\\' or '/';
    }
}

/// <summary>Caches SteamLibraryLocator's library paths for 10 minutes - every
/// appCrash incident on every /diagnostics/incidents poll would otherwise
/// re-read the registry and libraryfolders.vdf.</summary>
public sealed class SteamGameLibraryCache
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private List<string> _paths = new();
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public IReadOnlyList<string> GetLibraryPaths()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _cachedAtUtc >= CacheTtl)
            {
                _paths = SteamLibraryLocator.EnumerateLibraryPaths().ToList();
                _cachedAtUtc = now;
            }
            return _paths;
        }
    }
}

// ----- Wire response wrappers (module DTOs don't match the contract JSON exactly) -----

public sealed record IncidentsResponse
{
    public bool Supported { get; init; }
    public int WindowDays { get; init; }
    public IReadOnlyList<DiagnosticIncident> Incidents { get; init; } = Array.Empty<DiagnosticIncident>();
}

public sealed record GpuInfoWire
{
    public string Name { get; init; } = "";
    public string? DriverVersion { get; init; }
    public double? TemperatureC { get; init; }
    public double? PowerW { get; init; }
    public GpuThrottleInfo Throttle { get; init; } = new(Array.Empty<string>(), null, null, null, null);
    public int RecentTdrCount { get; init; }
}

public sealed record GpuHealthResponse
{
    public bool Supported { get; init; }
    public IReadOnlyList<GpuInfoWire> Gpus { get; init; } = Array.Empty<GpuInfoWire>();
}

public sealed record MemoryHealthResponse
{
    public bool Supported { get; init; }
    public IReadOnlyList<MemoryModuleInfo> Modules { get; init; } = Array.Empty<MemoryModuleInfo>();
    public bool? XmpLikelyActive { get; init; }
    public MemoryTestResult? LastTest { get; init; }
    public bool TestScheduled { get; init; }
}

public sealed record MemoryTestScheduleResponse
{
    public bool Scheduled { get; init; }
    public bool RequiresReboot { get; init; }
}

public sealed record MemoryTestCancelResponse
{
    public bool Scheduled { get; init; }
}

public sealed record SystemDiagnosticsCounts
{
    public int Whea { get; init; }
    public int Bugchecks { get; init; }
    public int DirtyShutdowns { get; init; }
    public int DiskErrors { get; init; }
    public int Tdrs { get; init; }
    public int AppCrashes { get; init; }
}

public sealed record SystemDiagnosticsResponse
{
    public bool Supported { get; init; }
    public IReadOnlyList<PnpProblemDevice> PnpProblems { get; init; } = Array.Empty<PnpProblemDevice>();
    public SystemDiagnosticsCounts Counts30d { get; init; } = new();
}

public sealed record EventLogClearResponse
{
    public bool Supported { get; init; }
    public bool Cleared { get; init; }
    public string? SystemError { get; init; }
    public string? ApplicationError { get; init; }
}

public sealed record TemperatureSeriesWire
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public IReadOnlyList<TemperaturePoint> Points { get; init; } = Array.Empty<TemperaturePoint>();
}

public sealed record TemperatureHistoryResponse
{
    public bool Supported { get; init; }
    public int BucketMinutes { get; init; } = Nexus.Service.Diagnostics.Temperature.TemperatureInsights.NativeBucketMinutes;
    public int RetentionDays { get; init; } = Nexus.Service.Diagnostics.Temperature.TemperatureInsights.RetentionDays;
    public IReadOnlyList<TemperatureSeriesWire> Series { get; init; } = Array.Empty<TemperatureSeriesWire>();
    public IReadOnlyList<TemperatureEpisode> Episodes { get; init; } = Array.Empty<TemperatureEpisode>();
}

public sealed record OpenEventViewerResponse
{
    public bool Opened { get; init; }
    public string? Error { get; init; }
}

public sealed record OpenDeviceManagerResponse
{
    public bool Opened { get; init; }
    public string? Error { get; init; }
}
