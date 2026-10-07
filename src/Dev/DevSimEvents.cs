#if DEV_TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Conflicts;
using Nexus.Service.Cooling;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Models.Conflicts;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Devices;
using Nexus.Service.Models.Update;
using Nexus.Service.Persistence;

namespace Nexus.Service.Dev;

public sealed record DevSimCatalogEntry(string Id, string Category, string Label);

/// <summary>
/// Dev-tools simulated events: what the UI would show if the event were real. Strictly an overlay on
/// read paths. It never writes a fan or a curve, never mutates settings, and holds its state in memory
/// only (cleared on restart).
/// </summary>
public sealed class DevSimEvents
{
    public const string GuardLimitTrip = "guard.limitTrip";
    public const string GuardCoolingLossTrip = "guard.coolingLossTrip";
    public const string GuardEscalated = "guard.escalated";
    public const string GuardWatchdogLatched = "guard.watchdogLatched";
    public const string GuardEndedTrip = "guard.endedTrip";
    public const string GuardPendingHeal = "guard.pendingHeal";
    public const string GuardGpuHandback = "guard.gpuHandback";
    public const string HealthFanStall = "health.fanStall";
    public const string HealthPumpStall = "health.pumpStall";
    public const string HealthSustainedHighTemp = "health.sustainedHighTemp";
    public const string HealthGpuThrottle = "health.gpuThrottle";
    public const string HealthGpuTdr = "health.gpuTdr";
    public const string HealthSmartWarning = "health.smartWarning";
    public const string HealthNvmeCritical = "health.nvmeCritical";
    public const string HealthMemoryWhea = "health.memoryWhea";
    public const string IncidentBsod = "incident.bsod";
    public const string IncidentAppCrash = "incident.appCrash";
    public const string IncidentDirtyShutdown = "incident.dirtyShutdown";
    public const string AppUpdateAvailable = "app.updateAvailable";
    public const string DeviceFirmwareUpdate = "device.firmwareUpdate";
    public const string AppConflict = "app.conflict";
    public const string DeviceDisconnect = "device.disconnect";

    /// <summary>The limit used when the guard reports none (it is switched off): the same default the real guard falls back to.</summary>
    public const double FallbackLimitC = ThermalLimits.GenericDefaultC;

    public static readonly IReadOnlyList<DevSimCatalogEntry> Catalog = new DevSimCatalogEntry[]
    {
        new(GuardLimitTrip, "guard", "Thermal guard: limit trip"),
        new(GuardCoolingLossTrip, "guard", "Thermal guard: cooling-loss trip"),
        new(GuardEscalated, "guard", "Thermal guard: escalated to BIOS"),
        new(GuardWatchdogLatched, "guard", "Thermal guard: watchdog latched"),
        new(GuardEndedTrip, "guard", "Thermal guard: ended trip (unacknowledged)"),
        new(GuardPendingHeal, "guard", "Thermal guard: heal waiting for Keep or Undo"),
        new(GuardGpuHandback, "guard", "Thermal guard: GPU fan handed back"),
        new(HealthFanStall, "health", "Health: fan stall"),
        new(HealthPumpStall, "health", "Health: pump stall"),
        new(HealthSustainedHighTemp, "health", "Health: sustained high temperature"),
        new(HealthGpuThrottle, "health", "Health: GPU throttling"),
        new(HealthGpuTdr, "health", "Health: GPU driver reset (TDR)"),
        new(HealthSmartWarning, "health", "Health: SMART warning"),
        new(HealthNvmeCritical, "health", "Health: NVMe critical warning"),
        new(HealthMemoryWhea, "health", "Health: memory WHEA errors"),
        new(IncidentBsod, "incident", "Incident: blue screen"),
        new(IncidentAppCrash, "incident", "Incident: app crash"),
        new(IncidentDirtyShutdown, "incident", "Incident: dirty shutdown"),
        new(AppUpdateAvailable, "device", "App update available"),
        new(DeviceFirmwareUpdate, "device", "Device firmware update available"),
        new(AppConflict, "device", "Conflicting RGB app running"),
        new(DeviceDisconnect, "device", "Device disconnected"),
    };

