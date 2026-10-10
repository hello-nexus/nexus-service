#if WINDOWS
using System.Collections.Generic;
using System.Runtime.Versioning;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>
/// Service-side IShortcutsProvider that runs installed-app enumeration, icons,
/// and launch through the user-session helper. Get-StartApps is per-user and
/// empty from Session 0, so a direct launch in the LocalSystem service can't
/// even resolve the target id (let alone reach the user's shell).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperShortcutsProxy : IShortcutsProvider
{
    private readonly HelperRegistry _registry;

    public HelperShortcutsProxy(HelperRegistry registry) { _registry = registry; }

    public IReadOnlyList<Shortcut> GetAll()
        => ShortcutsCommands.GetAllAsync(_registry).GetAwaiter().GetResult();

    public Shortcut? GetById(string targetId)
        => ShortcutsCommands.GetByIdAsync(_registry, targetId).GetAwaiter().GetResult();

    public byte[]? GetIcon(string targetId)
        => ShortcutsCommands.GetIconAsync(_registry, targetId).GetAwaiter().GetResult();

    public bool Launch(string targetId)
        => ShortcutsCommands.LaunchAsync(_registry, targetId).GetAwaiter().GetResult();

    public string ResolveProcessName(string targetId)
        => ShortcutsCommands.ResolveProcessNameAsync(_registry, targetId).GetAwaiter().GetResult();
}
#endif
