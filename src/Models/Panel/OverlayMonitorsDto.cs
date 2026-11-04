using System.Collections.Generic;

namespace Nexus.Service.Models.Panel;

/// <summary>One monitor as the Windows overlay host numbers it (EnumDisplayMonitors order), with its /displays id.</summary>
public sealed class OverlayMonitorDto
{
    public int Index { get; set; }
    public string DisplayId { get; set; } = "";
    public bool Primary { get; set; }
}

/// <summary>POST /overlay/monitors body.</summary>
public sealed class OverlayMonitorsBody
{
    public List<OverlayMonitorDto> Monitors { get; set; } = new();
}
