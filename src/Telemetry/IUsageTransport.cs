using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Telemetry;

/// <summary>Outcome plus the HTTP status (0 when no response arrived).</summary>
internal readonly record struct UsageSendResult(UsageDelivery Delivery, int Status);

internal enum UsageDelivery
{
    /// <summary>2xx: the server has the batch.</summary>
    Delivered,

    /// <summary>5xx, 429 or a network failure: keep the batch for the next flush.</summary>
    Retry,

    /// <summary>413: the body was too large; the caller splits the batch and retries.</summary>
    TooLarge,

    /// <summary>A 4xx other than 429: the server will never accept this batch.</summary>
    Drop,
}

/// <summary>Delivers one batch to nexus-api; abstracted so the worker is testable without HTTP. Never throws.</summary>
internal interface IUsageTransport
{
    Task<UsageSendResult> SendAsync(string installId, string version, IReadOnlyList<UsageEvent> batch, CancellationToken ct);
}
