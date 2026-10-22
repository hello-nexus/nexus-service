using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Lighting.Rgb;

public enum NexusCoolingOutcome
{
    Ok,
    /// <summary>The daemon ACKed the packet as unsupported: an upstream build, or a controller without cooling.</summary>
    Unsupported,
    /// <summary>No answer, a refusal other than unsupported, or an unreadable reply.</summary>
    Failed,
}

public readonly record struct NexusCoolingStatusResult(NexusCoolingOutcome Outcome, CommanderCoreStatus? Status);

/// <summary>The Nexus cooling packets of the bundled OpenRGB daemon, addressed by controller address.</summary>
public interface INexusCoolingTransport
{
    Task<NexusCoolingStatusResult> GetCoolingStatusAsync(int address, CancellationToken ct = default);

    Task<NexusCoolingOutcome> SetCoolingDutiesAsync(int address, IReadOnlyList<(int Channel, int Duty)> duties, CancellationToken ct = default);
}

/// <summary>Stands in when the RGB controller has no cooling packets, so the provider reports nothing.</summary>
public sealed class NoNexusCoolingTransport : INexusCoolingTransport
{
    public Task<NexusCoolingStatusResult> GetCoolingStatusAsync(int address, CancellationToken ct = default) =>
        Task.FromResult(new NexusCoolingStatusResult(NexusCoolingOutcome.Unsupported, null));

    public Task<NexusCoolingOutcome> SetCoolingDutiesAsync(int address, IReadOnlyList<(int Channel, int Duty)> duties, CancellationToken ct = default) =>
        Task.FromResult(NexusCoolingOutcome.Unsupported);
}
