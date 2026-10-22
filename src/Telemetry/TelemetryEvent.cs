using System;
using System.Collections.Generic;

namespace Nexus.Service.Telemetry;

/// <summary>One queued event. Captured with its timestamp; the install id is
/// stamped later, at send time, so every event in a flush shares one id.</summary>
internal sealed class TelemetryEvent
{
    /// <summary>Minted at capture so every sink, and a persisted snapshot taken mid-flush, names the same event the same way.</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
    public IReadOnlyList<KeyValuePair<string, object?>> Properties { get; init; }
        = Array.Empty<KeyValuePair<string, object?>>();

    /// <summary>Person properties to merge via PostHog <c>$set</c>, when this is
    /// an identify. Null for ordinary events.</summary>
    public IReadOnlyList<KeyValuePair<string, object?>>? Set { get; init; }
}
