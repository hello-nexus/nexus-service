using System;
using System.Collections.Generic;
using Nexus.Service.Platform;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Monitoring.Events;

/// <summary>
/// IMonitoringEventStore decorator that forwards every call to the wrapped
/// store unchanged, then pushes the appended event onto the monitoring/events
/// multiplex topic. Wraps the single registered IMonitoringEventStore so both
/// producers (MonitoringEventCollector and POST /monitoring/events) push
/// through this one path.
/// </summary>
public sealed class BroadcastingMonitoringEventStore : IMonitoringEventStore
{
    private readonly IMonitoringEventStore _inner;
    private readonly MultiplexHub _hub;

    public BroadcastingMonitoringEventStore(IMonitoringEventStore inner, MultiplexHub hub)
    {
        _inner = inner;
        _hub = hub;
    }

    public MonitoringEvent Append(long tUtcMs, string kind, string label, string? detail, bool custom)
    {
        var created = _inner.Append(tUtcMs, kind, label, detail, custom);

        try
        {
            if (_hub.TopicHasSubscribers(PanelTopics.MonitoringEvents))
            {
                var dto = MonitoringHistoryRoutes.ToEventDto(created);
                var env = WsEnvelope.Build(PanelTopics.MonitoringEvents, dto, AppJsonContext.Default.MonitoringEventDto);
                _ = _hub.BroadcastTopicAsync(PanelTopics.MonitoringEvents, env);
            }
        }
        catch (Exception ex)
        {
            // A broadcast failure must not surface through Append: both
            // MonitoringEventCollector and POST /monitoring/events must still
            // see the event as recorded even when the push-only path fails.
            ServiceLog.Warn($"[monitoring-events] broadcast failed: {ex.Message}");
        }

        return created;
    }

    public IReadOnlyList<MonitoringEvent> Query(long fromUtcMs, long toUtcMs, int limit) =>
        _inner.Query(fromUtcMs, toUtcMs, limit);

    public bool DeleteCustom(long id) => _inner.DeleteCustom(id);

    public void PruneOlderThan(long cutoffUtcMs) => _inner.PruneOlderThan(cutoffUtcMs);
}
