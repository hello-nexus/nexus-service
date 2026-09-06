using System;

namespace Nexus.Service.Monitoring.History;

/// <summary>
/// Optional observer MetricsSampler notifies once per recorded 1Hz sample,
/// right after the sample lands in MetricsSampleBuffer. Implementations must
/// return quickly and never throw - MetricsSampler calls this synchronously
/// on its dedicated sampling thread.
/// </summary>
public interface IMetricsSampleSink
{
    void OnSample(MetricSample sample, DateTime nowUtc);
}
