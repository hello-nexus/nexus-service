using System;
using System.Collections.Generic;

namespace Nexus.Service.Telemetry;

/// <summary>One usage event queued for nexus-api's /telemetry/usage. The id is minted once at capture so a retried batch is idempotent server-side.</summary>
internal sealed class UsageEvent
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
    public IReadOnlyList<KeyValuePair<string, object?>> Props { get; init; }
        = Array.Empty<KeyValuePair<string, object?>>();
}
