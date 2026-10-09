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
