using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Mcp;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;

namespace Nexus.Service.Mcp.Tools;

/// <summary>
/// Shared lookups and validation for the panel MCP tools. Resolves a panel by id
/// or name, seeds the starter layout the way GET /panel/devices/{id} does, and
/// checks a layout against the widget catalog and, on a Y70, the page grid.
/// </summary>
internal static class PanelToolSupport
{
    public const string PanelArgDescription = "Panel id or display name from list_panels.";

    /// <summary>Built-in widget kinds a panel layout may place, with the sizes each declares
    /// (nexus-web src/panel/widgets/*/index.ts). "app:&lt;id&gt;" marketplace widgets are not listed.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> WidgetSizes = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["benchmark"] = ["2x2", "4x2"],
        ["blocks"] = ["2x2", "4x2"],
        ["calculator"] = ["4x4"],
        ["calendar"] = ["2x2", "2x4", "4x2", "4x4", "2x2round"],
        ["camera"] = ["2x2", "4x2", "4x4"],
        ["clock"] = ["2x2", "2x4", "4x2", "4x4", "2x2round"],
        ["cooling"] = ["2x2", "2x4", "4x2", "4x4"],
        ["deck"] = ["2x2", "4x1", "4x2", "4x4"],
        ["diagnostics"] = ["2x2", "4x2"],
        ["displays"] = ["2x2", "4x2"],
        ["emoji"] = ["4x2", "4x4"],
        ["gallery"] = ["2x2", "2x4", "4x2", "4x4", "2x2round"],
        ["home-assistant"] = ["2x2", "4x2"],
        ["lighting"] = ["2x2", "4x2", "4x4"],
        ["media"] = ["2x2", "2x4", "4x2", "4x4", "2x2round"],
        ["mixer"] = ["4x4"],
        ["monitoring"] = ["2x2", "2x4", "4x2", "4x4", "2x2round"],
        ["obs"] = ["2x2", "4x2", "4x4"],
        ["processes"] = ["4x2", "4x4"],
        ["screentime"] = ["2x2", "2x4", "4x2", "4x4"],
        ["smart-lights"] = ["2x2", "4x2"],
        ["snake"] = ["2x2", "4x2"],
        ["steam"] = ["4x4"],
        ["stocks"] = ["2x2", "2x4", "4x2", "4x4"],
        ["stopwatch"] = ["2x2", "4x2"],
        ["timer"] = ["2x2", "4x2"],
        ["transfer"] = ["2x2", "4x2"],
        ["twitch"] = ["2x4", "4x4"],
        ["weather"] = ["2x2", "2x4", "4x2", "4x4", "2x2round"],
        ["web-view"] = ["4x2", "4x4"],
        ["whiteboard"] = ["2x2", "4x2", "4x4"],
    };

    /// <summary>Widgets that also take any span with both sides at least <see cref="FreeformMinSide"/>
    /// (nexus-web freeformResize.ts), as do marketplace widgets.</summary>
    private static readonly HashSet<string> FreeformTypes = new(StringComparer.Ordinal) { "gallery", "web-view", "whiteboard" };
    private const int FreeformMinSide = 4;

    public const string MarketplacePrefix = "app:";

    public const string MonitoringSlotHelp =
        "A monitoring widget's config holds up to four gauges as slotN_device, slotN_sensor and slotN_design " +
        "(N = 1-4). Gauge designs: sparkline, line, text, waterLevel, caterpillar, squareRing, bar, microbars, " +
        "hbar, dotgrid, halfgauge, numberfill, thermo, arc270, wedge, battery, segments, mirrorwave, heatmap, " +
        "dial, tickring, backdrop, fill.";

    /// <summary>Finds a panel by exact id, then by case-insensitive display name. On a miss,
    /// <paramref name="error"/> names every known panel.</summary>
    public static PanelDeviceRecord? Resolve(PanelDeviceRegistry registry, string? requested, out string error)
    {
        error = "";
        var devices = registry.List();
        if (string.IsNullOrWhiteSpace(requested))
        {
            error = "'panel' is required: " + PanelArgDescription + " Known panels: " + Known(devices) + ".";
            return null;
        }
        var match = devices.FirstOrDefault(d => string.Equals(d.Id, requested, StringComparison.Ordinal));
        if (match is null)
        {
            var byName = devices.Where(d => string.Equals(d.DisplayName, requested, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count > 1)
            {
                error = $"More than one panel is named '{requested}'. Use its id: {Known(byName)}.";
                return null;
            }
            match = byName.FirstOrDefault();
        }
        if (match is null)
        {
            error = $"Unknown panel '{requested}'. Known panels: {Known(devices)}.";
        }
        return match;
    }

    private static string Known(IEnumerable<PanelDeviceRecord> devices)
    {
        var list = devices.Select(d => $"{d.DisplayName} ({d.Id})").ToList();
        return list.Count == 0 ? "(none)" : string.Join(", ", list);
    }

    public static string SurfaceOf(PanelDeviceRecord record) => record.Capabilities?.Surface ?? PanelSurfaces.Y70;

    /// <summary>The stored layout, or the starter GET /panel/devices/{id} serves when none is stored yet.</summary>
    public static PanelLayoutDto EffectiveLayout(PanelDeviceRecord record) =>
        record.Layout ?? PanelLayoutDefaults.WithStableIds(
            PanelLayoutDefaults.StripSeedFor(record) ?? PanelLayoutDefaults.ForSurface(SurfaceOf(record)),
            record.Id);

    /// <summary>Page grid in cells, known only for a Y70: the short axis is the record's grid slots
    /// (default 4) and the long axis is fixed at 16. Null for every other surface, whose grid the
    /// panel derives from its own viewport.</summary>
    public static (int Cols, int Rows)? GridOf(PanelDeviceRecord record)
    {
        if (!string.Equals(SurfaceOf(record), PanelSurfaces.Y70, StringComparison.Ordinal))
        {
            return null;
        }
        var shortSide = record.GridSlots ?? Y70LayoutPlacement.GridCols;
        var landscape = string.Equals(record.Capabilities?.Orientation, "landscape", StringComparison.OrdinalIgnoreCase);
        return landscape
            ? (Y70LayoutPlacement.GridRows, shortSide)
            : (shortSide, Y70LayoutPlacement.GridRows);
    }

    /// <summary>Parses "{cols}x{rows}" or "2x2round". False for anything else.</summary>
    public static bool TryParseSize(string? size, out int cols, out int rows)
    {
        cols = rows = 0;
        if (size == "2x2round")
        {
            cols = rows = 2;
            return true;
        }
        var parts = size?.Split('x');
        return parts is { Length: 2 }
            && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out cols)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out rows)
            && cols is >= 1 and <= 64 && rows is >= 1 and <= 64;
    }

    /// <summary>
    /// Checks every page of <paramref name="layout"/>: unique ids, known widget kinds, a size the
    /// kind offers, non-negative placement, no overlap, and on a Y70 the page grid bounds. A kind
    /// the stored layout already holds passes even when the catalog does not know it, so a layout
    /// written by a newer client still round-trips. Problems the stored layout already has are left
    /// out, so an edit is never refused for something it did not cause. Returns every problem found,
    /// or an empty list.
    /// </summary>
    public static List<string> Validate(PanelLayoutDto layout, PanelDeviceRecord record)
    {
        var existing = new HashSet<string>(Problems(EffectiveLayout(record), record), StringComparer.Ordinal);
        return Problems(layout, record).Where(p => !existing.Contains(p)).ToList();
    }

    private static List<string> Problems(PanelLayoutDto layout, PanelDeviceRecord record)
    {
        var problems = new List<string>();
        var grid = GridOf(record);
        var storedTypes = new HashSet<string>(
            EffectiveLayout(record).Pages.SelectMany(p => p.Widgets).Select(w => w.Type), StringComparer.Ordinal);
        if (layout.Pages.Count == 0)
        {
            problems.Add("layout needs at least one page.");
        }
        if (layout.Pages.Count > Y70LayoutPlacement.MaxPages)
        {
            problems.Add($"layout has {layout.Pages.Count} pages; the most a panel holds is {Y70LayoutPlacement.MaxPages}.");
        }
        var pageIds = new HashSet<string>(StringComparer.Ordinal);
        var widgetIds = new HashSet<string>(StringComparer.Ordinal);
        for (var p = 0; p < layout.Pages.Count; p++)
        {
            var page = layout.Pages[p];
            if (!pageIds.Add(page.Id))
            {
                problems.Add($"page {p}: id '{page.Id}' is used twice.");
            }
            var placed = new List<(string Id, int Col, int Row, int Cols, int Rows)>();
            foreach (var widget in page.Widgets)
            {
                var label = $"page {p} widget '{widget.Id}' ({widget.Type})";
                if (!widgetIds.Add(widget.Id))
                {
                    problems.Add($"{label}: id is used twice.");
                }
                var marketplace = widget.Type.StartsWith(MarketplacePrefix, StringComparison.Ordinal) && widget.Type.Length > MarketplacePrefix.Length;
                var known = WidgetSizes.TryGetValue(widget.Type, out var sizes);
                if (!known && !marketplace && !storedTypes.Contains(widget.Type))
                {
                    problems.Add($"{label}: unknown widget type. Known types: {string.Join(", ", WidgetSizes.Keys)}, or app:<id> for a marketplace widget.");
                    continue;
                }
                if (!TryParseSize(widget.Size, out var cols, out var rows))
                {
                    problems.Add($"{label}: size '{widget.Size}' is not \"<cols>x<rows>\" or \"2x2round\".");
                    continue;
                }
                var freeform = marketplace || FreeformTypes.Contains(widget.Type);
                if (known && !sizes!.Contains(widget.Size) && !(freeform && cols >= FreeformMinSide && rows >= FreeformMinSide))
                {
                    var extra = freeform ? $", or any span with both sides at least {FreeformMinSide}" : "";
                    problems.Add($"{label}: size '{widget.Size}' is not offered. Sizes: {string.Join(", ", sizes!)}{extra}.");
                    continue;
                }
                if (widget.Col < 0 || widget.Row < 0)
                {
                    problems.Add($"{label}: col and row must be 0 or more.");
                    continue;
                }
                if (grid is { } g && (widget.Col + cols > g.Cols || widget.Row + rows > g.Rows))
                {
                    problems.Add($"{label}: a {widget.Size} at col {widget.Col}, row {widget.Row} runs off the {g.Cols}x{g.Rows} grid.");
                    continue;
                }
                foreach (var other in placed)
                {
                    if (widget.Col < other.Col + other.Cols && other.Col < widget.Col + cols
                        && widget.Row < other.Row + other.Rows && other.Row < widget.Row + rows)
                    {
                        problems.Add($"{label}: overlaps widget '{other.Id}'.");
                    }
                }
                placed.Add((widget.Id, widget.Col, widget.Row, cols, rows));
            }
        }
        if (layout.ImmersiveOnLoadWidgetId is { } immersive
            && (layout.Pages.Count == 0 || !layout.Pages[0].Widgets.Any(w => w.Id == immersive)))
        {
            problems.Add($"immersiveOnLoadWidgetId '{immersive}' names no widget on the first page.");
        }
        return problems;
    }

    public static McpPanelLook LookOf(PanelDeviceRecord r) => new()
    {
        ThemeMode = r.ThemeMode,
        AccentColor = r.AccentColor,
        AccentSyncWithDesktop = r.AccentSyncWithDesktop,
        ThemeSyncWithDesktop = r.ThemeSyncWithDesktop,
        BackgroundMode = r.BackgroundMode,
        BackgroundColor = r.BackgroundColor,
        BackgroundColorLight = r.BackgroundColorLight,
        BackgroundEffect = r.BackgroundEffect,
        BackgroundTemplate = r.BackgroundTemplate,
        BackgroundOpacity = r.BackgroundOpacity,
        BackgroundFrostLevel = r.BackgroundFrostLevel,
        BackgroundMediaId = r.BackgroundMediaId,
        BackgroundMediaSlideshow = r.BackgroundMediaSlideshow,
        BackgroundMediaInterval = r.BackgroundMediaInterval,
        BackgroundMediaShuffle = r.BackgroundMediaShuffle,
        ImmersiveBackdrop = r.ImmersiveBackdrop,
        WidgetOpacity = r.WidgetOpacity,
        WidgetLabels = r.WidgetLabels,
        WidgetPadding = r.WidgetPadding,
        TextColorMode = r.TextColorMode,
        TextColor = r.TextColor,
        Font = r.Font,
        GaugeGradient = r.GaugeGradient,
    };

    public static McpPanelPresetsResult PresetsResult(string panelId, PanelPresetsResponse response) => new()
    {
        Panel = panelId,
        Presets = response.Presets.Select(p => new McpPanelPresetSummary
        {
            Id = p.Id,
            Name = p.Name,
            Active = p.Id == response.ActiveId,
        }).ToList(),
    };

    /// <summary>Finds a preset of the panel by exact id, then by case-insensitive name.</summary>
    public static PanelPresetDto? ResolvePreset(PanelPresetsResponse presets, string? requested, out string error)
    {
        error = "";
        var match = presets.Presets.FirstOrDefault(p => string.Equals(p.Id, requested, StringComparison.Ordinal))
            ?? presets.Presets.FirstOrDefault(p => string.Equals(p.Name, requested, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var known = presets.Presets.Count == 0 ? "(none)" : string.Join(", ", presets.Presets.Select(p => $"{p.Name} ({p.Id})"));
            error = string.IsNullOrWhiteSpace(requested)
                ? $"'preset' is required: a preset name or id from list_panel_presets. Known presets: {known}."
                : $"Unknown preset '{requested}'. Known presets: {known}.";
        }
        return match;
    }
}
