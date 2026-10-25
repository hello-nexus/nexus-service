using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Telemetry;

/// <summary>Outcome of one fleet event POST.</summary>
internal enum FleetSendResult
{
    Delivered,

    /// <summary>nexus-api answered Bad Request: it refused the body itself, as an older api does for fields it does not know.</summary>
    Rejected,
    Failed,
}

/// <summary>Delivers one fleet event to nexus-api; abstracted so <see cref="FleetEventService"/> is testable without a real HTTP call.</summary>
internal interface IFleetEventTransport
{
    /// <returns>True on a 2xx response; false on any failure (never throws).</returns>
    Task<bool> SendAsync(FleetEventPayload payload, CancellationToken ct);

    /// <summary>Same send with the failure kind kept apart; defaults to <see cref="SendAsync"/> for transports that cannot tell.</summary>
    async Task<FleetSendResult> PostAsync(FleetEventPayload payload, CancellationToken ct) =>
        await SendAsync(payload, ct).ConfigureAwait(false) ? FleetSendResult.Delivered : FleetSendResult.Failed;
}
