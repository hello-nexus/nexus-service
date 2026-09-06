using System;
using System.Collections.Generic;
using Nexus.Service.Routes;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// IMetricsSampleSink that pushes each 1Hz sample onto the
/// monitoring/history-tail multiplex topic, built through
/// MonitoringHistoryRoutes.BuildHistoryResponse with fromSec == toSec == the
/// sample's own timestamp so every series decimates to exactly one point -
/// the same MetricsHistoryResponse shape GET /monitoring/history's tail poll
/// returns. Adapter LUIDs come from the sample's own GpuReading.AdapterLuid
/// rather than a fresh ISensorProvider.GetGpus() call, so this stays off the
/// LHM/Astral hardware-enumeration path on the sampler thread.
/// </summary>
public sealed class MonitoringHistoryTailBroadcaster : IMetricsSampleSink
{
    private const int MaxPoints = 1;

    private readonly MultiplexHub _hub;

    public MonitoringHistoryTailBroadcaster(MultiplexHub hub)
    {
        _hub = hub;
    }

    public void OnSample(MetricSample sample, DateTime nowUtc)
    {
        if (!_hub.TopicHasSubscribers(PanelTopics.MonitoringHistoryTail))
        {
            return;
        }

        var adapterLuids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var gpu in sample.Gpus)
        {
            if (!string.IsNullOrEmpty(gpu.AdapterLuid))
            {
                adapterLuids[gpu.GpuId] = gpu.AdapterLuid;
            }
        }

        var response = MonitoringHistoryRoutes.BuildHistoryResponse(
            Array.Empty<MetricSample>(), new[] { sample },
            sample.TsSec, sample.TsSec, MaxPoints, seriesFilter: null, adapterLuids);

        var env = WsEnvelope.Build(
            PanelTopics.MonitoringHistoryTail, response, AppJsonContext.Default.MetricsHistoryResponse);
        _ = _hub.BroadcastTopicAsync(PanelTopics.MonitoringHistoryTail, env);
    }
}
