using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Deck;

/// <summary>
/// Drops "widget:&lt;id&gt;" deck instance rows whose widget id no longer
/// appears in any panel layout (the desktop dashboard or any registered panel
/// device). Run after a layout save. "streamdeck:&lt;serial&gt;" rows are
/// never GC'd - a physical deck's instance persists with its Decks row even
/// while unplugged.
/// </summary>
public static class DeckInstanceGc
{
    public static void Run(NexusSettings doc)
    {
        var liveWidgetIds = new HashSet<string>(StringComparer.Ordinal);
        CollectWidgetIds(doc.Panel.DashboardLayout, liveWidgetIds);
        foreach (var record in doc.PanelDevices.Values)
        {
            CollectWidgetIds(record.Layout, liveWidgetIds);
        }

        foreach (var instanceId in doc.StreamDeck.Instances.Keys.ToList())
        {
            if (!instanceId.StartsWith("widget:", StringComparison.Ordinal))
            {
                continue;
            }
            var widgetId = instanceId["widget:".Length..];
            if (!liveWidgetIds.Contains(widgetId))
            {
                doc.StreamDeck.Instances.Remove(instanceId);
            }
        }
    }

    private static void CollectWidgetIds(PanelLayoutDto? layout, HashSet<string> into)
    {
        if (layout is null)
        {
            return;
        }
        foreach (var page in layout.Pages)
        {
            foreach (var widget in page.Widgets)
            {
                into.Add(widget.Id);
            }
        }
    }
}
