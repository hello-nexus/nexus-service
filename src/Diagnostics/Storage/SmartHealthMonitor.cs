using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
#if WINDOWS
using System.Diagnostics;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;
using LibreHardwareMonitor.Hardware;
#endif

namespace Nexus.Service.Diagnostics.Storage;

// Wire DTOs for GET /diagnostics/smart. Plain records, no JSON attributes -
// the integrator registers these in AppJsonContext (camelCase policy, null
// properties omitted on write).

public sealed record SmartSnapshot
{
    public bool Supported { get; init; }
    public IReadOnlyList<SmartDriveInfo> Drives { get; init; } = Array.Empty<SmartDriveInfo>();
}

public sealed record SmartDriveInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Serial { get; init; } = "";
    /// <summary>nvme | sata | usb | raid | other</summary>
    public string Bus { get; init; } = "other";
    public ulong? SizeBytes { get; init; }
    public double? TemperatureC { get; init; }
    public ulong? PowerOnHours { get; init; }
    public ulong? PowerCycles { get; init; }
    public int? HealthPercent { get; init; }
    /// <summary>good | caution | warning | bad | unknown</summary>
    public string Status { get; init; } = "unknown";
    public IReadOnlyList<string> StatusReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<SmartAttributeWire> Attributes { get; init; } = Array.Empty<SmartAttributeWire>();
    public SmartNvmeWire? Nvme { get; init; }

    /// <summary>Full classification (severity/summary/detail) behind StatusReasons's
    /// codes. Not part of the GET /diagnostics/smart wire shape - only the health
    /// aggregator (GET /diagnostics/health) reads this.</summary>
    [JsonIgnore]
    public IReadOnlyList<SmartReason> DetailedReasons { get; init; } = Array.Empty<SmartReason>();
}

public sealed record SmartAttributeWire
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public int Current { get; init; }
    public int Worst { get; init; }
    public int Threshold { get; init; }
    public ulong Raw { get; init; }
    public bool Flagged { get; init; }
}

public sealed record SmartNvmeWire
{
    public int CriticalWarning { get; init; }
    public int AvailableSpare { get; init; }
    public int SpareThreshold { get; init; }
    public int PercentageUsed { get; init; }
    public ulong MediaErrors { get; init; }
    public ulong ErrorLogEntries { get; init; }
    public ulong UnsafeShutdowns { get; init; }
    public ulong DataUnitsReadBytes { get; init; }
    public ulong DataUnitsWrittenBytes { get; init; }
}

/// <summary>What SystemMetricsSource consumes from a SMART health source -
/// narrow enough to substitute a stub in tests instead of constructing the
/// real LhmComputer-backed monitor.</summary>
public interface ISmartHealthSource
{
    SmartSnapshot Snapshot();
    void ForceRefresh();
}

