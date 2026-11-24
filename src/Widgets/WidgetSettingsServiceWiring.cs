using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Widgets;

/// <summary>Connects settings writes to the socket hub, so every write path broadcasts the same way.</summary>
public static class WidgetSettingsServiceWiring
{
    public static WidgetSettingsService Create(IConfigStore store, AppRegistry registry, MultiplexHub hub)
        => Wire(new WidgetSettingsService(store, registry), hub);

    internal static WidgetSettingsService Wire(WidgetSettingsService service, MultiplexHub hub)
    {
        service.SettingsChanged += (instanceId, doc) => AppSettingsTopics.Broadcast(hub, instanceId, doc);
        return service;
    }
}
