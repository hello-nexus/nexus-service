using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Nexus.Service.Models.Sensors;

public class GetPollingRateResponse
{
    public int PollingRate { get; set; }
}

public class GetModelResponse
{
    public string Model { get; set; } = "";
}

public class GetGpuModelsResponse : ApiResponse
{
    public List<string> Models { get; set; } = new();
}

public class CpuHealthResponse : ApiResponse
{
    public bool Healthy { get; set; } = true;
    public float DistanceToTJMax { get; set; }
}

public class GetStoragePartitionsResponse : ApiResponse
{
    public List<string> Partitions { get; set; } = new();
}

public class GetDriveStorageResponse : ApiResponse
{
    public List<StorageDriveInfo> Storage { get; set; } = new();
}

/// <summary>
/// Compact, one-line-per-field snapshot of the PC's hardware/software identity
/// for the Devices → System Specs tab. Each property is a fully-formed display
/// string (no further formatting needed on the client); empty string means
/// the platform couldn't resolve the value. Designed to be copy-pasted as a
/// shareable rig summary.
/// </summary>
public class SystemSpecsResponse
{
    public string PcName { get; set; } = "";
    public string OsBuild { get; set; } = "";
    public string Processor { get; set; } = "";
    public string Motherboard { get; set; } = "";
    public string Memory { get; set; } = "";
    public string Storage { get; set; } = "";
    public string GraphicsCard { get; set; } = "";
    // The card that renders games: the first discrete adapter, else the first
    // adapter. GraphicsCard lists every adapter " + "-joined, iGPU included,
    // and on AMD-iGPU-first rigs the iGPU comes first.
    public string PrimaryGpu { get; set; } = "";
    // Per-adapter chip and board maker behind GraphicsCard; in-process only (the fleet event and cloud spec map read it).
    [JsonIgnore]
    public List<GpuCard> GpuCards { get; set; } = new();
    public string Monitor { get; set; } = "";
    public string SoundCard { get; set; } = "";
    public string NetworkCard { get; set; } = "";
    // OA3 firmware Windows product key: a bearer credential. Held on the cached
    // collector object so the gated system.specs app-action can read it, but
    // never serialized onto the shareable /system/specs response.
    [JsonIgnore]
    public string Oa3ProductKey { get; set; } = "";
    // Model identifiers for the catalog match. Never a serial number, MAC address, key or host name.
    public HardwareIdsDto HardwareIds { get; set; } = new();
}

/// <summary>
/// Hardware model identifiers for matching the PC against the catalog. A list or block with no source on the
/// platform is null; an individual value that could not be read is null. Serial numbers, MAC addresses, product keys and
/// host names are never part of this block.
/// </summary>
public class HardwareIdsDto
{
    /// <summary>One entry per graphics card, in the order GraphicsCard lists them.</summary>
    public List<GpuIdsDto>? Gpus { get; set; }
    public List<RamModuleIdsDto>? RamModules { get; set; }
    public List<DriveIdsDto>? Drives { get; set; }
    public BoardIdsDto? Board { get; set; }
    public List<MonitorIdsDto>? Monitors { get; set; }
}

/// <summary>PCI ids as four lowercase hex digits. Vendor and Device name the chip; SubVendor and SubDevice name the board partner's design.</summary>
public class GpuIdsDto
{
    public string? Vendor { get; set; }
    public string? Device { get; set; }
    public string? SubVendor { get; set; }
    public string? SubDevice { get; set; }
}

public class RamModuleIdsDto
{
    public string? Manufacturer { get; set; }
    public string? PartNumber { get; set; }
    public long? CapacityBytes { get; set; }
    public int? ConfiguredSpeedMts { get; set; }
}

public class DriveIdsDto
{
    public string? Vendor { get; set; }
    public string? Model { get; set; }
}

/// <summary>SMBIOS baseboard (Type 2) manufacturer and product name.</summary>
public class BoardIdsDto
{
    public string? Manufacturer { get; set; }
    public string? Product { get; set; }
}

/// <summary>EDID PnP manufacturer id, product code as four uppercase hex digits, and monitor name.</summary>
public class MonitorIdsDto
{
    public string? Manufacturer { get; set; }
    public string? ProductCode { get; set; }
    public string? Name { get; set; }
}
