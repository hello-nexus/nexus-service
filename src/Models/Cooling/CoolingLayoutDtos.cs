using Nexus.Service.Cooling;

namespace Nexus.Service.Models.Cooling;

/// <summary><c>GET /cooling/layout</c> and the 409 body of a stale write: rev 0 with an empty layout when nothing is saved.</summary>
public sealed class CoolingLayoutResponse
{
    public int Rev { get; set; }
    public CoolingLayout Layout { get; set; } = new();
}

/// <summary>200 body of a successful write.</summary>
public sealed class CoolingLayoutRevResponse
{
    public int Rev { get; set; }
}

public sealed class PutCoolingLayoutBody
{
    /// <summary>The revision the client last read; required.</summary>
    public int? BaseRev { get; set; }
    public CoolingLayout? Layout { get; set; }
}

/// <summary>Frame of the <c>cooling/layout</c> topic. Subscribers refetch <c>GET /cooling/layout</c>.</summary>
public sealed class CoolingLayoutChangedFrame
{
    /// <summary>The revision just saved; a client holding it already has the data.</summary>
    public int Rev { get; set; }
}
