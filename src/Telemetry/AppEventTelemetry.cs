#if DEV_TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Nexus.Service.Telemetry;

public sealed class AppTelemetryRequest
{
    [JsonPropertyName("event")] public string? Event { get; set; }
    [JsonPropertyName("properties")] public Dictionary<string, JsonElement>? Properties { get; set; }
    [JsonPropertyName("surface")] public string? Surface { get; set; }
}

public sealed class AppPageClosedRequest
{
    [JsonPropertyName("durationMs")] public double DurationMs { get; set; }
}

/// <summary>Only enum-like tokens pass, so an app cannot send free text, emails or URLs.</summary>
public static partial class AppTelemetryValidator
{
    public const int MaxProperties = 10;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex NameShape();

    [GeneratedRegex("^[a-z0-9][a-z0-9_.:-]{0,63}$")]
    private static partial Regex ValueShape();

    private static readonly string[] Surfaces = { "page", "widget", "immersive" };

    /// <summary>The flat properties to capture, or null with <paramref name="error"/> set.</summary>
    public static (string Key, object? Value)[]? Validate(AppTelemetryRequest? body, out string? error)
    {
        error = null;
        if (body?.Event is null || !NameShape().IsMatch(body.Event))
        {
            error = "invalid event name";
            return null;
        }
        if (body.Surface is not null && Array.IndexOf(Surfaces, body.Surface) < 0)
        {
            error = "invalid surface";
            return null;
        }
        var props = body.Properties;
        if (props is { Count: > MaxProperties })
        {
            error = $"at most {MaxProperties} properties";
            return null;
        }

        var result = new List<(string Key, object? Value)>();
        foreach (var (key, value) in props ?? new Dictionary<string, JsonElement>())
        {
            if (!NameShape().IsMatch(key))
            {
                error = $"invalid property name '{key}'";
                return null;
            }
            object? parsed;
            switch (value.ValueKind)
            {
                case JsonValueKind.True: parsed = true; break;
                case JsonValueKind.False: parsed = false; break;
                case JsonValueKind.Number when value.TryGetDouble(out var d) && double.IsFinite(d): parsed = d; break;
                case JsonValueKind.String when ValueShape().IsMatch(value.GetString()!): parsed = value.GetString(); break;
                default:
                    error = $"invalid value for property '{key}'";
                    return null;
            }
            result.Add(("p_" + key, parsed));
        }
        return result.ToArray();
    }

        public static (string Key, object? Value)[] Compose(
        string appId, string appVersion, AppTelemetryRequest body, (string Key, object? Value)[] appProps)
    {
        var all = new List<(string Key, object? Value)>
        {
            ("app_id", appId), ("app_version", appVersion), ("event", body.Event),
        };
        if (body.Surface is not null) all.Add(("surface", body.Surface));
        all.Add(("dev_tools", true));
        all.AddRange(appProps);
        return all.ToArray();
    }

    public static (string Key, object? Value)[] ComposePageClosed(string appId, string appVersion, double durationMs)
    {
        var seconds = double.IsFinite(durationMs) ? Math.Clamp(Math.Round(durationMs / 1000, MidpointRounding.AwayFromZero), 0, 86400) : 0;
        return new (string, object?)[]
        {
            ("app_id", appId), ("app_version", appVersion), ("duration_s", seconds),
        };
    }
}

/// <summary>Fixed-window per-app limit on app-sent events.</summary>
public sealed class AppTelemetryRateLimiter
{
    public const int MaxPerMinute = 60;

    private readonly Dictionary<string, (long Window, int Count)> _buckets = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    public AppTelemetryRateLimiter() : this(() => DateTimeOffset.UtcNow) { }

    public AppTelemetryRateLimiter(Func<DateTimeOffset> clock) => _clock = clock;

    public bool TryAcquire(string appId)
    {
        var window = _clock().ToUnixTimeSeconds() / 60;
        lock (_buckets)
        {
            _buckets.TryGetValue(appId, out var bucket);
            if (bucket.Window != window) bucket = (window, 0);
            if (bucket.Count >= MaxPerMinute) return false;
            _buckets[appId] = (window, bucket.Count + 1);
            return true;
        }
    }
}

public sealed record RecordedAppEvent(DateTimeOffset At, string Event, IReadOnlyList<(string Key, object? Value)> Properties);

/// <summary>Buffers app_* events before the real client decides whether to send them, so they are visible without a PostHog key.</summary>
public sealed class AppEventRecorder : ITelemetry
{
    public const int Capacity = 200;

    private readonly ITelemetry _inner;
    private readonly Func<string> _status;
    private readonly Func<DateTimeOffset> _clock;
    private readonly LinkedList<RecordedAppEvent> _events = new();

    public AppEventRecorder(ITelemetry inner, Func<string> status, Func<DateTimeOffset>? clock = null)
    {
        _inner = inner;
        _status = status;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Capture(string @event, params (string Key, object? Value)[] properties)
    {
        if (@event is not null && @event.StartsWith("app_", StringComparison.Ordinal))
        {
            lock (_events)
            {
                _events.AddFirst(new RecordedAppEvent(_clock(), @event, properties.ToArray()));
                if (_events.Count > Capacity) _events.RemoveLast();
            }
        }
        _inner.Capture(@event!, properties);
    }

    public void Identify(params (string Key, object? Value)[] properties) => _inner.Identify(properties);

    /// <summary>"on", "off" (no key, nothing is sent) or "opted_out" (the user's setting wins).</summary>
    public string Status => _status();

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RecordedAppEvent> Recent()
    {
        lock (_events) return _events.ToArray();
    }

    public byte[] RecentJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("posthog", Status);
            w.WriteStartArray("events");
            foreach (var e in Recent())
            {
                w.WriteStartObject();
                w.WriteString("at", e.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                w.WriteString("event", e.Event);
                w.WriteStartObject("properties");
                foreach (var (key, value) in e.Properties)
                {
                    w.WritePropertyName(key);
                    WriteValue(w, value);
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case double d when double.IsFinite(d): w.WriteNumberValue(d); break;
            case float f when float.IsFinite(f): w.WriteNumberValue(f); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case IEnumerable<string> list:
                w.WriteStartArray();
                foreach (var item in list) w.WriteStringValue(item);
                w.WriteEndArray();
                break;
            default: w.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }
}
#endif
