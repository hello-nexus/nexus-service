using System.Collections.Generic;

namespace Nexus.Service.Models.Sensors;

/// <summary>
/// Single sensor reading matching the ISystemSensor JSON shape.
/// `Type` is a string (LibreHardwareMonitor sensor type names: Load, Temperature,
/// Clock, Power, Voltage, Fan, etc) so the SPA can render any sensor without
/// shipping the enum.
/// </summary>
public class HardwareSensor
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "Load";
    public float Value { get; set; }
    public float Min { get; set; }
    public float Max { get; set; }
    public float Average { get; set; }
    public float Usage { get; set; }
    public float TheoreticalMaximum { get; set; }
    public string Units { get; set; } = "";
    public string Formatted { get; set; } = "";
    public string FormattedMax { get; set; } = "";
    public string FormattedMin { get; set; } = "";
    public string FormattedAverage { get; set; } = "";
    public string FormattedUsage { get; set; } = "";
    public SensorParent Parent { get; set; } = new();
    // get_sensors only (WhenWritingNull hides it elsewhere): HistoryIdMapping's id for query_sensor_history, or null.
    public string? HistoryId { get; set; }

    /// <summary>
    /// Copy for varying one property without touching the instance a provider handed out.
    /// Shallow, so <see cref="Parent"/> is shared; nothing mutates it. MemberwiseClone so a
    /// new property is never silently dropped.
    /// </summary>
    public HardwareSensor Clone() => (HardwareSensor)MemberwiseClone();
}

public class SensorParent
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class HardwareComponent
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    // GPU only (null for CPU/memory/motherboard/storage so they stay off the
    // wire under WhenWritingNull). Vendor: "nvidia"|"amd"|"intel"|"apple".
    // Integrated drives the client's default "discrete-first" GPU pick.
    public string? Vendor { get; set; }
    public bool? Integrated { get; set; }
    // GPU only: Windows adapter LUID ("HighPart:LowPart") so the client can
    // attribute per-process GPU counters to this physical GPU. Null off Windows
    // or when no DXGI adapter matched (client then shows combined totals).
    public string? AdapterLuid { get; set; }
    public List<HardwareSensor> Sensors { get; set; } = new();
}

/// <summary>
/// One physical GPU with its own sensor set, grouped and classified by the
/// platform sensor provider. The monitoring broadcaster turns each into a
/// `gpu/{i}` <see cref="HardwareComponent"/>; the client picks one as the
/// "primary" GPU (defaulting to the first discrete one).
/// </summary>
public sealed class GpuReadout
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public bool Integrated { get; set; }
    // Windows adapter LUID ("HighPart:LowPart"); "" off Windows or unmatched.
    public string AdapterLuid { get; set; } = "";
    public List<HardwareSensor> Sensors { get; set; } = new();
}

public class StorageComponent : HardwareComponent
{
    public string Format { get; set; } = "";
    public string Capacity { get; set; } = "";
    public string FreeSpace { get; set; } = "";
    public string UsedSpace { get; set; } = "";
    public string UsedPercentage { get; set; } = "";
}

public class StorageDriveInfo
{
    public string Name { get; set; } = "";
    public string Partition { get; set; } = "";
    public string Capacity { get; set; } = "";
}
