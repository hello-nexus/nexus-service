using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Defaults;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Panel;

/// <summary>
/// Starter layout for a freshly registered panel device. Per-surface
/// definitions live in data/install-defaults.json under panel.layouts.
/// Shown until the user's first edit, which is persisted on the device
/// record and used in place of this default thereafter.
/// </summary>
public static class PanelLayoutDefaults
{
    public static PanelLayoutDto Default() => ForSurface("y70");

    public static PanelLayoutDto ForSurface(string surface)
    {
        // Layouts is nullable on the shared PanelSettings POCO (live profiles
        // leave it null); install-defaults always populates it.
        var layouts = InstallDefaults.Panel.Layouts ?? new PanelLayoutsDefaults();
        var src = surface switch
        {
            "desktop" => layouts.Desktop,
            // Promoted-monitor panels seed from the desktop layout: same
            // landscape, large-canvas shape; no dedicated JSON entry needed.
            Models.Panel.PanelSurfaces.Monitor => layouts.Desktop,
            "phone" => layouts.Phone,
            "q60" => layouts.Q60,
            _ => layouts.Y70,
        };
        return Build(src, surface);
    }

    /// <summary>Starter page for a panel on a 4:1 strip, where the surface's own seed would fill a quarter of the glass.</summary>
    public static PanelLayoutDto ForStrip(string surface) =>
        Build((InstallDefaults.Panel.Layouts ?? new PanelLayoutsDefaults()).Strip, surface);

    /// <summary>A monitor-surface panel at least three times as long as it is wide.</summary>
    public static bool IsStrip(string? surface, int width, int height) =>
        surface == Models.Panel.PanelSurfaces.Monitor
        && Math.Min(width, height) > 0
        && Math.Max(width, height) >= 3 * Math.Min(width, height);

    /// <summary>
    /// The strip seed for a streamed strip panel's record, else null. Display-bound monitors
    /// (a Xeneon Edge) keep the desktop seed.
    /// </summary>
    public static PanelLayoutDto? StripSeedFor(PanelDeviceRecord record) =>
        string.IsNullOrEmpty(record.DisplayId)
        && record.Capabilities is { } caps
        && IsStrip(caps.Surface, caps.CssWidth ?? 0, caps.CssHeight ?? 0)
            ? ForStrip(caps.Surface!)
            : null;

    private static PanelLayoutDto Build(PanelLayoutDefault src, string surface)
    {
        return new PanelLayoutDto
        {
            LayoutSchemaVersion = src.LayoutSchemaVersion,
            // Caller's surface argument wins regardless of what the JSON
            // entry's surface field says - guards against a hand-edit mistake
            // that would otherwise tag a `phone` layout as `y70`.
            Surface = surface,
            Pages = new List<PanelPageDto>
            {
                new()
                {
                    Id = Guid.NewGuid().ToString(),
                    Widgets = src.Widgets.Select(w => new PanelWidgetDto
                    {
                        Id = Guid.NewGuid().ToString(),
                        Type = w.Type,
                        Size = w.Size,
                        Col = w.Col,
                        Row = w.Row,
                        // Carry the seed's Config through so install-defaults
                        // can pre-populate per-widget settings (e.g. monitoring
                        // sensors, clock design). Null means "use the widget's
                        // own component-default behavior".
                        Config = w.Config,
                    }).ToList(),
                },
            },
        };
    }
}
