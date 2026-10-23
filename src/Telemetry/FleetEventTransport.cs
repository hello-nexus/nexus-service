using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Telemetry;

/// <summary>Mirrors HeartbeatService's transport; a failed send returns false (never throws) so the caller retries later.</summary>
internal sealed class FleetEventTransport : IFleetEventTransport
{
    private const string Endpoint = "https://api.hellonexus.com/telemetry/events";

    private readonly IHttpClientFactory _http;

    public FleetEventTransport(IHttpClientFactory http) => _http = http;

    public async Task<bool> SendAsync(FleetEventPayload payload, CancellationToken ct) =>
        await PostAsync(payload, ct).ConfigureAwait(false) == FleetSendResult.Delivered;

    public async Task<FleetSendResult> PostAsync(FleetEventPayload payload, CancellationToken ct)
    {
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            Common.ClientCredential.Apply(client);
            using var content = JsonContent.Create(payload, AppJsonContext.Default.FleetEventPayload);
            using var res = await client.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"[fleet-event] {(int)res.StatusCode} from {Endpoint}");
                return res.StatusCode == System.Net.HttpStatusCode.BadRequest ? FleetSendResult.Rejected : FleetSendResult.Failed;
            }
            return FleetSendResult.Delivered;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fleet-event] send failed: {ex.GetType().Name}: {ex.Message}");
            return FleetSendResult.Failed;
        }
    }
}
