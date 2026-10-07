using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Telemetry;

/// <summary>Delivers one error batch to nexus-api; abstracted so <see cref="ErrorReporter"/> is testable without HTTP.</summary>
internal interface IErrorTransport
{
    /// <returns>True on a 2xx response; false on any failure (never throws).</returns>
    Task<bool> SendAsync(ErrorReportPayload payload, CancellationToken ct);
}

internal sealed class NullErrorTransport : IErrorTransport
{
    public Task<bool> SendAsync(ErrorReportPayload payload, CancellationToken ct) => Task.FromResult(true);
}
