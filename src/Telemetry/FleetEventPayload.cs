namespace Nexus.Service.Telemetry;

/// <summary>Fleet event body for nexus-api's /telemetry/events; a cross-language contract, serialized camelCase - do not rename fields without updating that side.</summary>
public sealed class FleetEventPayload
{
    public string InstallId { get; set; } = "";

    /// <summary>One of <see cref="TelemetryEvents.Install"/>, <see cref="TelemetryEvents.Specs"/>, <see cref="TelemetryEvents.OptOut"/>, <see cref="TelemetryEvents.OptIn"/>.</summary>
    public string Type { get; set; } = "";
    public string Version { get; set; } = "";
    public string Os { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string Arch { get; set; } = "";
    public string DeviceType { get; set; } = "";

    /// <summary>Present only on a <see cref="TelemetryEvents.Specs"/> event.</summary>
    public FleetEventSpecs? Specs { get; set; }
}

/// <summary>Coarse hardware summary for a "specs" fleet event; RamBytes is parsed-GB converted to bytes, an approximation, not an exact byte count.</summary>
public sealed class FleetEventSpecs
{
    public string Cpu { get; set; } = "";
    public string[] Gpu { get; set; } = System.Array.Empty<string>();
    public long RamBytes { get; set; }
    public string Motherboard { get; set; } = "";

    /// <summary>Attached USB peripherals; nexus-api stores these relationally, not inside the specs blob.</summary>
    public System.Collections.Generic.List<FleetEventDevice> Devices { get; set; } = new();

    /// <summary>A Y70 has been connected on this install at least once, per the device handler (serial link, EDID or USB); GW / Ina / Y70ti panels never appear in <see cref="Devices"/>.</summary>
    public bool Y70Seen { get; set; }

    /// <summary>SMBIOS system manufacturer is iBUYPOWER - the same match that unlocks the iBUYPOWER app.</summary>
    public bool IbuypowerSystem { get; set; }

    /// <summary>Populated DIMM count; null when the platform exposes no per-module data (Apple Silicon unified memory, unreadable SMBIOS).</summary>
    public int? RamModuleCount { get; set; }

    /// <summary>One entry per populated module, capped at <see cref="Nexus.Service.Sensors.HardwareInventoryCollector.MaxRamModules"/>; null when unavailable.</summary>
    public System.Collections.Generic.List<FleetEventRamModule>? RamModules { get; set; }

    /// <summary>One entry per physical drive, never volumes or partitions; capped at <see cref="Nexus.Service.Sensors.HardwareInventoryCollector.MaxDrives"/>; null when unavailable.</summary>
    public System.Collections.Generic.List<FleetEventDrive>? StorageDrives { get; set; }

    /// <summary>Board-level identity of each discrete GPU whose PCI subsystem could be read, capped at <see cref="FleetEventGpuCard.MaxCards"/>; <see cref="Gpu"/> keeps the bare chip names. Null when none could be read.</summary>
    public System.Collections.Generic.List<FleetEventGpuCard>? GpuCards { get; set; }
}

/// <summary>
/// One discrete GPU as its board partner built it. Chip equals the matching <see cref="FleetEventSpecs.Gpu"/> entry.
/// The ids are the PCI subsystem vendor and device as lowercase four-digit hex; Maker is this client's name for the
/// vendor id and is absent when the id is not in its table. No serial number.
/// </summary>
public sealed class FleetEventGpuCard
{
    /// <summary>Bounds the specs event; nexus-api cuts a longer list to the same size.</summary>
    internal const int MaxCards = 8;
    internal const int MaxChipLength = 128;

    public string Chip { get; set; } = "";
    public string? Maker { get; set; }
    public string? SubsystemVendorId { get; set; }
    public string? SubsystemDeviceId { get; set; }

    internal static FleetEventGpuCard From(Nexus.Service.Models.Sensors.GpuCard card) => new()
    {
        Chip = card.Chip.Length > MaxChipLength ? card.Chip[..MaxChipLength] : card.Chip,
        Maker = card.Maker,
        SubsystemVendorId = card.SubsystemVendorId.ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
        SubsystemDeviceId = card.SubsystemDeviceId.ToString("x4", System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// One populated memory module. Every field is absent rather than guessed when the platform cannot
/// supply it. No serial number: it identifies the module, not the model.
/// </summary>
public sealed class FleetEventRamModule
{
    /// <summary>Decoded JEDEC vendor name, or the SMBIOS string when it was already a name.</summary>
    public string? Manufacturer { get; set; }

    /// <summary>The raw JEDEC id (continuation byte then id byte, hex) when one was reported; lets nexus-api resolve vendors this client could not name.</summary>
    public string? ManufacturerId { get; set; }
    public string? PartNumber { get; set; }
    public long? CapacityBytes { get; set; }

    /// <summary>Speed the module runs at now, MT/s.</summary>
    public int? ConfiguredSpeedMts { get; set; }

    /// <summary>Speed the module is rated for, MT/s.</summary>
    public int? RatedSpeedMts { get; set; }

    /// <summary>DDR3, DDR4, DDR5, LPDDR4 and so on.</summary>
    public string? Generation { get; set; }
}

/// <summary>
/// One physical drive. No serial number, volume label, mount path or user-chosen name:
/// only the model string the drive firmware reports.
/// </summary>
public sealed class FleetEventDrive
{
    public string? Model { get; set; }

    /// <summary>Only when the platform reports the vendor separately from the model; transport tags such as ATA are dropped.</summary>
    public string? Vendor { get; set; }
    public long? CapacityBytes { get; set; }

    /// <summary>Lowercase transport: nvme, sata, ata, sas, scsi, raid. USB-attached and removable drives are not reported.</summary>
    public string? Bus { get; set; }

    /// <summary>ssd or hdd.</summary>
    public string? Media { get; set; }
}

/// <summary>
/// One attached USB device. The ids are what nexus-api keys on; the name is a
/// label for hardware its catalog does not recognize yet. No serial - it
/// identifies the machine rather than the model.
/// </summary>
public sealed class FleetEventDevice
{
    public int Vid { get; set; }
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
}
