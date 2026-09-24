using System.Collections.Generic;

namespace Nexus.Service.Models.Cooling;

// ----- Top-level cooling component shape (for /cooling/all) -----

public class CoolingComponent
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>One of: Q60, Q80, P60, P80, NP50, MiniHub, Motherboard, GPU, PwmFanAndArgbHub.</summary>
    public string Type { get; set; } = "MiniHub";
    public List<CoolingDevice> Devices { get; set; } = new();
}

public class CoolingDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Fan";
    public int? Speed { get; set; }
    public int? Rpm { get; set; }
    public int? TargetRpm { get; set; }
    public float? Temperature { get; set; }
    public float? PumpTempIn { get; set; }
    public float? PumpTempOut { get; set; }
    public int? Pwm { get; set; }
}

public class GetAllCoolingResponse : ApiResponse
{
    public List<CoolingComponent> CoolingComponents { get; set; } = new();
}

// ----- /cooling/curves/set -----

public class SetCurvesBody
{
    public double GlobalSpeedModifier { get; set; } = 1.0;
    public List<Curve> Curves { get; set; } = new();
}

public class Curve
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>One of: Flat, Linear, Graph, Mixed, Trigger, Sync, Auto.</summary>
    public string Type { get; set; } = "Flat";
    public CurveInput Input { get; set; } = new();
    public List<CurveOutput> Outputs { get; set; } = new();
    public FlatCurve? Flat { get; set; }
    public LinearCurve? Linear { get; set; }
    public GraphCurve? Graph { get; set; }
    public MixedCurve? Mixed { get; set; }
    public TriggerCurve? Trigger { get; set; }
    public SyncCurve? Sync { get; set; }
    public AutoCurve? Auto { get; set; }
    /// <summary>"silent" | "balanced" | "turbo" | "max" for the shared preset curves; null for user curves. Independent of Type.</summary>
    public string? Preset { get; set; }
    /// <summary>For preset curves only: true when the curve's Type + Linear params match <see cref="Nexus.Service.Cooling.FanProfiles.PresetDefaults"/>. Null for user curves. Drives the Reset-to-defaults button's enabled state in the SPA, so the FE doesn't have to mirror PresetDefaults locally.</summary>
    public bool? IsDefault { get; set; }
}

public class CurveInput
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Device { get; set; } = "";
}

public class CurveOutput
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
}

public class FlatCurve { public int Speed { get; set; } }
public class MixedCurve
{
    public double ResponseTime { get; set; } = 1.0;
    public List<string> CurveIds { get; set; } = new();
    public string Fn { get; set; } = "max";
}

/// <summary>Wire twin of <see cref="Nexus.Service.Persistence.TriggerCurveData"/>.</summary>
public class TriggerCurve
{
    public double ResponseTime { get; set; } = 1.0;
    public double IdleTemp { get; set; }
    public double LoadTemp { get; set; }
    public double IdleSpeed { get; set; }
    public double LoadSpeed { get; set; }
}

/// <summary>Wire twin of <see cref="Nexus.Service.Persistence.SyncCurveData"/>.</summary>
public class SyncCurve
{
    public string SourceChannelId { get; set; } = "";
    public double Offset { get; set; }
    public bool Proportional { get; set; }
}

/// <summary>Wire twin of <see cref="Nexus.Service.Persistence.AutoCurveData"/>.</summary>
public class AutoCurve
{
    public double ResponseTime { get; set; } = 5.0;
    public double IdleTemp { get; set; }
    public double LoadTemp { get; set; }
    public double MinSpeed { get; set; }
    public double MaxSpeed { get; set; }
    public double Step { get; set; } = 5.0;
    public double Deadband { get; set; } = 2.0;
}

public class LinearCurve
{
    public double ResponseTime { get; set; } = 1.0;
    public double MinTemp { get; set; }
    public double MaxTemp { get; set; }
    public double MinSpeed { get; set; }
    public double MaxSpeed { get; set; }
}

