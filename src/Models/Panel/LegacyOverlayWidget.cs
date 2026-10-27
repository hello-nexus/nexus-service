using System.Collections.Generic;

namespace Nexus.Service.Models.Panel;

/// <summary>
/// Read model of a widget in the retired floating desktop overlay, kept only so
/// <see cref="Nexus.Service.Panel.DesktopWidgetsMigration"/> can read old settings and hand the
/// widgets to a panel record. Cells are on a sparse grid per monitor.
/// </summary>
public sealed class LegacyOverlayWidget
{
    public string Id { get; set; } = "";

    /// <summary>Widget kind key ("clock", "performance", ...). Mirrors PanelWidgetDto.Type.</summary>
    public string Type { get; set; } = "";

    /// <summary>"1x1" | "2x2" | "2x4" | "4x1" | "4x2" | "4x4". Mirrors PanelWidgetDto.Size.</summary>
    public string Size { get; set; } = "2x2";

    /// <summary>0-based monitor index in EnumDisplayMonitors order.</summary>
    public int Monitor { get; set; }

    /// <summary>Top-left cell column on the target monitor. Fractional values
    /// allowed so users can position at sub-cell precision (the drag-aligned grid is finer than the
    /// widget sizing grid).</summary>
    public double Col { get; set; }

    /// <summary>Top-left cell row on the target monitor. Same fractional rules
    /// as <see cref="Col"/>.</summary>
    public double Row { get; set; }

    /// <summary>When true the SPA blocks drag/edit/resize of this widget until
    /// the user unlocks it. Inner widget controls stay live.</summary>
    public bool Locked { get; set; }

    public Dictionary<string, System.Text.Json.JsonElement>? Config { get; set; }
}
