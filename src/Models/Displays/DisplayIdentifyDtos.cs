using System.Collections.Generic;

namespace Nexus.Service.Models.Displays;

/// <summary>One display to flash: the id /displays reports, a short label and an optional name.</summary>
public sealed class IdentifyDisplayEntry
{
    public string DisplayId { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Name { get; set; }
}

/// <summary>POST /displays/identify.</summary>
public sealed class IdentifyDisplaysBody
{
    public List<IdentifyDisplayEntry>? Displays { get; set; }
}

/// <summary>
/// An identify in progress as the hosts see it. Token changes on every request
/// so a host restarts its timer; RemainingMs lets a host that starts late or
/// polls late show only what is left.
/// </summary>
public sealed class OverlayIdentifyDto
{
    public long Token { get; set; }
    public int RemainingMs { get; set; }
    public List<IdentifyDisplayEntry> Displays { get; set; } = new();
}
