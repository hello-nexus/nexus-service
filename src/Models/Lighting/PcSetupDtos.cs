using System.Collections.Generic;
using Nexus.Service.Lighting.Scene;

namespace Nexus.Service.Models.Lighting;

/// <summary><c>GET /system/setup</c> and the 409 body of a stale write: rev 0 with an empty setup when nothing is saved.</summary>
public sealed class PcSetupResponse
{
    public int Rev { get; set; }
    public PcSetup Setup { get; set; } = new();
}

/// <summary>200 body of a successful write.</summary>
public sealed class PcSetupRevResponse
{
    public int Rev { get; set; }
}

public sealed class PutPcSetupBody
{
    /// <summary>The revision the client last read; required.</summary>
    public int? BaseRev { get; set; }
    public PcSetup? Setup { get; set; }
}

/// <summary>Lighting's write: moves and turns existing items, nothing else. The service updates the matching resolved objects' position and yaw itself; a <c>resolved</c> field in the body is ignored.</summary>
public sealed class PatchPcSetupPlacementsBody
{
    public int? BaseRev { get; set; }
    public List<PcSetupPlacement>? Places { get; set; }
}

/// <summary>An item id, or <c>case</c>, and its new footprint center and yaw (units and direction in <see cref="PcSetup"/>).</summary>
public sealed class PcSetupPlacement
{
    public string Id { get; set; } = "";
    public float X { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
}

/// <summary>400 body: <see cref="ApiResponse"/> plus a stable code.</summary>
public sealed class PcSetupErrorResponse : ApiResponse
{
    public string Code { get; set; } = "";
}

/// <summary>Frame of the <c>system/setup</c> topic. Subscribers refetch <c>GET /system/setup</c>.</summary>
public sealed class PcSetupChangedFrame
{
    /// <summary>The record revision just saved; a client holding it already has the data.</summary>
    public int Rev { get; set; }
}
