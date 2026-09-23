using System.Collections.Generic;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Models.Panel;

/// <summary>GET /overlay/state: everything nexus-overlay reconciles against, in one read.</summary>
public sealed class OverlayStateResponse
{
    public bool AutoLaunch { get; set; }
    public bool ReserveMonitor { get; set; }
    public string Y70Backdrop { get; set; } = "";
    public bool Y70CompatibilityRendering { get; set; }
    public bool OverlayEnabled { get; set; }
    public bool AlwaysOnTop { get; set; }
    public int Monitor { get; set; } = -1;
    public int Pinned { get; set; }
    public List<DisplayAssignmentDto> Assignments { get; set; } = new();
    public List<StreamAssignmentDto> Streams { get; set; } = new();
}
