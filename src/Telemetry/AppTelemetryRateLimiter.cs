using System;
using System.Collections.Generic;

namespace Nexus.Service.Telemetry;

/// <summary>Fixed one-minute window per app.</summary>
public sealed class AppTelemetryRateLimiter
{
    private readonly Dictionary<string, (long Window, int Count)> _buckets = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    private readonly int _maxPerMinute;

    public AppTelemetryRateLimiter(int maxPerMinute, Func<DateTimeOffset>? clock = null)
    {
        _maxPerMinute = maxPerMinute;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool TryAcquire(string appId)
    {
        var window = _clock().ToUnixTimeSeconds() / 60;
        lock (_buckets)
        {
            _buckets.TryGetValue(appId, out var bucket);
            if (bucket.Window != window) bucket = (window, 0);
            if (bucket.Count >= _maxPerMinute) return false;
            _buckets[appId] = (window, bucket.Count + 1);
            return true;
        }
    }
}
