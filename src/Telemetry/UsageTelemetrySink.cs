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
/// usage fields become one <c>profile</c> event instead. The queue is bounded:
/// oldest events shed first. It is snapshotted to disk at exit (<see cref="Persist"/>)
/// and reloaded at start (<see cref="Restore"/>).
/// </summary>
internal sealed class UsageTelemetrySink : ITelemetrySink
{
    internal const int MaxQueued = 2000;
    internal const string ProfileEvent = TelemetryEvents.ProfileRefresh;

    // Profile fields that reach PostHog only through the $identify $set. The
    // specs fleet event already carries cpu / gpu / ram / motherboard.
    private static readonly HashSet<string> ProfileFields = new(StringComparer.Ordinal)
    {
        "os_build", "storage", "storage_amount", "monitor", "network", "sound", "devices",
        "lighting_devices", "cooling_channels", "panel_devices", "panel_surfaces",
        "paired_phones", "phones_active_7d", "relay_enabled",
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
        if (e.Name is "$identify" or TelemetryEvents.ProfileRefresh)
        {
            var source = e.Name == "$identify" ? e.Set : e.Properties;
            if (source is null)
                return null;
            var props = source.Where(p => ProfileFields.Contains(p.Key)).ToArray();
            return props.Length == 0 ? null : New(e.Id, ProfileEvent, e.Timestamp, props);
        }
        return New(e.Id, e.Name, e.Timestamp, e.Properties);
    }

    private const int EnvelopeBytes = 128;

    private static UsageEvent New(string eventId, string name, DateTimeOffset ts, IReadOnlyList<KeyValuePair<string, object?>> props)
    {
        var fitted = FitProps(props);
        return new()
        {
            Id = string.IsNullOrEmpty(eventId) ? Guid.NewGuid().ToString() : eventId,
            Name = name,
            Timestamp = ts,
            Props = fitted,
            Bytes = Measure(fitted) + name.Length + EnvelopeBytes,
        };
    }

    // Keeps each event under nexus-api's per-event props size cap, which otherwise drops the whole event.
    internal const int MaxPropsBytes = 6000;
    private const int MaxStringChars = 256;

    /// <summary>Truncates long strings, then halves the largest list (or drops the largest value) until the serialized props fit.</summary>
    internal static KeyValuePair<string, object?>[] FitProps(IReadOnlyList<KeyValuePair<string, object?>> props)
    {
        var list = props.Select(p => new KeyValuePair<string, object?>(p.Key, Clamp(p.Value))).ToList();
        while (list.Count > 0 && Measure(list) > MaxPropsBytes)
        {
            var largest = 0;
            var largestSize = -1;
            for (var i = 0; i < list.Count; i++)
            {
                var size = Measure(new[] { list[i] });
                if (size > largestSize)
                {
                    largest = i;
                    largestSize = size;
                }
            }
            if (list[largest].Value is string[] arr && arr.Length > 1)
                list[largest] = new(list[largest].Key, arr[..(arr.Length / 2)]);
            else
                list.RemoveAt(largest);
        }
        return list.ToArray();
    }

    private static object? Clamp(object? v) => v switch
    {
        string s when s.Length > MaxStringChars => s[..MaxStringChars],
        string[] a => a.Select(x => x.Length > MaxStringChars ? x[..MaxStringChars] : x).ToArray(),
        _ => v,
    };

    private static int Measure(IReadOnlyList<KeyValuePair<string, object?>> props)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var p in props)
            {
                w.WritePropertyName(p.Key);
                PostHogSink.WriteValue(w, p.Value);
            }
            w.WriteEndObject();
        }
        return buffer.WrittenCount;
    }

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

    /// <summary>Moves a snapshot left by <see cref="Persist"/> back to the head of the queue and deletes the file.</summary>
    public int Restore(string path)
    {
        var events = UsageQueueFile.ReadAndDelete(path);
        if (events.Count > 0)
            Requeue(events);
        return events.Count;
    }

    /// <summary>
    /// Snapshots the queue plus <paramref name="undrained"/> (events the flush service has not handed over yet) to
    /// <paramref name="path"/>. An opted-out install keeps only a pending opt_out. Never throws.
    /// </summary>
    public void Persist(string path, bool optedOut, IReadOnlyList<TelemetryEvent>? undrained = null)
    {
        try
        {
            List<UsageEvent> all;
            lock (_lock) all = new List<UsageEvent>(_queue);
            if (undrained is not null)
            {
                foreach (var e in undrained)
                {
                    // Its own try: one unmappable event (a non-finite number) must not drop the snapshot.
                    try
                    {
                        if (Map(e) is { } mapped)
                            all.Add(mapped);
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                    {
                        Console.Error.WriteLine($"[usage] skipped unmappable {e.Name} in snapshot: {ex.GetType().Name}");
                    }
                }
            }
            // A flush tick between the client snapshot and the queue copy puts the same event in both, under one id.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            all.RemoveAll(e => !seen.Add(e.Id));
            if (optedOut)
                all.RemoveAll(e => e.Name != TelemetryEvents.OptOut);
            if (all.Count > MaxQueued)
                all.RemoveRange(0, all.Count - MaxQueued);
            UsageQueueFile.Write(path, all);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[usage] queue persist failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The Windows service exits without running host StopAsync (Program.FastServiceShutdown), so the worker's final
    /// flush never runs there. This is the network-free equivalent: snapshot everything pending, including events
    /// still in the client queue.
    /// </summary>
    public void PersistForShutdown(Persistence.IConfigStore store, TelemetryClient client)
    {
        if (!Enabled)
            return;
        // Client queue first, then the sink: an event drained in between is then in both, and Persist collapses it by id.
        var undrained = client.Snapshot();
        Persist(UsageQueueFile.DefaultPath(), !store.Load().Telemetry.CollectAnonymousData, undrained);
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
