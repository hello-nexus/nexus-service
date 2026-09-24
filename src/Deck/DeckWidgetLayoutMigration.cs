using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Deck;

/// <summary>
/// Part of the schema v18 migration (see <see cref="DeckModesMigration"/>):
/// lifts every deck widget's inline <c>config.deck</c> - on the desktop
/// dashboard layout and every registered panel device's layout - into a host-
/// wide preset plus a "widget:&lt;id&gt;" instance row, then strips
/// <c>config.deck</c> from the widget so its instance id is the only thing the
/// layout still carries.
/// </summary>
internal static class DeckWidgetLayoutMigration
{
    private const string DeckConfigKey = "deck";

    public static void Apply(NexusSettings doc)
    {
        MigrateLayout(doc, doc.Panel.DashboardLayout, "Desktop");
        foreach (var record in doc.PanelDevices.Values)
        {
            MigrateLayout(doc, record.Layout, string.IsNullOrEmpty(record.DisplayName) ? "Panel" : record.DisplayName);
        }
    }

    private static void MigrateLayout(NexusSettings doc, PanelLayoutDto? layout, string panelName)
    {
        if (layout is null)
        {
            return;
        }
        foreach (var page in layout.Pages)
        {
            foreach (var widget in page.Widgets)
            {
                if (!string.Equals(widget.Type, DeckLayoutPolicy.DeckWidgetType, StringComparison.Ordinal))
                {
                    continue;
                }
                var config = DeckLayoutPolicy.ReadDeckConfig(widget);
                if (config is null || !DeckConfigNavigation.HasContent(config))
                {
                    widget.Config?.Remove(DeckConfigKey);
                    if (widget.Config is { Count: 0 })
                    {
                        widget.Config = null;
                    }
                    continue;
                }

                var grid = DeckConfigNavigation.InnerGridForSize(widget.Size);
                var preset = new DeckPreset
                {
                    Id = DeckModesMigration.NewPresetId(),
                    Name = UniqueSuffixedName(doc.StreamDeck.Presets, $"{panelName} deck"),
                    Cols = grid.Cols,
                    Rows = grid.Rows,
                    Deck = config,
                };
                doc.StreamDeck.Presets.Add(preset);
                doc.StreamDeck.Instances[DeckInstanceResolver.WidgetInstanceId(widget.Id)] =
                    new DeckInstance { Mode = "custom", ActivePresetId = preset.Id };

                widget.Config!.Remove(DeckConfigKey);
                if (widget.Config.Count == 0)
                {
                    widget.Config = null;
                }
            }
        }
    }

    private static string UniqueSuffixedName(List<DeckPreset> presets, string baseName)
    {
        if (!presets.Any(p => string.Equals(p.Name, baseName, StringComparison.OrdinalIgnoreCase)))
        {
            return baseName;
        }
        var n = 2;
        string candidate;
        do
        {
            candidate = $"{baseName} {n}";
            n++;
        }
        while (presets.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)));
        return candidate;
    }
}
