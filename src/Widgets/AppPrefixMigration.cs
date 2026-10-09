using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Widgets;

/// <summary>
/// Rewrites the legacy "marketplace:" app-placement prefix to
/// <see cref="WidgetSettingsService.AppTypePrefix"/> across every persisted
/// widget-type carrier. The prefix rename is hard (no runtime alias), so
/// pre-rename layouts must be rewritten or their app placements stop
/// resolving. Runs as the schema v11 load migration and on profile
/// apply/import. Idempotent and null-tolerant against foreign documents.
/// </summary>
public static class AppPrefixMigration
{
    private const string LegacyPrefix = "marketplace:";

    public static void Apply(NexusSettings doc)
    {
        if (doc.Ui?.PinnedSidebarApps is { } pinned)
        {
            for (var i = 0; i < pinned.Count; i++)
            {
                pinned[i] = Rewrite(pinned[i]);
            }
        }
        RewriteLayout(doc.Panel?.DashboardLayout);
        RewriteSeeds(doc.Panel?.Layouts);
        if (doc.PanelDevices is not null)
        {
            foreach (var record in doc.PanelDevices.Values)
            {
                RewriteLayout(record?.Layout);
            }
        }
        if (doc.Overlay?.Layout is { } overlay)
        {
            foreach (var widget in overlay)
            {
                widget?.Type = Rewrite(widget.Type);
            }
        }
    }

    private static void RewriteLayout(PanelLayoutDto? layout)
    {
        if (layout?.Pages is not { } pages)
        {
            return;
        }
        foreach (var page in pages)
        {
            if (page?.Widgets is not { } widgets)
            {
                continue;
            }
            foreach (var widget in widgets)
            {
                widget?.Type = Rewrite(widget.Type);
            }
        }
    }

    private static void RewriteSeeds(PanelLayoutsDefaults? layouts)
    {
        if (layouts is null)
        {
            return;
        }
        foreach (var surface in new[] { layouts.Desktop, layouts.Y70, layouts.Phone, layouts.Q60, layouts.Strip })
        {
            if (surface?.Widgets is not { } widgets)
            {
                continue;
            }
            foreach (var widget in widgets)
            {
                widget?.Type = Rewrite(widget.Type);
            }
        }
    }

    private static string Rewrite(string? type)
        => type is not null && type.StartsWith(LegacyPrefix, System.StringComparison.Ordinal)
            ? WidgetSettingsService.AppTypePrefix + type.Substring(LegacyPrefix.Length)
            : type ?? "";
}
