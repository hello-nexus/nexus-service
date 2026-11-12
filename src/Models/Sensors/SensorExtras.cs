using System.Collections.Generic;

namespace Nexus.Service.Models.Sensors;

/// <summary>
/// Extra hardware components surfaced only to the Monitoring "Detailed" tab.
/// Each list is a separate hardware family; consumers render one section per
/// non-empty list. The composite "monitoring" frame intentionally does NOT
/// include this payload so other pages aren't flooded with sensor data they
/// don't display.
///
/// Lists are always present (never null) so the SPA can iterate without null
/// checks. An empty list means "platform doesn't expose this family" and the
/// section hides itself.
/// </summary>
public sealed class SensorExtras
{
    /// <summary>Battery hardware (laptop main battery, UPS). Empty on most desktops.</summary>
    public List<HardwareComponent> Batteries { get; set; } = new();

    /// <summary>Network controllers (NICs) with bytes-in/out throughput sensors.</summary>
    public List<HardwareComponent> Nics { get; set; } = new();

    /// <summary>Coolers / AIOs reported by LHM as separate hardware (pump RPM, liquid temp).</summary>
    public List<HardwareComponent> Coolers { get; set; } = new();

    /// <summary>Temperature probes built into fans (iCUE LINK QX). Empty unless such a fan is connected.</summary>
    public List<HardwareComponent> FanSensors { get; set; } = new();

    /// <summary>Power supply units with rail voltages, total wattage, efficiency.</summary>
    public List<HardwareComponent> Psus { get; set; } = new();

    /// <summary>NVMe / SATA drives surfaced as LHM hardware (controller temp, SMART data).
    /// Distinct from the drive-letter capacity rows under the "Storage" section.</summary>
    public List<HardwareComponent> NvmeStorage { get; set; } = new();

    /// <summary>Embedded controllers (laptop EC chips). Rare on desktops.</summary>
    public List<HardwareComponent> EmbeddedControllers { get; set; } = new();

    /// <summary>Per-DIMM SPD data (temperature, capacity, SDRAM timings). Empty
    /// when the platform can't reach SMBus/SPD, e.g. a VM or restricted BIOS.</summary>
    public List<HardwareComponent> MemoryModules { get; set; } = new();
}
