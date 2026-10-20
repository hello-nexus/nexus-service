using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.Panel;

/// <summary>
/// Removes a panel record the way the delete route does: the record, its uploaded media, and
/// the change notice. One place, so a record dropped by the service (a merge of duplicates)
/// leaves nothing behind that a user's delete would have cleaned.
/// </summary>
public sealed class PanelDeviceRemover
{
    private readonly PanelDeviceRegistry _registry;
    private readonly PanelBgLibrary _bgLibrary;
    private readonly MultiplexHub _hub;

    public PanelDeviceRemover(PanelDeviceRegistry registry, PanelBgLibrary bgLibrary, MultiplexHub hub)
    {
        _registry = registry;
        _bgLibrary = bgLibrary;
        _hub = hub;
    }

    /// <summary>
    /// A record is customized when it stores anything beyond a fresh one, or when its device
    /// folder holds uploaded background media (the files live outside the record).
    /// </summary>
    public bool IsCustomized(Models.Panel.PanelDeviceRecord record) =>
        PanelDeviceRegistry.IsCustomized(record) || _bgLibrary.ListItems(record.Id).Count > 0;

    /// <summary>False when the id is unknown.</summary>
    public bool Remove(string id)
    {
        if (!_registry.Remove(id))
        {
            return false;
        }
        // Single-instance surfaces mint a fresh id on the next allocate,
        // so a removed device's media dir would be orphaned forever.
        if (!_bgLibrary.DeleteDeviceMedia(id))
        {
            ServiceLog.Warn($"[panel] remove: media dir for '{id}' could not be fully removed");
        }
        PanelTopics.BroadcastPanelDevice(_hub, id);
        return true;
    }
}
