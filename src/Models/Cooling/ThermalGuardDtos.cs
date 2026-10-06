using System.Collections.Generic;

namespace Nexus.Service.Models.Cooling;

public sealed class ThermalGuardTripDto
{
    public long AtUtcMs { get; set; }
    public double PeakC { get; set; }
    /// <summary>"limit" | "cooling-loss".</summary>
    public string Reason { get; set; } = "";
    public bool Escalated { get; set; }
}

public sealed class HealChannelDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Hazard { get; set; } = "";
}

public sealed class HealStateDto
{
    public bool UndoAvailable { get; set; }
    public long? HealedAtUtcMs { get; set; }
    public List<HealChannelDto> Channels { get; set; } = new();
}

public sealed class GpuGuardDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double? TempC { get; set; }
    public double? LimitC { get; set; }
    public string? LimitSource { get; set; }
    /// <summary>"inactive" | "normal" | "handedBack" | "forced".</summary>
    public string State { get; set; } = "inactive";
}

/// <summary>GET /cooling/guard and POST /cooling/guard/config.</summary>
public sealed class ThermalGuardResponse : ApiResponse
{
    /// <summary>"off" | "inactive" | "normal" | "floor" | "tripped" | "escalated".</summary>
    public string State { get; set; } = "inactive";
    public double? GuardTempC { get; set; }
    /// <summary>The effective CPU limit.</summary>
    public double? LimitC { get; set; }
    /// <summary>"hardware" | "spec" | "default" | "user" (the override is in effect), null when there is no limit.</summary>
    public string? LimitSource { get; set; }
    /// <summary>What the service detected, whatever the override.</summary>
    public double? DetectedLimitC { get; set; }
    /// <summary>"hardware" | "spec" | "default".</summary>
    public string? DetectedLimitSource { get; set; }
    /// <summary>The user's limit in C, null = automatic.</summary>
    public double? LimitOverrideC { get; set; }
    public long? SinceUtcMs { get; set; }
    /// <summary>True after repeated cooling-engine stalls: Nexus writes no fan until restart or a guard toggle.</summary>
    public bool WatchdogLatched { get; set; }
    public ThermalGuardTripDto? LastTrip { get; set; }
    public HealStateDto Heal { get; set; } = new();
    public List<GpuGuardDto> Gpus { get; set; } = new();
}

/// <summary>Partial update: only the fields present change.</summary>
public sealed class SetThermalGuardConfigBody
{
    public bool? Enabled { get; set; }
    public double? LimitOverrideC { get; set; }
    public bool? ClearLimitOverride { get; set; }
}

public sealed class LintHazardDto
{
    public string ChannelId { get; set; } = "";
    public string ChannelName { get; set; } = "";
    public string Kind { get; set; } = "";
    public string RootId { get; set; } = "";
    public string RootName { get; set; } = "";
}

public sealed class LintCurvesResponse : ApiResponse
{
    public List<LintHazardDto> Hazards { get; set; } = new();
    public bool FixAvailable { get; set; }
}
