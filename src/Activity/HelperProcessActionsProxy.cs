#if WINDOWS
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;

namespace Nexus.Service.Activity;

/// <summary>Service-side IProcessActionsProvider that routes both actions
/// through the user-session helper (see HelperProcessIconProxy for the same
/// constraint on icon extraction).</summary>
[SupportedOSPlatform("windows")]
public sealed class HelperProcessActionsProxy : IProcessActionsProvider
{
    private readonly HelperRegistry _registry;

    public HelperProcessActionsProxy(HelperRegistry registry) { _registry = registry; }

    public bool IsAvailable => _registry.IsAnyConnected;

    public async Task<(int Killed, int Failed)> KillAsync(string processName)
    {
        var result = await ProcessActionsCommands.KillAsync(_registry, processName).ConfigureAwait(false);
        return result is null ? (0, 0) : (result.Killed, result.Failed);
    }

    public Task<bool> OpenLocationAsync(string exePath)
        => ProcessActionsCommands.OpenLocationAsync(_registry, exePath);

    public Task<bool> ActivateWindowAsync(int pid)
        => ProcessActionsCommands.ActivateWindowAsync(_registry, pid);
}
#endif
