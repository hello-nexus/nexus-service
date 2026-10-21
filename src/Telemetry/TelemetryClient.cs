using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Persistence;

namespace Nexus.Service.Telemetry;

/// <summary>
/// Default <see cref="ITelemetry"/>: a bounded in-memory queue. Capture()
/// enqueues and returns immediately; <see cref="TelemetryFlushService"/> drains
/// and sends. The queue is capped so a sink outage or a burst can never grow
/// memory - oldest events shed first (telemetry is best-effort, never
/// back-pressure). The opt-out is honored at the source: while disabled,
/// Capture() is a no-op and the queue is cleared.
/// </summary>
internal sealed class TelemetryClient : ITelemetry, IDisposable
{
    // ~10 min of headroom at a few events/sec; overflow drops oldest.
    private const int MaxQueued = 2048;

    private readonly ConcurrentQueue<TelemetryEvent> _queue = new();
    private readonly IConfigStore _store;
    private volatile bool _enabled;
    private int _count;

    public TelemetryClient(IConfigStore store)
    {
        _store = store;
        _enabled = store.Load().Telemetry.CollectAnonymousData;
        _store.OnChanged += OnSettingsChanged;
    }

    public void Capture(string @event, params (string Key, object? Value)[] properties)
    {
        if (!_enabled || string.IsNullOrEmpty(@event))
            return;

        Enqueue(new TelemetryEvent
        {
            Name = @event,
            Timestamp = DateTimeOffset.UtcNow,
            Properties = Map(properties),
        });
    }

    public void Identify(params (string Key, object? Value)[] properties)
    {
        if (!_enabled || properties.Length == 0)
            return;

        Enqueue(new TelemetryEvent
        {
            Name = "$identify",
            Timestamp = DateTimeOffset.UtcNow,
            Set = Map(properties),
        });
    }

    private void Enqueue(TelemetryEvent e)
    {
        _queue.Enqueue(e);
        if (Interlocked.Increment(ref _count) > MaxQueued && _queue.TryDequeue(out _))
            Interlocked.Decrement(ref _count);
    }

    /// <summary>Drain up to <paramref name="max"/> events for one batch.</summary>
    public List<TelemetryEvent> DrainBatch(int max)
    {
        // Clamp: Capture enqueues then increments as two steps, so a concurrent
        // drain/clear can drive _count transiently negative - never seed a List
        // with a negative capacity.
        var batch = new List<TelemetryEvent>(Math.Clamp(Volatile.Read(ref _count), 0, max));
        while (batch.Count < max && _queue.TryDequeue(out var e))
        {
            Interlocked.Decrement(ref _count);
            batch.Add(e);
        }
        return batch;
    }

    /// <summary>Non-destructive copy of what is still queued.</summary>
    public TelemetryEvent[] Snapshot() => _queue.ToArray();

    /// <summary>Discard everything queued (used when the user opts out).</summary>
    public void Clear()
    {
        while (_queue.TryDequeue(out _))
            Interlocked.Decrement(ref _count);
    }

    private static KeyValuePair<string, object?>[] Map((string Key, object? Value)[] p)
    {
        if (p.Length == 0)
            return Array.Empty<KeyValuePair<string, object?>>();
        var a = new KeyValuePair<string, object?>[p.Length];
        for (int i = 0; i < p.Length; i++)
            a[i] = new KeyValuePair<string, object?>(p[i].Key, p[i].Value);
        return a;
    }

    private void OnSettingsChanged()
    {
        _enabled = _store.Load().Telemetry.CollectAnonymousData;
        if (!_enabled)
            Clear();
    }

    public void Dispose() => _store.OnChanged -= OnSettingsChanged;
}
