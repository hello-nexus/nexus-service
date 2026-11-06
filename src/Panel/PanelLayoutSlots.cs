using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Panel;

/// <summary>
/// A glass whose render size swaps under a quarter turn keeps one layout per orientation class so
/// turning it never rearranges the other one. The 8.8 inch strip is in this class too. <see cref="PanelDeviceRecord.Layout"/> and its
/// WidgetSize and GridSlots are the half-turn class, exactly as earlier builds stored them;
/// the Portrait-prefixed fields are the quarter class and stay empty until something is saved
/// there, which reads like a layout nobody has saved yet.
/// </summary>
public static class PanelLayoutSlots
{
    /// <summary>The record's current rotation puts it in the quarter-turn class.</summary>
    public static bool UsesPortraitSlot(PanelDeviceRecord record) =>
        PanelRotation.IsPortrait(PanelRotation.Effective(record))
        && (PanelRotation.SwapsRenderSize(record.Capabilities) || record.Capabilities?.SupportsPortrait == true);

    /// <summary>
    /// The record's page renders at the glass's size with its sides swapped and the transport turns it back. The
    /// 8.8 inch strip is not that: it renegotiates its geometry, so its capabilities already carry the turned size.
    /// </summary>
    public static bool TurnsRender(PanelDeviceRecord record) =>
        PanelRotation.IsPortrait(PanelRotation.Effective(record)) && PanelRotation.SwapsRenderSize(record.Capabilities);

    /// <summary>The layout the record shows now: the slot for its current rotation.</summary>
    public static PanelLayoutDto? Active(PanelDeviceRecord record) =>
        UsesPortraitSlot(record) ? record.PortraitLayout : record.Layout;

    public static void SetActiveLayout(PanelDeviceRecord record, PanelLayoutDto? layout)
    {
        if (UsesPortraitSlot(record))
        {
            record.PortraitLayout = layout;
        }
        else
        {
            record.Layout = layout;
        }
    }

    public static string? ActiveWidgetSize(PanelDeviceRecord record) =>
        UsesPortraitSlot(record) ? record.PortraitWidgetSize : record.WidgetSize;

    public static void SetActiveWidgetSize(PanelDeviceRecord record, string? size)
    {
        if (UsesPortraitSlot(record))
        {
            record.PortraitWidgetSize = size;
        }
        else
        {
            record.WidgetSize = size;
        }
    }

    public static int? ActiveGridSlots(PanelDeviceRecord record) =>
        UsesPortraitSlot(record) ? record.PortraitGridSlots : record.GridSlots;

    public static void SetActiveGridSlots(PanelDeviceRecord record, int? slots)
    {
        if (UsesPortraitSlot(record))
        {
            record.PortraitGridSlots = slots;
        }
        else
        {
            record.GridSlots = slots;
        }
    }

    /// <summary>Every stored layout of the record, for code that looks a widget up or rewrites one.</summary>
    public static IEnumerable<PanelLayoutDto> All(PanelDeviceRecord record)
    {
        if (record.Layout is not null)
        {
            yield return record.Layout;
        }
        if (record.PortraitLayout is not null)
        {
            yield return record.PortraitLayout;
        }
    }

    /// <summary>Clears the quarter-turn slot, as a personalization reset does.</summary>
    public static void ClearPortraitSlot(PanelDeviceRecord record)
    {
        record.PortraitLayout = null;
        record.PortraitWidgetSize = null;
        record.PortraitGridSlots = null;
    }

    /// <summary>
    /// The size the panel renders at for the record's rotation, or null where the glass takes no quarter
    /// turn. Square glass and the 8.8 inch strip (which renegotiates its own capabilities) keep them.
    /// </summary>
    public static (int Width, int Height)? RenderSize(PanelDeviceRecord record)
    {
        var caps = record.Capabilities;
        if (caps is not { CssWidth: > 0, CssHeight: > 0 } || !PanelRotation.SupportedFor(caps).Contains(90))
        {
            return null;
        }
        return TurnsRender(record)
            ? (caps.CssHeight!.Value, caps.CssWidth!.Value)
            : (caps.CssWidth!.Value, caps.CssHeight!.Value);
    }
}
