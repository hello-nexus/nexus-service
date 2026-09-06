using System.Collections.Generic;

namespace Nexus.Service.Models.Panel;

/// <summary>
/// A panel deck key press. Names a slot in the panel's own stored layout; the
/// service resolves and executes the action saved there (PanelDeckRoutes).
/// </summary>
public sealed class PanelDeckDispatchBody
{
    public string DeviceId { get; set; } = "";
    public string WidgetId { get; set; } = "";
    /// <summary>0-based deck page.</summary>
    public int Page { get; set; }
    /// <summary>Folder indices from the page root down to the slot's parent; empty at the root.</summary>
    public List<int>? FolderPath { get; set; }
    /// <summary>Slot index within the resolved view.</summary>
    public int Slot { get; set; }
    /// <summary>Toggle keys only: "on" | "off", the branch the panel resolved from live state.</summary>
    public string? Branch { get; set; }
}
