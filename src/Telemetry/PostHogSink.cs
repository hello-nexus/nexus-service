using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Telemetry;

/// <summary>
/// Ships batches to PostHog's /batch/ ingestion endpoint as hand-written JSON -
/// no SDK, so nothing reflective enters the NativeAOT build. Mirrors
/// HeartbeatService's transport (IHttpClientFactory, 10s timeout, swallowed
/// failures). No-op until a Project API Key is configured.
/// </summary>
internal sealed class PostHogSink : ITelemetrySink
{
    private const string LibName = "nexus-service";

    private static readonly string Os = TelemetryPlatform.OsTag();

    private readonly IHttpClientFactory _http;
    private readonly PostHogOptions _options;

    public PostHogSink(IHttpClientFactory http, PostHogOptions options)
    {
        _http = http;
        _options = options;
    }

    public bool Enabled => !string.IsNullOrEmpty(_options.ProjectApiKey);

    public async Task SendAsync(string distinctId, IReadOnlyList<TelemetryEvent> batch, CancellationToken ct)
    {
        if (!Enabled)
            return;

        // app_started is delivered to nexus-api only (UsageTelemetrySink).
        if (batch.Any(e => e.Name == TelemetryEvents.AppStarted))
            batch = batch.Where(e => e.Name != TelemetryEvents.AppStarted).ToArray();
        if (batch.Count == 0)
            return;

        var buffer = new ArrayBufferWriter<byte>();
        WriteBody(buffer, _options.ProjectApiKey, distinctId, batch);

        using var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        using var content = new ReadOnlyMemoryContent(buffer.WrittenMemory);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var res = await client
            .PostAsync(_options.BatchEndpoint, content, ct)
            .ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            Console.Error.WriteLine($"[telemetry] {(int)res.StatusCode} from PostHog");
    }

    // internal + static for testability: deterministic body from inputs alone.
    internal static void WriteBody(IBufferWriter<byte> buffer, string apiKey, string distinctId, IReadOnlyList<TelemetryEvent> batch)
    {
        using var w = new Utf8JsonWriter(buffer);
        w.WriteStartObject();
        w.WriteString("api_key", apiKey);
        w.WriteStartArray("batch");
        foreach (var e in batch)
        {
            w.WriteStartObject();
            w.WriteString("event", e.Name);
            w.WriteString("distinct_id", distinctId);
            w.WriteString("timestamp", e.Timestamp.ToString("O", CultureInfo.InvariantCulture));
            w.WriteStartObject("properties");
            w.WriteString("$lib", LibName);
            w.WriteString("$lib_version", BuildInfo.Version);
            w.WriteString("os", Os);
            foreach (var p in e.Properties)
            {
                w.WritePropertyName(p.Key);
                WriteValue(w, p.Value);
            }
            // Person properties ($set) - persisted on the install id (e.g. the
            // hardware/system profile from an Identify call).
            if (e.Set is { Count: > 0 })
            {
                w.WriteStartObject("$set");
                foreach (var p in e.Set)
                {
                    w.WritePropertyName(p.Key);
                    WriteValue(w, p.Value);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject(); // properties
            w.WriteEndObject(); // event
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    // Reflection-free value writer - covers the property types Capture accepts.
    internal static void WriteValue(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case double d: w.WriteNumberValue(d); break;
            case float f: w.WriteNumberValue(f); break;
            case decimal m: w.WriteNumberValue(m); break;
            // string[] / List<string> etc. → JSON array (string handled above, so
            // it never reaches here as IEnumerable<char>).
            case System.Collections.IEnumerable seq:
                w.WriteStartArray();
                foreach (var item in seq) WriteValue(w, item);
                w.WriteEndArray();
                break;
            default: w.WriteStringValue(v.ToString() ?? ""); break;
        }
    }
}
