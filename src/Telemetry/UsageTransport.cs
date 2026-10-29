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

/// <summary>POSTs usage batches to nexus-api. Hand-written JSON so nothing reflective enters the AOT build; a cross-language contract with /telemetry/usage.</summary>
internal sealed class UsageTransport : IUsageTransport
{
    private const string Endpoint = "https://api.hellonexus.com/telemetry/usage";

    private readonly IHttpClientFactory _http;

    public UsageTransport(IHttpClientFactory http) => _http = http;

    public async Task<UsageSendResult> SendAsync(string installId, string version, IReadOnlyList<UsageEvent> batch, CancellationToken ct)
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
            return new UsageSendResult(Classify(res.StatusCode), (int)res.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new UsageSendResult(UsageDelivery.Retry, 0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[usage] send failed: {ex.GetType().Name}: {ex.Message}");
            return new UsageSendResult(UsageDelivery.Retry, 0);
        }
    }

    internal static UsageDelivery Classify(HttpStatusCode status)
    {
        var code = (int)status;
        if (code is >= 200 and < 300)
            return UsageDelivery.Delivered;
        if (status == HttpStatusCode.RequestEntityTooLarge)
            return UsageDelivery.TooLarge;
        if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout)
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
                UsageTelemetrySink.WriteValue(w, p.Value);
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }
}