#if WINDOWS
/// <summary>
/// Polls LhmComputer's storage hardware for SMART/NVMe health, lazily, at
/// most once per RefreshInterval. Never throws out of Snapshot(); a drive
/// that fails to read is skipped and warned about once (not once per poll).
/// </summary>
public sealed class SmartHealthMonitor : ISmartHealthSource
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);

    // Floor below which ForceRefresh() is a no-op, so a stuck client retry
    // loop cannot make this re-poll LhmComputer continuously.
    private static readonly TimeSpan ForceRefreshFloor = TimeSpan.FromSeconds(5);

    private readonly LhmComputer _lhm;
    private readonly object _lock = new();
    private readonly Stopwatch _sinceRefresh = Stopwatch.StartNew();
    private readonly HashSet<string> _warnedDrives = new();
    private SmartSnapshot _cached = new() { Supported = true, Drives = Array.Empty<SmartDriveInfo>() };
    private bool _hasSnapshot;

    public SmartHealthMonitor(LhmComputer lhm)
    {
        _lhm = lhm;
    }

    public SmartSnapshot Snapshot()
    {
        lock (_lock)
        {
            if (!_hasSnapshot || _sinceRefresh.Elapsed >= RefreshInterval)
                Refresh(explicitRefresh: false);
            return _cached;
        }
    }

    public void ForceRefresh()
    {
        lock (_lock)
        {
            if (_hasSnapshot && _sinceRefresh.Elapsed < ForceRefreshFloor)
            {
                return;
            }
            Refresh(explicitRefresh: true);
        }
    }

    // Caller holds _lock. Mirrors LhmComputer.Update's early-out: while the
    // background Computer.Open() is still running, Hardware is empty, so
    // returning here (instead of falling through to the finally) keeps
    // _hasSnapshot false and leaves the refresh clock unstarted - the next
    // Snapshot() call retries immediately rather than pinning an empty
    // Supported=true list for the full 10-minute cache window.
    private void Refresh(bool explicitRefresh)
    {
        if (!_lhm.OpenTask.IsCompletedSuccessfully)
        {
            return;
        }

        try
        {
            // Only an explicit refresh opens the reader window; the staleness path reads just the drives something needs.
            if (explicitRefresh) _lhm.WalkForSmartRefresh();
            else _lhm.Update(SensorRefresh.Force);
            var drives = new List<SmartDriveInfo>();
            foreach (var hw in _lhm.Instance.Hardware)
            {
                if (hw.HardwareType != HardwareType.Storage) continue;
                if (hw is not LibreHardwareMonitor.Hardware.Storage.StorageDevice lhmDrive) continue;

                var hardwareId = hw.Identifier.ToString() ?? "";
                try
                {
                    var dit = lhmDrive.Storage;
                    if (dit is null) continue;
                    drives.Add(BuildDrive(dit, hardwareId));
                }
                catch (Exception ex)
                {
                    if (_warnedDrives.Add(hardwareId))
                        ServiceLog.Warn($"[smart] failed reading drive {hardwareId}: {ex.Message}");
                }
            }
            _cached = new SmartSnapshot { Supported = true, Drives = drives };
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[smart] refresh failed: {ex.Message}");
            // Keep the previous cached snapshot rather than clobbering it with empty data.
        }
        finally
        {
            _sinceRefresh.Restart();
            _hasSnapshot = true;
        }
    }

    private static SmartDriveInfo BuildDrive(DiskInfoToolkit.Devices.StorageDevice dit, string hardwareId)
    {
        var serial = dit.SerialNumber ?? "";
        var id = !string.IsNullOrWhiteSpace(serial) ? $"storage:{serial}" : $"storage:{hardwareId}";
        var name = !string.IsNullOrWhiteSpace(dit.DisplayName) ? dit.DisplayName : dit.ProductName ?? "";
        var bus = MapBus(dit.BusType);

        if (!dit.SupportsSmart)
        {
            return new SmartDriveInfo
            {
                Id = id,
                Name = name,
                Serial = serial,
                Bus = bus,
                SizeBytes = dit.DiskSizeBytes,
                Status = "unknown",
            };
        }

        var attributeSamples = (dit.SmartAttributes ?? new List<DiskInfoToolkit.Smart.SmartAttributeEntry>())
            .Select(a => new SmartAttributeSample(a.ID, a.Name ?? "", a.CurrentValue, a.WorstValue, a.ThresholdValue, a.RawValue))
            .ToList();

        var busKind = MapBusKind(dit.BusType);
        var nvmeLog = busKind == StorageBusKind.Nvme ? NvmeHealthLog.Parse(dit.Nvme?.SmartLogData) : null;
        var vendorHealth = MapVendorHealth(dit.HealthStatus);

        var classification = SmartClassifier.Classify(new SmartClassifierInput(
            busKind, attributeSamples, nvmeLog, vendorHealth, dit.PredictsFailure));

        var flaggedIds = classification.FlaggedAttributeIds;
        var attributesWire = attributeSamples.Select(a => new SmartAttributeWire
        {
            Id = a.Id,
            Name = a.Name,
            Current = a.Current,
            Worst = a.Worst,
            Threshold = a.Threshold,
            Raw = a.Raw,
            Flagged = flaggedIds.Contains(a.Id),
        }).ToList();

        SmartNvmeWire? nvmeWire = nvmeLog is null
            ? null
            : new SmartNvmeWire
            {
                CriticalWarning = nvmeLog.CriticalWarning,
                AvailableSpare = nvmeLog.AvailableSpare,
                SpareThreshold = nvmeLog.AvailableSpareThreshold,
                PercentageUsed = nvmeLog.PercentageUsed,
                MediaErrors = nvmeLog.MediaErrors,
                ErrorLogEntries = nvmeLog.ErrorInfoLogEntries,
                UnsafeShutdowns = nvmeLog.UnsafeShutdowns,
                DataUnitsReadBytes = nvmeLog.DataUnitsReadBytes,
                DataUnitsWrittenBytes = nvmeLog.DataUnitsWrittenBytes,
            };

        return new SmartDriveInfo
        {
            Id = id,
            Name = name,
            Serial = serial,
            Bus = bus,
            SizeBytes = dit.DiskSizeBytes,
            TemperatureC = dit.Temperature,
            PowerOnHours = dit.PowerOnHours,
            PowerCycles = dit.PowerOnCount,
            HealthPercent = dit.Health,
            Status = MapStatus(classification.Status),
            StatusReasons = classification.Reasons.Select(r => r.Code).Distinct().ToList(),
            Attributes = attributesWire,
            Nvme = nvmeWire,
            DetailedReasons = classification.Reasons,
        };
    }

    private static string MapBus(DiskInfoToolkit.Devices.StorageBusType bus) => bus switch
    {
        DiskInfoToolkit.Devices.StorageBusType.Nvme => "nvme",
        DiskInfoToolkit.Devices.StorageBusType.Sata or DiskInfoToolkit.Devices.StorageBusType.Ata => "sata",
        DiskInfoToolkit.Devices.StorageBusType.Usb => "usb",
        DiskInfoToolkit.Devices.StorageBusType.RAID => "raid",
        _ => "other",
    };

    private static StorageBusKind MapBusKind(DiskInfoToolkit.Devices.StorageBusType bus) => bus switch
    {
        DiskInfoToolkit.Devices.StorageBusType.Nvme => StorageBusKind.Nvme,
        DiskInfoToolkit.Devices.StorageBusType.Sata or DiskInfoToolkit.Devices.StorageBusType.Ata => StorageBusKind.Sata,
        DiskInfoToolkit.Devices.StorageBusType.Usb => StorageBusKind.Usb,
        DiskInfoToolkit.Devices.StorageBusType.RAID => StorageBusKind.Raid,
        _ => StorageBusKind.Other,
    };

    private static VendorHealthStatus? MapVendorHealth(DiskInfoToolkit.Smart.StorageHealthStatus? status) => status switch
    {
        DiskInfoToolkit.Smart.StorageHealthStatus.Good => VendorHealthStatus.Good,
        DiskInfoToolkit.Smart.StorageHealthStatus.Caution => VendorHealthStatus.Caution,
        DiskInfoToolkit.Smart.StorageHealthStatus.Warning => VendorHealthStatus.Warning,
        DiskInfoToolkit.Smart.StorageHealthStatus.Bad => VendorHealthStatus.Bad,
        _ => null,
    };

    private static string MapStatus(DriveStatus status) => status switch
    {
        DriveStatus.Good => "good",
        DriveStatus.Caution => "caution",
        DriveStatus.Warning => "warning",
        DriveStatus.Bad => "bad",
        _ => "unknown",
    };
}
#else
/// <summary>Non-Windows: delegates to <see cref="LinuxSmartHealthMonitor"/>
/// (smartctl-backed) at runtime on Linux; every other platform reports
/// unsupported, since there is no cross-platform SMART reader.</summary>
public sealed class SmartHealthMonitor : ISmartHealthSource
{
    private readonly LinuxSmartHealthMonitor? _linux = OperatingSystem.IsLinux() ? new LinuxSmartHealthMonitor() : null;

    public SmartSnapshot Snapshot() =>
        _linux?.Snapshot() ?? new SmartSnapshot { Supported = false, Drives = Array.Empty<SmartDriveInfo>() };

    public void ForceRefresh() => _linux?.ForceRefresh();
}
#endif