public class GraphCurve
{
    public double ResponseTime { get; set; } = 1.0;
    public double SpeedModifier { get; set; } = 1.0;
    public List<GraphPoint> Points { get; set; } = new();
}

public class GraphPoint
{
    public double Temp { get; set; }
    public double Speed { get; set; }
}

// ----- Fan control (IFanControlProvider) -----

/// <summary>A controllable fan channel discovered from hardware.</summary>
public sealed class FanChannel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int DutyPercent { get; set; }
    public int Rpm { get; set; }
    /// <summary>One of <see cref="FanModes.Auto"/>, <see cref="FanModes.Manual"/>, <see cref="FanModes.Curve"/>.</summary>
    public string Mode { get; set; } = FanModes.Auto;
    /// <summary>One of <see cref="FanKinds.Fan"/> / <see cref="FanKinds.Pump"/>. Drives the fan card's header icon.</summary>
    public string Kind { get; set; } = FanKinds.Fan;
    /// <summary>Telemetry-only channel: the card shows the readout but no duty bar or mode control (Q-series pump today).</summary>
    public bool ReadOnly { get; set; }
    /// <summary>Duty is controllable but RPM cannot be read (SLV3 wireless chain whose controller does not enumerate its fans; MiniHub, whose tach bytes never track the fans); the card shows a "no RPM" indicator instead of a number.</summary>
    public bool RpmUnavailable { get; set; }
    public int? MinRpm { get; set; }
    public int? MaxRpm { get; set; }
    public int? MinDuty { get; set; }
    public string? Classification { get; set; }
    public bool Calibrated => MinRpm is not null;
    /// <summary>True when the channel is exempt from Silent/Balanced/Turbo/Max/Off/Custom preset applies. An explicit user lock wins; otherwise pumps and GPU fans default locked. Computed from settings plus <see cref="IsGpu"/>.</summary>
    public bool Locked { get; set; }
    /// <summary>False when the user marked this channel not controlled: Nexus drives no duty onto it and no preset reclaims it, so the motherboard or a vendor app owns it. Computed from settings; not read from hardware.</summary>
    public bool Controlled { get; set; } = true;
    /// <summary>Duty points added to whatever drives this channel, in [-100,100]. Computed from settings; 0 when the channel has none. Surfaced so an imported offset is never invisible on the card.</summary>
    public int Offset { get; set; }
    /// <summary>True when this channel belongs to an all-in-one liquid cooler - derived from the device exposing a pump head, not from its name. Read from hardware, like <see cref="IsGpu"/>; drives the default in <see cref="Nexus.Service.Cooling.FanProfiles.IsLockedByDefault"/>.</summary>
    public bool IsAio { get; set; }
    /// <summary>True when this channel is a fan on the graphics card, decided by the hardware the provider enumerated it from (LibreHardwareMonitor GpuNvidia/GpuAmd/GpuIntel on Windows, NVML on Linux) - never by its name. Read from hardware, unlike <see cref="Role"/>; drives the default in <see cref="Nexus.Service.Cooling.FanProfiles.IsLockedByDefault"/>.</summary>
    public bool IsGpu { get; set; }
    /// <summary>Display/monitoring-grouping role: one of <see cref="FanRoleKind.None"/> / <see cref="FanRoleKind.Cpu"/> / <see cref="FanRoleKind.Gpu"/>. Computed from settings; not read from hardware. Never affects fan control, locking, or preset logic.</summary>
    public string Role { get; set; } = FanRoleKind.None;
    /// <summary>The hardware name <see cref="Name"/> replaced, set only on a renamed channel. Null means <see cref="Name"/> IS the hardware name.</summary>
    public string? OriginalName { get; set; }
    /// <summary>The hardware name <see cref="DeviceName"/> replaced, set only when the owning group header was renamed.</summary>
    public string? OriginalDeviceName { get; set; }
    /// <summary>Sanitized id matching the monitoring history series key ("fan:" + SeriesId), per <see cref="Nexus.Service.Monitoring.History.MetricsHistory.SanitizeId"/>. Computed from <see cref="Id"/>; the sanitize rule is lossy and one-way, so this is never reverse-mapped back to Id.</summary>
    public string SeriesId { get; set; } = "";

    /// <summary>
    /// Identifier of the tachometer sensor this channel reads RPM from, matching
    /// <see cref="Nexus.Service.Models.Sensors.HardwareSensor.Id"/> in the monitoring
    /// motherboard and gpu components. <see cref="Id"/> is the PWM control sensor, a
    /// different identifier, so this is the only key that joins a fan channel to the sensor
    /// list widgets and deck keys pick from. Set only by the LibreHardwareMonitor-backed
    /// Windows provider, for its motherboard and GPU fans. Null everywhere else, including
    /// Linux, whose sensor list does carry fan tachs but keys its channels differently - so
    /// a rename there stays on the Cooling page.
    /// </summary>
    public string? RpmSensorId { get; set; }

    // Owning-device metadata. Set for a channel on a USB hub like NP50 and for a
    // GPU fan; null for a motherboard header, except the DeviceName a rail-block
    // rename puts there. Drives device-grouped rendering.

    /// <summary>Stable per-device id, e.g. "np50:1A2B3C" or a GPU's "/gpu-nvidia/0". Null for motherboard.</summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// User-facing product name of the owning device, e.g. "HYTE NP50" or
    /// "NVIDIA GeForce RTX 3070". Identical for every channel on the same device - the
    /// cooling page groups by <see cref="DeviceId"/> and labels the group
    /// from any group member's <see cref="DeviceName"/>, so the lighting and
    /// cooling pages always show the same name for the same physical device.
    /// On a motherboard header this is null until the board's rail block is
    /// renamed; that block falls back to system specs, never to
    /// <see cref="OriginalDeviceName"/>.
    /// </summary>
    public string? DeviceName { get; set; }

    /// <summary>Human-readable port label, e.g. "Port 1" or "Legacy 4-pin". Null for motherboard and GPU fans.</summary>
    public string? PortLabel { get; set; }

    /// <summary>Connected fan model, e.g. "LS30" | "LS10" | "FP12". Null when unknown or motherboard.</summary>
    public string? FanModel { get; set; }

    /// <summary>"Back" | "Down" | "Up" | "Front". NP50-style orientation reported by the fan. Null otherwise.</summary>
    public string? Orientation { get; set; }
}