    private static readonly HashSet<string> Ids = Catalog.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

    // The singleton instance the read-path overlays consult; null in tests that build their own.
    public static DevSimEvents? Current { get; private set; }

    /// <summary>Supplies the real effective CPU limit (null when the guard reports none), so every simulated trip uses it.</summary>
    public Func<double?>? EffectiveLimit { get; set; }

    public double CurrentLimitC => EffectiveLimit?.Invoke() ?? FallbackLimitC;

    /// <summary>Raised after the active set changed, so the host can broadcast the topics the affected surfaces listen on.</summary>
    public event Action? Changed;

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _active = new(StringComparer.Ordinal);
    private readonly Func<long> _nowMs;

    public DevSimEvents(Func<long>? nowMs = null, bool registerAsCurrent = true)
    {
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (registerAsCurrent)
        {
            Current = this;
        }
    }

    public static bool IsKnown(string id) => Ids.Contains(id);

    public IReadOnlyList<(string Id, long StartedAtUtcMs)> Active()
    {
        lock (_gate)
        {
            return Catalog.Where(c => _active.ContainsKey(c.Id)).Select(c => (c.Id, _active[c.Id])).ToList();
        }
    }

    public bool IsActive(string id)
    {
        lock (_gate) { return _active.ContainsKey(id); }
    }

    /// <summary>Starts a simulation. False for an unknown id. Idempotent: <paramref name="newlyStarted"/> is false when it was already running.</summary>
    public bool Start(string id, out bool newlyStarted)
    {
        newlyStarted = false;
        if (!IsKnown(id))
        {
            return false;
        }
        lock (_gate)
        {
            if (!_active.ContainsKey(id))
            {
                _active[id] = _nowMs();
                newlyStarted = true;
            }
        }
        if (newlyStarted)
        {
            Changed?.Invoke();
        }
        return true;
    }

    public bool Stop(string id)
    {
        if (!IsKnown(id))
        {
            return false;
        }
        bool removed;
        lock (_gate) { removed = _active.Remove(id); }
        if (removed)
        {
            Changed?.Invoke();
        }
        return true;
    }

    public void Clear()
    {
        bool any;
        lock (_gate)
        {
            any = _active.Count > 0;
            _active.Clear();
        }
        if (any)
        {
            Changed?.Invoke();
        }
    }

    private long? StartedAt(string id)
    {
        lock (_gate) { return _active.TryGetValue(id, out var t) ? t : null; }
    }

    // ── Thermal guard ──

    // One precedence everywhere: escalated over limit over cooling-loss. An ended trip only shows when none is active.
    private string? ActiveTripSim() =>
        new[] { GuardEscalated, GuardLimitTrip, GuardCoolingLossTrip }.FirstOrDefault(IsActive);

    private static double TripPeak(string simId, double limitC) => simId == GuardCoolingLossTrip ? limitC - 10 : limitC + 4;

    private static string TripReason(string simId) => simId == GuardCoolingLossTrip ? ThermalTripReasons.CoolingLoss : ThermalTripReasons.Limit;

    /// <summary>Overlays GET /cooling/guard. The response is a fresh object per call, so it is edited in place.</summary>
    public void ApplyGuard(ThermalGuardResponse r)
    {
        var limit = r.LimitC ?? FallbackLimitC;
        if (StartedAt(GuardEndedTrip) is { } ended)
        {
            r.LastTrip = new ThermalGuardTripDto
            {
                AtUtcMs = ended - 10 * 60_000,
                PeakC = TripPeak(GuardEndedTrip, limit),
                Reason = ThermalTripReasons.Limit,
                Escalated = false,
                EndedAtUtcMs = ended - 5 * 60_000,
                Acknowledged = false,
            };
        }
        if (ActiveTripSim() is { } trip && StartedAt(trip) is { } startedAt)
        {
            SetActiveTrip(r, startedAt, TripPeak(trip, limit), TripReason(trip), escalated: trip == GuardEscalated,
                state: trip == GuardEscalated ? "escalated" : "tripped");
        }
        if (IsActive(GuardWatchdogLatched))
        {
            r.WatchdogLatched = true;
        }
        if (StartedAt(GuardPendingHeal) is { } heal)
        {
            r.Heal = new HealStateDto
            {
                UndoAvailable = true,
                HealedAtUtcMs = heal,
                Channels = new List<HealChannelDto>
                {
                    new() { Id = "sim:fan-1", Name = "Simulated Fan #1", Hazard = "follows-stoppable-source" },
                    new() { Id = "sim:fan-2", Name = "Simulated Fan #2", Hazard = "low-ceiling" },
                },
            };
        }
        if (IsActive(GuardGpuHandback))
        {
            r.Gpus.Add(new GpuGuardDto
            {
                Id = "sim:gpu-0",
                Name = "Simulated GPU",
                TempC = 86,
                LimitC = 90,
                LimitSource = "hardware",
                State = "handedBack",
            });
        }
    }

