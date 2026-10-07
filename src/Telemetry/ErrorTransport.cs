using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Telemetry;

/// <summary>Same transport as <see cref="FleetEventTransport"/>; a failed send returns false and never throws.</summary>
internal sealed class ErrorTransport : IErrorTransport
{
    private const string Endpoint = "https://api.hellonexus.com/telemetry/errors";

    private readonly IHttpClientFactory _http;

    public ErrorTransport(IHttpClientFactory http) => _http = http;

    public async Task<bool> SendAsync(ErrorReportPayload payload, CancellationToken ct)
    {
        try
        {
            using var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            Common.ClientCredential.Apply(client);
            using var content = JsonContent.Create(payload, AppJsonContext.Default.ErrorReportPayload);
            using var res = await client.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[error-report] send failed: {ex.GetType().Name}");
            return false;
        }
    }
}
