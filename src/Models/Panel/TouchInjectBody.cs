namespace Nexus.Service.Models.Panel;

/// <summary>One touch pointer event for the user-session helper to inject onto a monitor.</summary>
public sealed class TouchInjectBody
{
    /// <summary>Display adapter name (EnumDisplayDevices DeviceString) of the target monitor.</summary>
    public string Adapter { get; set; } = "";
    public uint PointerId { get; set; }
    /// <summary>"down", "move" or "up".</summary>
    public string Phase { get; set; } = "";
    /// <summary>Monitor-relative pixels.</summary>
    public int X { get; set; }
    public int Y { get; set; }
}
