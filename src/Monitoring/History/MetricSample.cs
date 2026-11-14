using System;
using System.Collections.Generic;

namespace Nexus.Service.Monitoring.History;

/// <summary>One GPU's load/temperature reading within a MetricSample.
/// GpuId is already MetricsHistory.SanitizeId-ed. AdapterLuid is the raw
/// GpuReadout.AdapterLuid captured at sample time ("" when unmatched or off
/// Windows); GET /monitoring/history resolves the wire adapterLuid live
/// instead of trusting a historical sample's value, so this field exists for
/// completeness rather than being read back by the route.</summary>
public sealed record GpuReading(
    string GpuId, string Name, string AdapterLuid, double? LoadPercent, double? TempC);

/// <summary>One fan channel's reading within a MetricSample. FanId is
/// already MetricsHistory.SanitizeId-ed. Rpm is null when the channel can't
/// report it (FanChannel.RpmUnavailable); Duty is always known since a
/// channel only exists in this list because it reported a commanded duty.</summary>
public sealed record FanReading(string FanId, string Name, int? Rpm, int? Duty);

/// <summary>One storage or RAM component's temperature reading within a
/// MetricSample. ComponentId is the same stable id used elsewhere for that
/// component (SmartDriveInfo.Id for storage, "ram:&lt;index&gt;" for RAM);
/// Kind is "storage" or "ram".</summary>
public sealed record ComponentTempReading(string ComponentId, string Kind, string Name, double? ValueC);

/// <summary>One tracked-sensor reading within a MetricSample: Id is the
/// sensor alert rule id, Value is in the sensor's native unit. Only finite,
/// readable values are ever carried.</summary>
public sealed record SensorSeriesReading(string Id, double Value);

/// <summary>Supplies the tracked-sensor readings MetricsSampler stamps onto
/// each sample (implemented by SensorAlertEngine).</summary>
public interface ISensorSeriesSource
{
    IReadOnlyList<SensorSeriesReading> CurrentReadings(long tsSec);
}

/// <summary>
/// One second's worth of system metrics: wall-clock epoch-seconds timestamp
/// plus nullable scalars (null = that source failed this tick) and the
/// per-GPU / per-fan / per-temp-component reading lists. The unit
/// MetricsSampleBuffer buffers, IMetricsHistoryStore persists, and GET
/// /monitoring/history serves. CpuName, ComponentTemps, and the disk fields
/// default so every pre-existing construction site keeps compiling.
/// </summary>
public sealed record MetricSample(
    long TsSec,
    double? CpuPercent,
    double? MemoryPercent,
    double? NetInBytesPerSec,
    double? NetOutBytesPerSec,
    double? CpuTempC,
    IReadOnlyList<GpuReading> Gpus,
    IReadOnlyList<FanReading> Fans,
    string? CpuName = null,
    IReadOnlyList<ComponentTempReading>? ComponentTemps = null,
    double? DiskReadBytesPerSec = null,
    double? DiskWriteBytesPerSec = null,
    int? Fps = null)
{
    public IReadOnlyList<ComponentTempReading> ComponentTemps { get; init; } = ComponentTemps ?? Array.Empty<ComponentTempReading>();

    public IReadOnlyList<SensorSeriesReading> SensorReadings { get; init; } = Array.Empty<SensorSeriesReading>();
}
