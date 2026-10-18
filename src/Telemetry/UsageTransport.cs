using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Telemetry;

/// <summary>POSTs usage batches to nexus-api. Hand-written JSON (like <see cref="PostHogSink"/>) so nothing reflective enters the AOT build; a cross-language contract with /telemetry/usage.</summary>
internal sealed class UsageTransport : IUsageTransport
{
    private const string Endpoint = "https://api.hellonexus.com/telemetry/usage";

    private readonly IHttpClientFactory _http;

    public UsageTransport(IHttpClientFactory http) => _http = http;

    public async Task<UsageDelivery> SendAsync(string installId, string version, IReadOnlyList<UsageEvent> batch, CancellationToken ct)
    {
        try
        {
            var buffer = new ArrayBufferWriter<byte>();
            WriteBody(buffer, installId, version, batch);

            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            Common.ClientCredential.Apply(client);
            using var content = new ReadOnlyMemoryContent(buffer.WrittenMemory);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var res = await client.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
            return Classify(res.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return UsageDelivery.Retry;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[usage] send failed: {ex.GetType().Name}: {ex.Message}");
            return UsageDelivery.Retry;
        }
    }

    internal static UsageDelivery Classify(HttpStatusCode status)
    {
        var code = (int)status;
        if (code is >= 200 and < 300)
            return UsageDelivery.Delivered;
        if (status == HttpStatusCode.TooManyRequests)
            return UsageDelivery.Retry;
        return code is >= 400 and < 500 ? UsageDelivery.Drop : UsageDelivery.Retry;
    }

    internal static void WriteBody(IBufferWriter<byte> buffer, string installId, string version, IReadOnlyList<UsageEvent> batch)
    {
        using var w = new Utf8JsonWriter(buffer);
        w.WriteStartObject();
        w.WriteString("installId", installId);
        w.WriteString("version", version);
        w.WriteStartArray("events");
        foreach (var e in batch)
        {
            w.WriteStartObject();
            w.WriteString("id", e.Id);
            w.WriteString("event", e.Name);
            w.WriteString("ts", e.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            w.WriteStartObject("props");
            foreach (var p in e.Props)
            {
                w.WritePropertyName(p.Key);
                PostHogSink.WriteValue(w, p.Value);
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }
}