/// <summary>A temperature sensor available as curve input.</summary>
public sealed class TemperatureSource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>"CPU" | "GPU" | "Motherboard" | "Storage" | "Hub"</summary>
    public string Category { get; set; } = "";
    public float Value { get; set; }

    /// <summary>Stable per-device id when this sensor lives on an external device (e.g. NP50 per-fan probe). Null for motherboard/CPU/GPU.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Product name of the owning device, for grouping these into the monitoring "Cooler" category. Null when DeviceId is.</summary>
    public string? DeviceName { get; set; }
}

// ----- Device warnings (AmpScale / device-count / LED-count overloads) -----

/// <summary>
/// A single warning surfaced by a cooling device (NP50 AmpScale today,
/// other hubs later). Frontend renders these as toasts + per-device badges.
/// </summary>
public sealed class CoolingWarning
{
    /// <summary>Owning device id, e.g. "np50:1A2B3C".</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>Port label the warning applies to, or null for device-level warnings.</summary>
    public string? Port { get; set; }

    /// <summary>Machine-readable code, e.g. "current_overflow" | "led_count_exceeded" | "device_count_exceeded".</summary>
    public string Code { get; set; } = "";

    /// <summary>Human-readable message suitable for UI display.</summary>
    public string Message { get; set; } = "";

    /// <summary>"info" | "warning" | "error". Drives toast/badge styling.</summary>
    public string Severity { get; set; } = "warning";
}

