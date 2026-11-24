using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;

namespace Nexus.Service.Sockets;

/// <summary>Fan-out for SDK app tile settings changes, one topic per widget instance so a running tile on any surface refreshes without reloading.</summary>
public static class AppSettingsTopics
{
    public static string TopicFor(string instanceId) => $"app-settings/{instanceId}";

    public static void Broadcast(MultiplexHub hub, string instanceId, WidgetSettingsDocument doc)
    {
        var topic = TopicFor(instanceId);
        if (!hub.TopicHasSubscribers(topic))
        {
            return;
        }
        var env = WsEnvelope.Build(topic, new AppSettingsFrame { Settings = doc.Values }, AppJsonContext.Default.AppSettingsFrame);
        _ = hub.BroadcastTopicAsync(topic, env);
    }
}
