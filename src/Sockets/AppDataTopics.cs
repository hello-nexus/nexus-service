using Nexus.Service.Models.Widgets;
using Nexus.Service.Serialization;

namespace Nexus.Service.Sockets;

/// <summary>
/// Fan-out for app-data document changes. Unlike <see cref="PanelTopics"/>'s
/// fixed topic constants, the topic name is per (appId, key) - a widget
/// instance only ever subscribes to the document it owns, so there is no
/// fixed enum of topics to declare here.
/// </summary>
public static class AppDataTopics
{
    public const string ResetTopic = "app-data-reset";

    public static string TopicFor(string appId, string key) => $"app-data/{appId}/{key}";

    public static void Broadcast(MultiplexHub hub, string appId, string key, AppDataDocumentDto doc)
    {
        var topic = TopicFor(appId, key);
        if (!hub.TopicHasSubscribers(topic))
        {
            return;
        }
        var env = WsEnvelope.Build(topic, doc, AppJsonContext.Default.AppDataDocumentDto);
        _ = hub.BroadcastTopicAsync(topic, env);
    }

    public static void BroadcastReset(MultiplexHub hub, string profileId)
    {
        if (!hub.TopicHasSubscribers(ResetTopic))
        {
            return;
        }
        var env = WsEnvelope.Build(ResetTopic, new AppDataResetFrame { ProfileId = profileId, ResetId = Guid.NewGuid().ToString("N") }, AppJsonContext.Default.AppDataResetFrame);
        _ = hub.BroadcastTopicAsync(ResetTopic, env);
    }
}
