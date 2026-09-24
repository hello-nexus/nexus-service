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
    public string Monitor { get; set; } = "";
    public string SoundCard { get; set; } = "";
    public string NetworkCard { get; set; } = "";
    // OA3 firmware Windows product key: a bearer credential. Held on the cached
    // collector object so the gated system.specs app-action can read it, but
    // never serialized onto the shareable /system/specs response.
    [JsonIgnore]
    public string Oa3ProductKey { get; set; } = "";
}
