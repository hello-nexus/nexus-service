using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Monitoring.Events;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;
using Xunit;

namespace Nexus.Service.Tests.Monitoring.Events;

public class BroadcastingMonitoringEventStoreTests
{
    private sealed class RecordingEventStore : IMonitoringEventStore
    {
        public List<(long TUtcMs, string Kind, string Label, string? Detail, bool Custom)> Appends { get; } = new();
        public List<long> PruneCalls { get; } = new();
        public bool DeleteCustomCalled { get; private set; }

        public MonitoringEvent Append(long tUtcMs, string kind, string label, string? detail, bool custom)
        {
            Appends.Add((tUtcMs, kind, label, detail, custom));
            return new MonitoringEvent(Appends.Count, tUtcMs, kind, label, detail, custom);
        }

        public IReadOnlyList<MonitoringEvent> Query(long fromUtcMs, long toUtcMs, int limit) => Array.Empty<MonitoringEvent>();

        public bool DeleteCustom(long id)
        {
            DeleteCustomCalled = true;
            return true;
        }

        public void PruneOlderThan(long cutoffUtcMs) => PruneCalls.Add(cutoffUtcMs);
    }

    [Fact]
    public void Append_ForwardsToTheInnerStore_AndReturnsItsResult()
    {
        var inner = new RecordingEventStore();
        var decorator = new BroadcastingMonitoringEventStore(inner, new MultiplexHub());

        var created = decorator.Append(1000, MonitoringEventKinds.UsbAttach, "Mouse", "1234:5678", custom: false);

        var call = Assert.Single(inner.Appends);
        Assert.Equal((1000L, MonitoringEventKinds.UsbAttach, "Mouse", "1234:5678", false), call);
        Assert.Equal(1, created.Id);
        Assert.Equal("Mouse", created.Label);
    }

    [Fact]
    public void Query_DeleteCustom_PruneOlderThan_ForwardUnchanged()
    {
        var inner = new RecordingEventStore();
        var decorator = new BroadcastingMonitoringEventStore(inner, new MultiplexHub());

        decorator.Query(0, 100, 10);
        var deleted = decorator.DeleteCustom(5);
        decorator.PruneOlderThan(42);

        Assert.True(deleted);
        Assert.True(inner.DeleteCustomCalled);
        Assert.Equal(42, Assert.Single(inner.PruneCalls));
    }

    [Fact]
    public void Append_NoSubscribers_DoesNotBroadcast()
    {
        var hub = new MultiplexHub();
        var captured = new List<string>();
        hub.OnBroadcastForTest += (topic, _) => captured.Add(topic);
        var decorator = new BroadcastingMonitoringEventStore(new RecordingEventStore(), hub);

        decorator.Append(1000, MonitoringEventKinds.AppOpen, "game.exe", null, custom: false);

        Assert.Empty(captured);
    }

    [Fact]
    public void Append_WithSubscriber_BroadcastsTheCreatedEventAsAMonitoringEventDto()
    {
        var hub = new MultiplexHub();
        var captured = new List<(string Topic, byte[] Payload)>();
        hub.OnBroadcastForTest += (topic, payload) => captured.Add((topic, payload.ToArray()));
        using var sub = hub.AddTestSubscription(PanelTopics.MonitoringEvents);
        var decorator = new BroadcastingMonitoringEventStore(new RecordingEventStore(), hub);

        decorator.Append(1000, MonitoringEventKinds.Custom, "Started stream", "note", custom: true);

        var frame = captured.Single(c => c.Topic == PanelTopics.MonitoringEvents);
        using var doc = JsonDocument.Parse(frame.Payload);
        Assert.Equal(PanelTopics.MonitoringEvents, doc.RootElement.GetProperty("t").GetString());

        var dto = JsonSerializer.Deserialize(
            doc.RootElement.GetProperty("d").GetRawText(), AppJsonContext.Default.MonitoringEventDto);
        Assert.NotNull(dto);
        Assert.Equal(1000, dto!.T);
        Assert.Equal(MonitoringEventKinds.Custom, dto.Kind);
        Assert.Equal("Started stream", dto.Label);
        Assert.Equal("note", dto.Detail);
        Assert.True(dto.Custom);
    }
}