public sealed class GetCoolingWarningsResponse : ApiResponse
{
    public List<CoolingWarning> Warnings { get; set; } = new();
}

public sealed class GetCurvesResponse : ApiResponse
{
    public double GlobalSpeedModifier { get; set; } = 1.0;
    public List<Curve> Curves { get; set; } = new();
}

public sealed class GetFanChannelsResponse : ApiResponse
{
    public List<FanChannel> Channels { get; set; } = new();
    /// <summary>User-made fan groups, in display order. Rides the channel list so the page needs no second fetch and the cooling topic already refreshes it.</summary>
    public List<Nexus.Service.Persistence.DeviceGroup> Groups { get; set; } = new();
}

public sealed class GetTemperatureSourcesResponse : ApiResponse
{
    public List<TemperatureSource> Sources { get; set; } = new();
}

public sealed class SetFanSpeedBody
{
    public int Speed { get; set; }
}

public sealed class SetFanSpeedResponse : ApiResponse
{
    public string ChannelId { get; set; } = "";
    public int Speed { get; set; }
    public string Mode { get; set; } = FanModes.Manual;
}

public sealed class SetFanNameBody
{
    public string Name { get; set; } = "";
}

public sealed class SetFanLockBody
{
    public bool Locked { get; set; }
}

/// <summary>Body for POST /cooling/fan/{id}/controlled. False hands the channel back to the motherboard and keeps every preset off it.</summary>
public sealed class SetFanControlledBody
{
    public bool Controlled { get; set; }
}

/// <summary>Body for POST /cooling/fan/{id}/offset. 0 clears the offset.</summary>
public sealed class SetFanOffsetBody
{
    public int Offset { get; set; }
}

public sealed class SetFanRoleBody
{
    public string Role { get; set; } = FanRoleKind.None;
}

// ----- Curve engine WebSocket push -----

public sealed class CurveOutputState
{
    public string ChannelId { get; set; } = "";
    public int AppliedSpeed { get; set; }
}

public sealed class CurveCalculation
{
    public string CurveId { get; set; } = "";
    public string InputSensorId { get; set; } = "";
    public float InputTemperature { get; set; }
    public double CalculatedSpeed { get; set; }
    public double ActualSpeed { get; set; }
    public List<CurveOutputState> Outputs { get; set; } = new();
}

public sealed class CurveCalculationsFrame
{
    public double GlobalSpeedModifier { get; set; } = 1.0;
    public List<CurveCalculation> Calculations { get; set; } = new();
}

// ----- Fan profiles -----

public sealed class FanProfile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class GetProfilesResponse : ApiResponse
{
    public List<FanProfile> Profiles { get; set; } = new();
    public string Active { get; set; } = "";
}

public sealed class ApplyProfileResponse : ApiResponse
{
    public string Applied { get; set; } = "";
}

// ----- User-saved cooling presets -----

public sealed class CoolingPresetDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Built-in mode the preset restores: off | silent | balanced | turbo | max | custom.</summary>
    public string Mode { get; set; } = "custom";
}

public sealed class CoolingPresetsResponse : ApiResponse
{
    public List<CoolingPresetDto> Presets { get; set; } = new();
    public string? ActiveId { get; set; }
}

public sealed class CreateCoolingPresetBody
{
    public string Name { get; set; } = "";
}

public sealed class CreateCoolingPresetResponse : ApiResponse
{
    public CoolingPresetDto? Preset { get; set; }
    public string? ActiveId { get; set; }
}

public sealed class UpdateCoolingPresetBody
{
    public string? Name { get; set; }
    /// <summary>Re-capture the live cooling configuration into this preset.</summary>
    public bool SaveCurrent { get; set; }
}

public sealed class SetActiveCoolingPresetBody
{
    public string? Id { get; set; }
}

public sealed class DeleteCoolingPresetResponse : ApiResponse
{
    public string? ActiveId { get; set; }
}
