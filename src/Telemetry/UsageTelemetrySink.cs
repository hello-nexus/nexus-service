using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Telemetry;

/// <summary>
/// Parallel sink next to PostHog: every event the flush service hands out is
/// queued here for <see cref="UsageTelemetryWorker"/> to deliver to nexus-api.
/// The <c>$identify</c> person update is not mirrored as such; its hardware and
/// usage fields become one <c>profile</c> event instead. The queue is bounded
/// and in memory only: oldest events shed first.
/// </summary>
internal sealed class UsageTelemetrySink : ITelemetrySink
{
    internal const int MaxQueued = 2000;
    internal const string ProfileEvent = "profile";

    // Profile fields that reach PostHog only through the $identify $set. The
    // specs fleet event already carries cpu / gpu / ram / motherboard.
    private static readonly HashSet<string> ProfileFields = new(StringComparer.Ordinal)
    {
        "os_build", "storage", "storage_amount", "monitor", "network", "sound", "devices",
        "lighting_devices", "cooling_channels", "panel_devices", "panel_surfaces",
        "widget_count", "widget_page_count", "widget_types", "lighting_mode", "cooling_mode",
        "features_off",
    };

    private readonly object _lock = new();
    private readonly List<UsageEvent> _queue = new();

    public UsageTelemetrySink(bool enabled) => Enabled = enabled;

    public bool Enabled { get; }

    public int Count
    {
        get { lock (_lock) return _queue.Count; }
    }

    public Task SendAsync(string distinctId, IReadOnlyList<TelemetryEvent> batch, CancellationToken ct)
    {
        foreach (var e in batch)
        {
            var mapped = Map(e);
            if (mapped is not null)
                Enqueue(mapped);
        }
        return Task.CompletedTask;
    }

    private static UsageEvent? Map(TelemetryEvent e)
    {
        if (e.Name == "$identify")
        {
            if (e.Set is null)
                return null;
            var props = e.Set.Where(p => ProfileFields.Contains(p.Key)).ToArray();
            return props.Length == 0 ? null : New(ProfileEvent, e.Timestamp, props);
        }
        return New(e.Name, e.Timestamp, e.Properties);
    }

    private static UsageEvent New(string name, DateTimeOffset ts, IReadOnlyList<KeyValuePair<string, object?>> props) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Name = name,
        Timestamp = ts,
        Props = props,
    };

    private void Enqueue(UsageEvent e)
    {
        lock (_lock)
        {
            _queue.Add(e);
            TrimLocked();
        }
    }

    /// <summary>Removes and returns up to <paramref name="max"/> of the oldest events.</summary>
    public List<UsageEvent> Take(int max)
    {
        lock (_lock)
        {
            var n = Math.Min(max, _queue.Count);
            var batch = _queue.GetRange(0, n);
            _queue.RemoveRange(0, n);
            return batch;
        }
    }

    /// <summary>Puts an undelivered batch back at the head, ahead of anything queued meanwhile.</summary>
    public void Requeue(List<UsageEvent> batch)
    {
        lock (_lock)
        {
            _queue.InsertRange(0, batch);
            TrimLocked();
        }
    }

    /// <summary>Opted out: drop everything except the opt_out event, which is delivered regardless of consent.</summary>
    public void RetainOnlyOptOut()
    {
        lock (_lock) _queue.RemoveAll(e => e.Name != TelemetryEvents.OptOut);
    }

    public void Clear()
    {
        lock (_lock) _queue.Clear();
    }

    private void TrimLocked()
    {
        if (_queue.Count > MaxQueued)
            _queue.RemoveRange(0, _queue.Count - MaxQueued);
    }
}
