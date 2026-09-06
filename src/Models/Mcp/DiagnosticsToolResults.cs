using System.Collections.Generic;
using Nexus.Service.Diagnostics;
using Nexus.Service.Diagnostics.Gpu;
using Nexus.Service.Diagnostics.Memory;
using Nexus.Service.Diagnostics.Storage;

namespace Nexus.Service.Models.Mcp;

/// <summary>Payload for the get_health MCP tool.</summary>
public sealed class McpHealthResult
{
    public bool Supported { get; set; }
    /// <summary>False when the Diagnostics feature pillar is off; every other field is a default in that case.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>ok | watch | act | unknown - the worst status across every component.</summary>
    public string Overall { get; set; } = "";
    public List<HealthComponent> Components { get; set; } = new();
}

/// <summary>NVMe SMART/health log block, part of get_storage_health.</summary>
public sealed class McpNvmeHealth
{
    public int CriticalWarning { get; set; }
    public int AvailableSpare { get; set; }
    public int SpareThreshold { get; set; }
    /// <summary>Rated write endurance consumed: 0 is a new drive, values at or above 100 mean the drive has used its full rated endurance. This is wear consumed, not remaining life - see the drive's HealthPercent for the vendor's life-remaining figure.</summary>
    public int WearPercentUsed { get; set; }
    public ulong MediaErrors { get; set; }
    public ulong ErrorLogEntries { get; set; }
    public ulong UnsafeShutdowns { get; set; }
    public ulong DataReadBytes { get; set; }
    public ulong DataWrittenBytes { get; set; }
}

/// <summary>One drive's SMART health, part of get_storage_health.</summary>
public sealed class McpStorageDrive
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>nvme | sata | usb | raid | other.</summary>
    public string Bus { get; set; } = "other";
    public ulong? SizeBytes { get; set; }
    public double? TemperatureC { get; set; }
    public ulong? PowerOnHours { get; set; }
    public ulong? PowerCycles { get; set; }
    /// <summary>Vendor life-remaining percentage: higher means healthier, 100 is a new drive.</summary>
    public int? HealthPercent { get; set; }
    /// <summary>good | caution | warning | bad | unknown.</summary>
    public string Status { get; set; } = "unknown";
    public List<string> StatusReasons { get; set; } = new();
    public List<SmartAttributeWire> Attributes { get; set; } = new();
    public McpNvmeHealth? Nvme { get; set; }
}

/// <summary>Payload for the get_storage_health MCP tool.</summary>
public sealed class McpStorageHealthResult
{
    public bool Supported { get; set; }
    public List<McpStorageDrive> Drives { get; set; } = new();
}

/// <summary>One GPU's health readout, part of get_gpu_health.</summary>
public sealed class McpGpuInfo
{
    public string Name { get; set; } = "";
    public string? DriverVersion { get; set; }
    public double? TemperatureC { get; set; }
    public double? PowerW { get; set; }
    public GpuThrottleInfo Throttle { get; set; } = new(System.Array.Empty<string>(), null, null, null, null);
    /// <summary>Timeout Detection and Recovery (driver-reset) event count over a recent lookback window.</summary>
    public int RecentTdrCount { get; set; }
}

/// <summary>Payload for the get_gpu_health MCP tool.</summary>
public sealed class McpGpuHealthResult
{
    public bool Supported { get; set; }
    public List<McpGpuInfo> Gpus { get; set; } = new();
}

/// <summary>Payload for the get_system_specs MCP tool.</summary>
public sealed class McpSystemSpecsResult
{
    public string PcName { get; set; } = "";
    public string OsBuild { get; set; } = "";
    public string Processor { get; set; } = "";
    public string Motherboard { get; set; } = "";
    public string Memory { get; set; } = "";
    public string Storage { get; set; } = "";
    public string GraphicsCard { get; set; } = "";
    public string Monitor { get; set; } = "";
    public string SoundCard { get; set; } = "";
    public string NetworkCard { get; set; } = "";
    /// <summary>Unix milliseconds the OS booted, derived from Environment.TickCount64.</summary>
    public long BootTimeUnixMs { get; set; }
    public double UptimeSeconds { get; set; }
}

/// <summary>One detected conflict, part of get_conflicts.</summary>
public sealed class McpConflictItem
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>lighting | cooling | peripherals | monitoring.</summary>
    public string Category { get; set; } = "";
    public string ConflictsWith { get; set; } = "";
}

/// <summary>Payload for the get_conflicts MCP tool.</summary>
public sealed class McpConflictsResult
{
    public bool AnyConflict { get; set; }
    public List<McpConflictItem> Conflicts { get; set; } = new();
}

/// <summary>One device's firmware status, part of get_update_status.</summary>
public sealed class McpFirmwareStatus
{
    public string Device { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string AvailableVersion { get; set; } = "";
    public bool UpdateAvailable { get; set; }
}

/// <summary>Payload for the get_update_status MCP tool.</summary>
public sealed class McpUpdateStatusResult
{
    public string CurrentVersion { get; set; } = "";
    public string AvailableVersion { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public string LastCheckError { get; set; } = "";
    /// <summary>always | notify | download.</summary>
    public string UpdateMode { get; set; } = "";
    public List<McpFirmwareStatus> Devices { get; set; } = new();
}

/// <summary>Payload for the get_memory_info MCP tool.</summary>
public sealed class McpMemoryInfoResult
{
    public bool Supported { get; set; }
    public List<MemoryModuleInfo> Modules { get; set; } = new();
    public bool? XmpLikelyActive { get; set; }
    public MemoryTestResult? LastTest { get; set; }
    public bool TestScheduled { get; set; }
}
