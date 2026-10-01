using System.Text;
using System.Text.Json;
using Nexus.Service.Models.Widgets;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Sockets;

/// <summary>src/Sockets/AppDataTopics.cs: the per-(appId,key) dynamic topic, mirroring UpdateStatusBroadcastTests' pattern for PanelTopics' fixed topics.</summary>
public sealed class AppDataTopicsTests
{
    [Fact]
    public void Broadcast_with_no_subscribers_never_touches_the_wire()
    {
        var hub = new MultiplexHub();
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);

        AppDataTopics.Broadcast(hub, "com.test.app", "save",
            new AppDataDocumentDto { Revision = 1, UpdatedAt = "t", Data = JsonDocument.Parse("1").RootElement });

        Assert.Empty(captured);
    }

    [Fact]
    public void Broadcast_with_a_subscriber_emits_the_document_under_its_own_topic()
    {
        var hub = new MultiplexHub();
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));
        var topic = AppDataTopics.TopicFor("com.test.app", "save");
        using var sub = hub.AddTestSubscription(topic);

        AppDataTopics.Broadcast(hub, "com.test.app", "save",
            new AppDataDocumentDto { Revision = 5, UpdatedAt = "2026-01-01T00:00:00Z", Data = JsonDocument.Parse("""{"x":1}""").RootElement });

        var frame = Assert.Single(captured, c => c.Topic == "app-data/com.test.app/save");
        var json = Encoding.UTF8.GetString(frame.Payload);
        Assert.Contains("\"revision\":5", json);
        Assert.Contains("\"x\":1", json);
    }

    [Fact]
    public void Different_keys_get_different_topics_and_never_cross_subscribe()
    {
        var hub = new MultiplexHub();
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);
        using var sub = hub.AddTestSubscription("app-data/com.test.app/save");

        AppDataTopics.Broadcast(hub, "com.test.app", "prefs",
            new AppDataDocumentDto { Revision = 1, UpdatedAt = "t", Data = JsonDocument.Parse("1").RootElement });

        // A subscriber to "save" must not receive a broadcast for "prefs" -
        // TopicHasSubscribers gates the write, so nothing is even attempted.
        Assert.Empty(captured);
    }

    [Fact]
    public void Document_frames_carry_the_profile_id()
    {
        var hub = new MultiplexHub();
        var captured = new List<byte[]>();
        hub.OnBroadcastForTest += (_, payload) => captured.Add(payload.ToArray());
        using var sub = hub.AddTestSubscription(AppDataTopics.TopicFor("com.test.app", "save"));

        AppDataTopics.Broadcast(hub, "com.test.app", "save",
            new AppDataDocumentDto { ProfileId = "p1", Revision = 1, UpdatedAt = "t", Data = JsonDocument.Parse("1").RootElement });

        Assert.Contains("\"profileId\":\"p1\"", Encoding.UTF8.GetString(Assert.Single(captured)));
    }

    [Fact]
    public void Reset_with_no_subscribers_never_touches_the_wire()
    {
        var hub = new MultiplexHub();
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);

        AppDataTopics.BroadcastReset(hub, "p1");

        Assert.Empty(captured);
    }

    [Fact]
    public void Reset_frame_carries_the_profile_id_and_a_resetId_unique_per_broadcast()
    {
        var hub = new MultiplexHub();
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));
        using var sub = hub.AddTestSubscription(AppDataTopics.ResetTopic);

        AppDataTopics.BroadcastReset(hub, "p1");
        AppDataTopics.BroadcastReset(hub, "p1");

        Assert.Equal(2, captured.Count);
        Assert.All(captured, c => Assert.Equal("app-data-reset", c.Topic));
        var frames = captured.Select(c => JsonDocument.Parse(c.Payload).RootElement.GetProperty("d")).ToList();
        Assert.All(frames, f => Assert.Equal("p1", f.GetProperty("profileId").GetString()));
        Assert.NotEqual(frames[0].GetProperty("resetId").GetString(), frames[1].GetProperty("resetId").GetString());
    }
}
