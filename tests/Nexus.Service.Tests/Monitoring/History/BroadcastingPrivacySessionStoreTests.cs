using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.History;

public class BroadcastingPrivacySessionStoreTests
{
    private sealed class RecordingSessionStore : IPrivacySessionStore
    {
        public List<(string Capability, string AppId, long Start, long? End)> Upserts { get; } = new();
        public List<long> PruneCalls { get; } = new();

        public void Upsert(string capability, string appId, long startUtcSec, long? endUtcSec) =>
            Upserts.Add((capability, appId, startUtcSec, endUtcSec));

        public IReadOnlyList<PrivacySession> Query(long fromSec, long toSec) => Array.Empty<PrivacySession>();

        public void PruneOlderThan(long cutoffSec) => PruneCalls.Add(cutoffSec);
    }

    [Fact]
    public void Upsert_ForwardsToTheInnerStore_Unchanged()
    {
        var inner = new RecordingSessionStore();
        var decorator = new BroadcastingPrivacySessionStore(inner, new MultiplexHub());

        decorator.Upsert("microphone", "app.exe", 10, null);

        var call = Assert.Single(inner.Upserts);
        Assert.Equal(("microphone", "app.exe", 10L, (long?)null), call);
    }

    [Fact]
    public void Query_PruneOlderThan_ForwardUnchanged()
    {
        var inner = new RecordingSessionStore();
        var decorator = new BroadcastingPrivacySessionStore(inner, new MultiplexHub());

        decorator.Query(0, 100);
        decorator.PruneOlderThan(42);

        Assert.Equal(42, Assert.Single(inner.PruneCalls));
    }

    [Fact]
    public void Upsert_NoSubscribers_DoesNotBroadcast()
    {
        var hub = new MultiplexHub();
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);
        var decorator = new BroadcastingPrivacySessionStore(new RecordingSessionStore(), hub);

        decorator.Upsert("webcam", "zoom.exe", 10, null);

        Assert.Empty(captured);
    }

    [Fact]
    public void Upsert_WithSubscriber_BroadcastsAPrivacySessionWire_MatchingBuildPrivacyResponsesShape()
    {
        var hub = new MultiplexHub();
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));
        using var sub = hub.AddTestSubscription(PanelTopics.MonitoringPrivacy);
        var decorator = new BroadcastingPrivacySessionStore(new RecordingSessionStore(), hub);

        decorator.Upsert("microphone", "app.exe", 10, 20);

        var frame = captured.Single(c => c.Topic == PanelTopics.MonitoringPrivacy);
        using var doc = JsonDocument.Parse(frame.Payload);
        Assert.Equal(PanelTopics.MonitoringPrivacy, doc.RootElement.GetProperty("t").GetString());

        var wire = JsonSerializer.Deserialize(
            doc.RootElement.GetProperty("d").GetRawText(), AppJsonContext.Default.PrivacySessionWire);
        Assert.NotNull(wire);
        Assert.Equal("app.exe", wire!.App);
        Assert.Equal("microphone", wire.Capability);
        Assert.Equal(10_000, wire.Start); // seconds to milliseconds, same conversion BuildPrivacyResponse uses
        Assert.Equal(20_000, wire.End);
    }

    [Fact]
    public void Upsert_OpenSession_BroadcastsANullEnd()
    {
        var hub = new MultiplexHub();
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));
        using var sub = hub.AddTestSubscription(PanelTopics.MonitoringPrivacy);
        var decorator = new BroadcastingPrivacySessionStore(new RecordingSessionStore(), hub);

        decorator.Upsert("microphone", "app.exe", 10, null);

        var frame = captured.Single(c => c.Topic == PanelTopics.MonitoringPrivacy);
        using var doc = JsonDocument.Parse(frame.Payload);
        var wire = JsonSerializer.Deserialize(
            doc.RootElement.GetProperty("d").GetRawText(), AppJsonContext.Default.PrivacySessionWire);
        Assert.Null(wire!.End);
    }
}
