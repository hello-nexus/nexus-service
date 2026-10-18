using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Telemetry;

internal enum UsageDelivery
{
    /// <summary>2xx: the server has the batch.</summary>
    Delivered,

    /// <summary>5xx, 429 or a network failure: keep the batch for the next flush.</summary>
    Retry,

    /// <summary>A 4xx other than 429: the server will never accept this batch.</summary>
    Drop,
}

/// <summary>Delivers one batch to nexus-api; abstracted so the worker is testable without HTTP. Never throws.</summary>
internal interface IUsageTransport
{
    Task<UsageDelivery> SendAsync(string installId, string version, IReadOnlyList<UsageEvent> batch, CancellationToken ct);
}