    private static void SetActiveTrip(ThermalGuardResponse r, long startedAt, double peak, string reason, bool escalated, string state)
    {
        r.State = state;
        r.GuardTempC = peak;
        r.SinceUtcMs = startedAt;
        r.LastTrip = new ThermalGuardTripDto
        {
            AtUtcMs = startedAt,
            PeakC = peak,
            Reason = reason,
            Escalated = escalated,
            EndedAtUtcMs = null,
            Acknowledged = false,
        };
    }

    /// <summary>The simulated trip as a persisted-shape record, for the Diagnostics component, at the real effective limit. Null when no trip sim runs.</summary>
    public ThermalGuardTripRecord? SimulatedTrip()
    {
        var limit = CurrentLimitC;
        if (ActiveTripSim() is { } trip && StartedAt(trip) is { } startedAt)
        {
            return new ThermalGuardTripRecord
            {
                AtUtcMs = startedAt,
                PeakC = TripPeak(trip, limit),
                Reason = TripReason(trip),
                Escalated = trip == GuardEscalated,
            };
        }
        if (StartedAt(GuardEndedTrip) is { } ended)
        {
            return new ThermalGuardTripRecord
            {
                AtUtcMs = ended - 10 * 60_000,
                EndedAtUtcMs = ended - 5 * 60_000,
                PeakC = TripPeak(GuardEndedTrip, limit),
                Reason = ThermalTripReasons.Limit,
            };
        }
        return null;
    }

    // ── Diagnostics health ──

    private static HealthComponent Component(string id, string kind, string name, string status, string code, string summary, string detail) => new()
    {
        Id = id,
        Kind = kind,
        Name = name,
        Status = status,
        Reasons = new List<HealthComponentReason> { new(code, status, summary, detail) },
    };

    /// <summary>The extra health components the active health sims contribute, with the real reason codes.</summary>
    public IReadOnlyList<HealthComponent> HealthComponents()
    {
        var list = new List<HealthComponent>();
        if (IsActive(HealthFanStall))
        {
            list.Add(Component("cooling:sim-fan", "cooling", "Simulated Fan #1", HealthStatuses.Act, "cooling.fanStall",
                "Simulated Fan #1 reports 0 RPM while driven", "rpm=0 targetDuty=60% (simulated)"));
        }
        if (IsActive(HealthPumpStall))
        {
            list.Add(Component("cooling:sim-pump", "cooling", "Simulated AIO pump", HealthStatuses.Act, "cooling.pumpStall",
                "Simulated AIO pump reports 0 RPM while driven", "rpm=0 targetDuty=70% (simulated)"));
        }
        if (IsActive(HealthSustainedHighTemp))
        {
            list.Add(Component("cooling", "cooling", "Cooling", HealthStatuses.Watch, "cooling.sustainedHighTemp",
                "Simulated CPU recently ran above 90 C", "componentId=sim peakC=94.0 (simulated)"));
        }
        if (IsActive(HealthGpuThrottle))
        {
            list.Add(Component("gpu:sim-throttle", "gpu", "Simulated GPU", HealthStatuses.Watch, "gpu.thermalThrottle",
                "GPU is hardware throttling", "Simulated hardware thermal throttle."));
        }
        if (IsActive(HealthGpuTdr))
        {
            list.Add(Component("gpu:sim-tdr", "gpu", "Simulated GPU", HealthStatuses.Act, "gpu.tdr",
                "The graphics driver was reset (TDR)", "Simulated timeout detection and recovery event."));
        }
        if (IsActive(HealthSmartWarning))
        {
            list.Add(Component("storage:sim-smart", "storage", "Simulated SATA drive", HealthStatuses.Watch, "smart.reallocated",
                "Simulated SATA drive reports a reallocated sector", "SMART attribute 5 raw value 1 (simulated)."));
        }
        if (IsActive(HealthNvmeCritical))
        {
            list.Add(Component("storage:sim-nvme", "storage", "Simulated NVMe drive", HealthStatuses.Act, "nvme.criticalWarning",
                "Simulated NVMe drive reports a critical warning", "Critical warning flags 0x01 (simulated)."));
        }
        if (IsActive(HealthMemoryWhea))
        {
            list.Add(Component("memory:sim-whea", "memory", "Simulated memory", HealthStatuses.Act, "memory.wheaErrors",
                "Simulated memory reported hardware errors", "WHEA corrected memory errors (simulated)."));
        }
        return list;
    }

