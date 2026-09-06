using System;
using System.Collections.Generic;
using Nexus.Service.Platform;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// IPrivacySessionStore decorator that forwards every call to the wrapped
/// store unchanged, then pushes the upserted session onto the
/// monitoring/privacy multiplex topic. PrivacyAccessWatcher is the only
/// writer (see IPrivacySessionStore), so wrapping the single registered
/// instance covers every Upsert.
/// </summary>
public sealed class BroadcastingPrivacySessionStore : IPrivacySessionStore
{
    private readonly IPrivacySessionStore _inner;
    private readonly MultiplexHub _hub;

    public BroadcastingPrivacySessionStore(IPrivacySessionStore inner, MultiplexHub hub)
    {
        _inner = inner;
        _hub = hub;
    }

    public void Upsert(string capability, string appId, long startUtcSec, long? endUtcSec)
    {
        _inner.Upsert(capability, appId, startUtcSec, endUtcSec);

        try
        {
            if (_hub.TopicHasSubscribers(PanelTopics.MonitoringPrivacy))
            {
                var wire = MonitoringHistoryRoutes.ToPrivacySessionWire(
                    new PrivacySession(appId, capability, startUtcSec, endUtcSec));
                var env = WsEnvelope.Build(PanelTopics.MonitoringPrivacy, wire, AppJsonContext.Default.PrivacySessionWire);
                _ = _hub.BroadcastTopicAsync(PanelTopics.MonitoringPrivacy, env);
            }
        }
        catch (Exception ex)
        {
            // A broadcast failure must not surface through Upsert: PrivacyAccessWatcher
            // would otherwise lose the rest of its batch over a push-only concern.
            ServiceLog.Warn($"[monitoring-privacy] broadcast failed: {ex.Message}");
        }
    }

    public IReadOnlyList<PrivacySession> Query(long fromSec, long toSec) => _inner.Query(fromSec, toSec);

    public void PruneOlderThan(long cutoffSec) => _inner.PruneOlderThan(cutoffSec);
}
