using System.Linq;
using Nexus.Service.Models.Panel;
using Nexus.Service.Persistence;

namespace Nexus.Service.Panel;

/// <summary>
/// v32: the 8.8 inch strip stores its layout per orientation. Until now it kept one layout for both
/// mountings, so a record turned to portrait moves that layout (with its widget size and grid slots)
/// into the portrait slot and leaves the landscape slot empty: the user sees the same layout after the
/// upgrade, and only the orientation they are not on starts empty. A record in landscape is untouched.
/// <see cref="PanelDeviceRecord.LayoutSlots"/> marks a record as placed, which makes the pass idempotent
/// and safe to run on a current document: a downgraded build drops the marker and the portrait slot
/// with it, and the next load places the layout again.
/// </summary>
public static class PanelLayoutSlotsMigration
{
    public const int SchemaVersion = 32;

    /// <summary>True when a record would have its layout moved, which is what the pre-v32 backup guards.</summary>
    public static bool WouldMove(NexusSettings doc) =>
        doc.PanelDevices.Values.Any(r => NeedsPlacing(r) && PanelRotation.IsPortrait(PanelRotation.Effective(r)) && HasLandscapeLook(r));

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
                record.PortraitLayout = record.Layout;
                record.PortraitWidgetSize = record.WidgetSize;
                record.PortraitGridSlots = record.GridSlots;
                record.Layout = null;
                record.WidgetSize = null;
                record.GridSlots = null;
            }
            record.LayoutSlots = true;
            changed = true;
        }
        return changed;
    }

    private static bool NeedsPlacing(PanelDeviceRecord record) =>
        record.Capabilities?.SupportsPortrait == true && record.LayoutSlots != true;

    // Only a record with nothing in the portrait slot has a single layout to place.
    private static bool HasLandscapeLook(PanelDeviceRecord record) =>
        record.PortraitLayout is null && record.PortraitWidgetSize is null && record.PortraitGridSlots is null
        && (record.Layout is not null || record.WidgetSize is not null || record.GridSlots is not null);
}
