using System.Collections.Generic;
using System.Text.Json;

namespace Nexus.Service.Models.Panel;

/// <summary>
/// Layout for the panel widget engine. Kiosk layouts live on panel device
/// records; the desktop dashboard layout is profile-scoped under
/// <c>UiSettings.DashboardLayout</c>.
/// </summary>
public sealed class PanelLayoutDto
{
    /// <summary>Schema version. v2 stores explicit (Col, Row) per
    /// widget instead of a flat Position index; the web client
    /// migrates v1 records on load. Default is 1 so JSON records
    /// missing the field deserialize as legacy and get migrated -
    /// new records explicitly set 2 in PanelLayoutDefaults.</summary>
    public int LayoutSchemaVersion { get; set; } = 1;

    public string Surface { get; set; } = "y70";

    public List<PanelPageDto> Pages { get; set; } = new();

    public string? ActivePageId { get; set; }

    /// <summary>
    /// Single-widget surfaces (Q-series) show one widget at a time. Swapping
    /// which widget is shown would otherwise discard the outgoing widget's
    /// config; this remembers each widget type's last config so switching back
    /// restores it. Keyed by widget type ("clock", "media", ...). Null on
    /// multi-widget surfaces, where each widget's config lives on its own
    /// PanelWidgetDto in Pages. Values are raw JSON, same shape as
    /// PanelWidgetDto.Config.
    /// </summary>
    public Dictionary<string, Dictionary<string, JsonElement>>? SingleWidgetConfigs { get; set; }
}

public sealed class PanelPageDto
{
    public string Id { get; set; } = "";
    public string? Label { get; set; }
    public List<PanelWidgetDto> Widgets { get; set; } = new();
}

public sealed class PanelWidgetDto
{
    public string Id { get; set; } = "";

    /// <summary>Widget kind key ("clock", "performance", "media", ...). String so older
    /// services don't reject a newer widget type - unknown types are preserved verbatim.</summary>
    public string Type { get; set; } = "";

    /// <summary>"1x1" | "2x2" | "2x4" | "4x2" | "4x4".</summary>
    public string Size { get; set; } = "2x2";

    /// <summary>Top-left column on the page grid (0-indexed).
    /// Schema v2 canonical placement; gaps between widgets are
    /// allowed and never auto-filled.</summary>
    public int Col { get; set; }

    /// <summary>Top-left row on the page grid (0-indexed).</summary>
    public int Row { get; set; }

    /// <summary>Schema v1 flat position. Kept for round-tripping
    /// old layouts the web client has not migrated yet; ignored
    /// when Col/Row are populated.</summary>
    public int Position { get; set; }

    /// <summary>Schema v1 landscape flat position. Same back-compat
    /// note as Position.</summary>
    public int PositionHorizontal { get; set; }

    public bool IsImmersive { get; set; }

    /// <summary>
    /// Per-instance widget config. Values are raw JSON - widget code reads
    /// scalars directly (string / number / bool) or structured shapes (arrays,
    /// objects) declared by the widget itself. Same wire shape native panel
    /// widgets and marketplace widgets both write to.
    /// </summary>
    public Dictionary<string, JsonElement>? Config { get; set; }
}