    // ── Incidents ──

    public IReadOnlyList<DiagnosticIncident> Incidents(DateTime nowUtc)
    {
        var list = new List<DiagnosticIncident>();
        if (IsActive(IncidentBsod))
        {
            list.Add(new DiagnosticIncident
            {
                Id = "sim/bsod",
                TimeUtc = nowUtc,
                Source = DiagnosticEventCatalog.SourceBugcheck,
                Severity = DiagnosticSeverity.Critical,
                Title = "The computer restarted after a bugcheck",
                Detail = "Simulated bugcheck 0x0000009F (DRIVER_POWER_STATE_FAILURE).",
            });
        }
        if (IsActive(IncidentAppCrash))
        {
            list.Add(new DiagnosticIncident
            {
                Id = "sim/app-crash",
                TimeUtc = nowUtc,
                Source = DiagnosticEventCatalog.SourceAppCrash,
                Severity = DiagnosticSeverity.Warning,
                Title = "SimulatedApp.exe stopped working",
                Detail = "Simulated application crash.",
                App = new DiagnosticAppInfo
                {
                    Name = "SimulatedApp.exe",
                    Path = "C:\\Simulated\\SimulatedApp.exe",
                    ExceptionCode = "0xc0000005",
                    FaultingModule = "simulated.dll",
                },
            });
        }
        if (IsActive(IncidentDirtyShutdown))
        {
            list.Add(new DiagnosticIncident
            {
                Id = "sim/dirty-shutdown",
                TimeUtc = nowUtc,
                Source = DiagnosticEventCatalog.SourceDirtyShutdown,
                Severity = DiagnosticSeverity.Warning,
                Title = "The system shut down unexpectedly",
                Detail = "Simulated unexpected shutdown (kernel power event).",
            });
        }
        return list;
    }

    /// <summary>The alert components for a sim that has one, built the way the real alert path evaluates them (so the notification switches apply).</summary>
    public IReadOnlyList<HealthComponent> AlertComponentsFor(string id)
    {
        var all = HealthComponents();
        var byId = id switch
        {
            HealthFanStall => "cooling:sim-fan",
            HealthPumpStall => "cooling:sim-pump",
            HealthSustainedHighTemp => "cooling",
            HealthGpuThrottle => "gpu:sim-throttle",
            HealthGpuTdr => "gpu:sim-tdr",
            HealthSmartWarning => "storage:sim-smart",
            HealthNvmeCritical => "storage:sim-nvme",
            HealthMemoryWhea => "memory:sim-whea",
            _ => null,
        };
        if (byId is not null)
        {
            return all.Where(c => c.Id == byId).ToList();
        }
        return id switch
        {
            IncidentBsod => new[] { Component("system:sim-bsod", "system", "Blue screen", HealthStatuses.Act, "system.incident", "Simulated blue screen detected", "Simulated bugcheck.") },
            IncidentAppCrash => new[] { Component("system:sim-app-crash", "system", "App crash", HealthStatuses.Watch, "system.incident", "SimulatedApp.exe stopped working", "Simulated application crash.") },
            IncidentDirtyShutdown => new[] { Component("system:sim-dirty-shutdown", "system", "Unexpected shutdown", HealthStatuses.Watch, "system.incident", "The system shut down unexpectedly", "Simulated dirty shutdown.") },
            _ => Array.Empty<HealthComponent>(),
        };
    }

