using System.Collections.Generic;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Sensors;

namespace Nexus.Service.Models.Mcp;

/// <summary>Payload for the get_system_overview MCP tool.</summary>
public sealed class McpSystemOverviewResult
{
    public string CpuModel { get; set; } = "";
    public List<string> GpuModels { get; set; } = new();
    public string MemoryTotal { get; set; } = "";
    /// <summary>CPU/GPU/memory temp, usage, and clock in one fixed-order list; see SummarySensors.Build.</summary>
    public List<HardwareSensor> Summary { get; set; } = new();
    public List<FanChannel> FanChannels { get; set; } = new();
    public string ActiveCoolingPreset { get; set; } = "";
}

/// <summary>Payload for the get_sensors MCP tool.</summary>
public sealed class McpSensorsResult
{
    /// <summary>Echoes the requested device: cpu, gpu, memory, motherboard, or storage.</summary>
    public string Device { get; set; } = "";
    public List<HardwareSensor> Sensors { get; set; } = new();
}

/// <summary>Payload for the get_cooling_state MCP tool.</summary>
public sealed class McpCoolingStateResult
{
    public List<FanChannel> FanChannels { get; set; } = new();
    public List<TemperatureSource> TemperatureSources { get; set; } = new();
    public List<Curve> Curves { get; set; } = new();
    public string ActivePreset { get; set; } = "";
    public double GlobalSpeedModifier { get; set; }
}

/// <summary>Payload for the get_lighting_state MCP tool.</summary>
public sealed class McpLightingStateResult
{
    /// <summary>Active sync mode, or the running effect's key when one is live.</summary>
    public string Sync { get; set; } = "";
    public string CurrentEffect { get; set; } = "";
    /// <summary>0..1 master brightness cap.</summary>
    public float GlobalBrightness { get; set; }
    /// <summary>Last static color as "#rrggbb".</summary>
    public string StaticColor { get; set; } = "";
}

/// <summary>Payload for the apply_cooling_preset MCP tool.</summary>
public sealed class McpApplyCoolingPresetResult
{
    /// <summary>Canonical preset name actually applied.</summary>
    public string Applied { get; set; } = "";
}

/// <summary>Payload for the set_global_fan_speed MCP tool.</summary>
public sealed class McpSetGlobalFanSpeedResult
{
    public int Percent { get; set; }
}

/// <summary>Payload for the set_fan_curve MCP tool.</summary>
public sealed class McpSetFanCurveResult
{
    public string CurveId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Outputs { get; set; } = new();
}

/// <summary>Payload for the apply_lighting_scenario MCP tool.</summary>
public sealed class McpApplyLightingScenarioResult
{
    public string Scenario { get; set; } = "";
}

/// <summary>Payload for the set_static_color MCP tool.</summary>
public sealed class McpSetStaticColorResult
{
    /// <summary>Echoes the requested color as "#rrggbb".</summary>
    public string Color { get; set; } = "";

    /// <summary>Friendly name of the built-in preset the color snapped to (e.g. "red").</summary>
    public string Preset { get; set; } = "";
}

/// <summary>Payload for the set_brightness MCP tool.</summary>
public sealed class McpSetBrightnessResult
{
    public int Percent { get; set; }
}

/// <summary>Payload for the stop_lighting MCP tool.</summary>
public sealed class McpStopLightingResult
{
    public bool Stopped { get; set; }
}

/// <summary>Payload for the list_profiles MCP tool.</summary>
public sealed class McpProfileListResult
{
    public List<McpProfileSummary> Profiles { get; set; } = new();
}

public sealed class McpProfileSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Active { get; set; }
}

