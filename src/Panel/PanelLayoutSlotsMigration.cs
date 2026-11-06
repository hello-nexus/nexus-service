using System.Linq;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Panel;

/// <summary>
/// v32: the 8.8 inch strip stores its layout per orientation. Until now it kept one layout for both
/// mountings, so a record turned to portrait gets that layout (with its widget size and grid slots)
/// copied into the portrait slot, and keeps it in the landscape slot too: the user sees the same layout
/// after the upgrade, and a downgraded build, which reads only the landscape fields and drops the rest,
/// still finds what the strip showed. A record in landscape is untouched.
/// <see cref="PanelDeviceRecord.LayoutSlots"/> marks a record as placed, which makes the pass idempotent
/// and safe to run on a current document: a downgraded build drops the marker and the portrait slot
/// with it, and the next load places whatever it left in the landscape fields again.
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
                record.PortraitLayout = PanelPresets.CloneLayout(record.Layout);
                record.PortraitWidgetSize = record.WidgetSize;
                record.PortraitGridSlots = record.GridSlots;
            }
            record.LayoutSlots = true;
            changed = true;
        }
        return changed;
    }

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
