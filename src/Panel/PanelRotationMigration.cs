using Nexus.Service.Persistence;

namespace Nexus.Service.Panel;

/// <summary>
/// v31: a panel record's Flip180 and Portrait flags become one clockwise Rotation. The result is
/// the turn those flags produced, so a screen looks the same after the upgrade; a flag on a record
/// whose surface never read it had no effect and is dropped. Idempotent, and also run on a current
/// document, since a downgraded build writes the flags back.
/// </summary>
public static class PanelRotationMigration
{
    public const int SchemaVersion = 31;

    /// <summary>True when any record changed.</summary>
    public static bool Apply(NexusSettings doc)
    {
        var changed = false;
        foreach (var record in doc.PanelDevices.Values)
        {
            if (record.Flip180 is null && record.Portrait is null)
            {
                continue;
            }
            if (record.Rotation is null)
            {
                var rotation = PanelRotation.FromLegacy(record.Flip180, record.Portrait, record.Capabilities);
                record.Rotation = rotation == 0 ? null : rotation;
            }
            record.Flip180 = null;
            record.Portrait = null;
            changed = true;
        }
        return changed;
    }
}