/// <summary>Payload for the apply_profile MCP tool.</summary>
public sealed class McpApplyProfileResult
{
    public string Switched { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>One point on a query_sensor_history series.</summary>
public sealed class McpHistoryPoint
{
    /// <summary>Unix milliseconds.</summary>
    public long T { get; set; }
    public double Value { get; set; }
}

/// <summary>Payload for the query_sensor_history MCP tool.</summary>
public sealed class McpQuerySensorHistoryResult
{
    public string SensorId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    /// <summary>"raw" | "1m" | "5m" - which tier the points were sourced from.</summary>
    public string Tier { get; set; } = "";
    public List<McpHistoryPoint> Points { get; set; } = new();
}

/// <summary>Min/max/avg/latest for one recorded sensor, part of get_history_summary.</summary>
public sealed class McpSensorSummary
{
    public string SensorId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public double Min { get; set; }
    public double Max { get; set; }
    public double Avg { get; set; }
    public double Latest { get; set; }
    /// <summary>Unix milliseconds of the Latest reading.</summary>
    public long LatestAtUtc { get; set; }
    public int Samples { get; set; }
}

/// <summary>Payload for the get_history_summary MCP tool.</summary>
public sealed class McpHistorySummaryResult
{
    public int Minutes { get; set; }
    public List<McpSensorSummary> Sensors { get; set; } = new();
}

/// <summary>One row from query_events.</summary>
public sealed class McpHistoryEvent
{
    /// <summary>Unix milliseconds.</summary>
    public long TUtc { get; set; }
    /// <summary>"ai_write" | "lifecycle".</summary>
    public string Type { get; set; } = "";
    /// <summary>Tool name for an ai_write event, event name for a lifecycle event.</summary>
    public string Name { get; set; } = "";
    public string ArgsJson { get; set; } = "";
    public bool Success { get; set; }
    public string? ErrorText { get; set; }
}

/// <summary>Payload for the query_events MCP tool.</summary>
public sealed class McpQueryEventsResult
{
    public List<McpHistoryEvent> Events { get; set; } = new();
    public bool Truncated { get; set; }
}

/// <summary>One app's window-average/max for get_top_apps.</summary>
public sealed class McpTopApp
{
    public string Name { get; set; } = "";
    public double Avg { get; set; }
    public double Max { get; set; }
}

/// <summary>Payload for the get_top_apps MCP tool.</summary>
public sealed class McpTopAppsResult
{
    public string Metric { get; set; } = "";
    public int Minutes { get; set; }
    public List<McpTopApp> Apps { get; set; } = new();
}

/// <summary>One point on a query_app_history series.</summary>
public sealed class McpAppHistoryPoint
{
    /// <summary>Unix milliseconds.</summary>
    public long T { get; set; }
    public double Value { get; set; }
}

/// <summary>Payload for the query_app_history MCP tool.</summary>
public sealed class McpAppHistoryResult
{
    public string Metric { get; set; } = "";
    public string App { get; set; } = "";
    /// <summary>Window-average dedicated VRAM in MB; only populated for a gpu/vram metric.</summary>
    public double? VramAvgMb { get; set; }
    public List<McpAppHistoryPoint> Points { get; set; } = new();
}

// History and action tools

/// <summary>One temperature bucket for get_temperature_history, optionally
/// annotated with the slot's dominant foreground app.</summary>
public sealed class McpTemperaturePoint
{
    /// <summary>Unix milliseconds, the bucket's start.</summary>
    public long T { get; set; }
    public double Avg { get; set; }
    public double Max { get; set; }
    /// <summary>Most-used foreground app in this slot; null with no recorded activity.</summary>
    public string? DominantApp { get; set; }
}

/// <summary>One component's temperature series for get_temperature_history.</summary>
public sealed class McpTemperatureSeries
{
    /// <summary>cpu, gpu:&lt;id&gt;, storage:&lt;serial&gt;, or ram:&lt;id&gt;.</summary>
    public string Id { get; set; } = "";
    /// <summary>cpu | gpu | storage | ram.</summary>
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public List<McpTemperaturePoint> Points { get; set; } = new();
}

/// <summary>Payload for the get_temperature_history MCP tool.</summary>
public sealed class McpTemperatureHistoryResult
{
    public int BucketMinutes { get; set; }
    public List<McpTemperatureSeries> Series { get; set; } = new();
}

/// <summary>One game's fps rollup across every stored session, part of get_game_sessions.</summary>
public sealed class McpGameSummary
{
    public string GameKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string Store { get; set; } = "";
    public int Sessions { get; set; }
    public long FocusedSec { get; set; }
    public long Frames { get; set; }
    public int MinFps { get; set; }
    public int MaxFps { get; set; }
    public double AvgFps { get; set; }
    /// <summary>Unix milliseconds of the most recent session.</summary>
    public long LastPlayedUtcMs { get; set; }
}

/// <summary>One recorded session for one game, part of get_game_sessions.</summary>
public sealed class McpGameSessionEntry
{
    public long StartedUtcMs { get; set; }
    public long EndedUtcMs { get; set; }
    public double AvgFps { get; set; }
    public int MinFps { get; set; }
    public int MaxFps { get; set; }
    public int DispW { get; set; }
    public int DispH { get; set; }
    public int RefreshHz { get; set; }
    public bool Fullscreen { get; set; }
    public bool Capped { get; set; }
    public int CapValue { get; set; }
}

/// <summary>Payload for the get_game_sessions MCP tool. Games is populated when
/// called without 'game'; Game/Sessions are populated when 'game' is given.</summary>
public sealed class McpGameSessionsResult
{
    /// <summary>False off Windows, where no fps recorder is registered.</summary>
    public bool Supported { get; set; } = true;
    public bool TrackingEnabled { get; set; } = true;
    public List<McpGameSummary>? Games { get; set; }
    public string? Game { get; set; }
    public List<McpGameSessionEntry>? Sessions { get; set; }
}

/// <summary>Payload for the get_screen_time MCP tool. Exactly one of Day,
/// Range, App, Hour is populated, matching the requested mode.</summary>
public sealed class McpScreenTimeResult
{
    public bool TrackingEnabled { get; set; } = true;
    /// <summary>day | range | app | hour.</summary>
    public string Mode { get; set; } = "";
    public DayBreakdown? Day { get; set; }
    public List<DayTotal>? Range { get; set; }
    public AppHistory? App { get; set; }
    public List<AppUsage>? Hour { get; set; }
}

/// <summary>Payload for the add_monitoring_event MCP tool.</summary>
public sealed class McpAddMonitoringEventResult
{
    public long Id { get; set; }
    /// <summary>Unix milliseconds.</summary>
    public long T { get; set; }
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
}

/// <summary>Payload for the calibrate_fans MCP tool.</summary>
public sealed class McpCalibrateFansResult
{
    public bool Started { get; set; }
    /// <summary>Echoes the requested fan channel id; null when every fan was calibrated.</summary>
    public string? Fan { get; set; }
    public string Message { get; set; } = "";
}