    /// <summary>The thermal guard notice a guard sim raises, in the real notice's wording at the real effective limit; null when its real counterpart raises none.</summary>
    public (string Title, string Text)? GuardNoticeFor(string id)
    {
        var limit = CurrentLimitC;
        return id switch
        {
            GuardLimitTrip or GuardEscalated or GuardCoolingLossTrip =>
                (ThermalGuardNotices.TripTitle, ThermalGuardNotices.TripText(TripPeak(id, limit), TripReason(id))),
            GuardEndedTrip => (ThermalGuardNotices.TripTitle, ThermalGuardNotices.TripText(TripPeak(id, limit), ThermalTripReasons.Limit)),
            GuardWatchdogLatched => (ThermalGuardNotices.LatchedTitle, ThermalGuardNotices.LatchedText),
            GuardPendingHeal => (ThermalGuardNotices.HealedTitle, ThermalGuardNotices.HealedText(2)),
            _ => null,
        };
    }

    // ── Device and app state ──

    public UpdateStatusResponse ApplyUpdate(UpdateStatusResponse status)
    {
        if (!IsActive(AppUpdateAvailable))
        {
            return status;
        }
        return new UpdateStatusResponse
        {
            CurrentVersion = status.CurrentVersion,
            LatestVersion = "99.0.0-sim",
            UpdateAvailable = true,
            Channel = status.Channel,
            UpdateMode = status.UpdateMode,
            ReleaseNotes = "Simulated update (dev tools).",
            LastCheckedUnix = status.LastCheckedUnix,
            LastCheckError = "",
            State = "idle",
            UpdateReady = false,
            JustUpdatedTo = status.JustUpdatedTo,
            PublishedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            DownloadUrl = "",
            CanAutoInstall = false,
            CanStage = false,
        };
    }

    public void ApplyFirmware(List<FirmwareStatusItem> items)
    {
        if (!IsActive(DeviceFirmwareUpdate))
        {
            return;
        }
        items.Add(new FirmwareStatusItem
        {
            DeviceType = "sim-device",
            FirmwareType = "sim-device",
            Name = "Simulated Device",
            Category = "lighting",
            CurrentVersion = "1.0.0",
            AvailableVersion = "1.1.0",
            UpdateAvailable = true,
            AvailableVersions = new List<string> { "1.1.0" },
            DevImages = new List<FlashableImage>(),
        });
    }

    public IReadOnlyList<DetectedConflict> ApplyConflicts(IReadOnlyList<DetectedConflict> real)
    {
        if (!IsActive(AppConflict))
        {
            return real;
        }
        var app = ConflictAppCatalog.All.FirstOrDefault(a => a.Id == "nzxt-cam")
            ?? ConflictAppCatalog.All.First(a => a.Category == "lighting");
        var list = real.ToList();
        if (list.All(c => c.Id != app.Id))
        {
            list.Add(new DetectedConflict
            {
                Id = app.Id,
                DisplayName = app.DisplayName,
                Category = app.Category,
                ProcessName = app.ProcessNames.FirstOrDefault() ?? app.DisplayName,
                Pid = 0,
            });
        }
        return list;
    }

    /// <summary>Reports the first present device as disconnected in the device list. The device handle is never touched.</summary>
    public void ApplyDevices(List<DeviceListItem> devices)
    {
        if (!IsActive(DeviceDisconnect))
        {
            return;
        }
        var present = devices.FirstOrDefault(d => d.Connected);
        present?.Connected = false;
    }
}
#endif
