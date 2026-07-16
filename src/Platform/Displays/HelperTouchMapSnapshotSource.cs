#if WINDOWS
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Digitizer/display association is read in the user-session helper (raw
/// input + EnumDisplayMonitors are Session 0-blind, same constraint as
/// <see cref="HelperDisplayTopologyProxy"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperTouchMapSnapshotSource : ITouchMapSnapshotSource
{
    private readonly HelperRegistry _registry;

    public HelperTouchMapSnapshotSource(HelperRegistry registry)
    {
        _registry = registry;
    }

    public Task<TouchMapSnapshot?> GetSnapshotAsync(CancellationToken ct = default) =>
        TouchMapCommands.SnapshotAsync(_registry, ct);
}
#endif
