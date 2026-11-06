using System.Linq;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Panel;

/// <summary>
/// v32: the 8.8 inch strip stores its layout per orientation. Until now it kept one layout for both
/// mountings. A record in landscape is untouched. A record turned to portrait gets that layout (with its
/// widget size and grid slots) copied into the portrait slot, and the landscape fields stay filled so a
/// downgraded build still finds a layout. The slot the record shows keeps the original page and widget
/// ids, because state outside the service is keyed by them (a whiteboard's drawing, an app's local
/// storage); the other slot's copy gets fresh ids derived from them, so each slot is independent. The
/// trade-off: a downgraded build reads the landscape fields, so a portrait user who downgrades sees the
/// fresh-id copy and loses that id-keyed state until upgrading again. Deck widgets keep a mode and a
/// preset in a row keyed by widget id; the copy gets its own row, while the preset itself is still
/// host-wide and shared. <see cref="PanelDeviceRecord.LayoutSlots"/> marks a record as placed, which makes
/// the pass idempotent and safe to run on a current document: a downgraded build drops the marker and the
/// portrait slot with it, and the next load places whatever it left in the landscape fields again,
/// deterministically.
/// </summary>
public static class PanelLayoutSlotsMigration
{
    public const int SchemaVersion = 32;

    /// <summary>True when a record would have its layout copied, which is what the pre-v32 backup guards.</summary>
    public static bool WouldMove(NexusSettings doc) =>
        doc.PanelDevices.Values.Any(r => NeedsPlacing(r) && PanelRotation.IsPortrait(PanelRotation.Effective(r)) && HasLandscapeLook(r))
        || doc.PanelDevices.Values.Any(NeedsClamp);

    /// <summary>True when any record changed.</summary>
    public static bool Apply(NexusSettings doc)
    {
        var changed = false;
        foreach (var record in doc.PanelDevices.Values)
        {
            if (!NeedsPlacing(record))
            {
                continue;
            }
            if (PanelRotation.IsPortrait(PanelRotation.Effective(record)) && HasLandscapeLook(record))
            {
                // Portrait is on show and keeps the ids; the landscape copy is the one re-identified.
                record.PortraitLayout = PanelPresets.CloneLayout(record.Layout);
                record.Layout = CopyWithFreshIds(doc, record.PortraitLayout);
                record.PortraitWidgetSize = record.WidgetSize;
                record.PortraitGridSlots = record.GridSlots;
            }
            record.LayoutSlots = true;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// A copy of the layout with its own page and widget ids so each slot is fully independent: a widget's
    /// settings are found by id, and a deck widget's mode and preset live in a row keyed by id, which the copy
    /// gets as well. The ids derive from the old ones, so the same input always gives the same copy. The copy's
    /// rows are overwritten from the original's: while the slot was dropped no build could edit them.
    /// </summary>
    private static PanelLayoutDto? CopyWithFreshIds(NexusSettings doc, PanelLayoutDto? source)
    {
        var copy = PanelPresets.CloneLayout(source);
        if (copy is null)
        {
            return null;
        }
        var widgetIds = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var page in copy.Pages)
        {
            var newPage = FreshId(page.Id);
            if (copy.ActivePageId == page.Id)
            {
                copy.ActivePageId = newPage;
            }
            page.Id = newPage;
            foreach (var widget in page.Widgets)
            {
                var old = widget.Id;
                widget.Id = FreshId(old);
                widgetIds[old] = widget.Id;
                var oldRow = Deck.DeckInstanceResolver.WidgetInstanceId(old);
                var newRow = Deck.DeckInstanceResolver.WidgetInstanceId(widget.Id);
                if (doc.StreamDeck.Instances.TryGetValue(oldRow, out var row))
                {
                    doc.StreamDeck.Instances[newRow] = new DeckInstance { Mode = row.Mode, ActivePresetId = row.ActivePresetId };
                }
            }
        }
        if (copy.ImmersiveOnLoadWidgetId is { } mark && widgetIds.TryGetValue(mark, out var moved))
        {
            copy.ImmersiveOnLoadWidgetId = moved;
        }
        return copy;
    }

    private static string FreshId(string id) =>
        new System.Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(id + "/portrait"))).ToString();

    /// <summary>
    /// Non-square glass takes quarter turns from this version on, so a quarter stored on one before it was
    /// never shown as a quarter: earlier builds clamped it to the half turn on a rectangle (a record reused
    /// by another family keeps its old value). The turn it displayed is kept. Runs once, with the version
    /// bump only, since a quarter on this glass is a real choice afterwards.
    /// </summary>
    public static bool ClampRectangular(NexusSettings doc)
    {
        var changed = false;
        foreach (var record in doc.PanelDevices.Values.Where(NeedsClamp))
        {
            record.Rotation = record.Rotation >= 180 ? 180 : null;
            changed = true;
        }
        return changed;
    }

    private static bool NeedsClamp(PanelDeviceRecord record) =>
        record.Rotation is 90 or 270
        && record.Capabilities is { SupportsPortrait: not true, CssWidth: > 0, CssHeight: > 0 } caps
        && caps.CssWidth != caps.CssHeight
        && caps.Surface is PanelSurfaces.LcdRound or PanelSurfaces.LcdSquare or PanelSurfaces.LcdWide;

    private static bool NeedsPlacing(PanelDeviceRecord record) =>
        record.Capabilities?.SupportsPortrait == true && record.LayoutSlots != true;

    // Only a record with nothing in the portrait slot has a single layout to place.
    private static bool HasLandscapeLook(PanelDeviceRecord record) =>
        record.PortraitLayout is null && record.PortraitWidgetSize is null && record.PortraitGridSlots is null
        && (record.Layout is not null || record.WidgetSize is not null || record.GridSlots is not null);
}
