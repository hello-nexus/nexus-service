#if WINDOWS
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Helper;
using Nexus.Service.Helper.Domains;
using Nexus.Service.Hotkeys;

namespace Nexus.Service.Platform.Windows;

/// <summary>
/// Windows hotkeys: the LocalSystem service sits in Session 0 and cannot register
/// a hotkey on the user's desktop, so the user-session helper does it. This
/// registrar pushes the combo set over the pipe, reads the per-id outcome, turns
/// the helper's <c>hotkeys.fired</c> pushes into <see cref="Fired"/>, and asks for a
/// re-push whenever a helper (re)connects, since a fresh helper holds no hotkeys.
/// With no helper connected nothing can be registered, so every id reports failure.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HelperHotkeyRegistrar : IHotkeyRegistrar
{
    private readonly HelperRegistry _registry;

    public HelperHotkeyRegistrar(HelperRegistry registry)
    {
        _registry = registry;
        _registry.Connected += _ => NeedsReapply?.Invoke();
        _registry.InboundEnvelope += OnInbound;
    }

    public string Platform => "windows";
    public bool Supported => true;

    public event Action<string>? Fired;
    public event Action? NeedsReapply;

    public async Task<IReadOnlyDictionary<string, bool>> ApplyAsync(IReadOnlyList<HotkeyRegistration> set, CancellationToken ct)
    {
        var outcome = await HotkeyCommands.SetAsync(_registry, set, ct).ConfigureAwait(false);
        if (outcome is not null)
        {
            return outcome;
        }
        var failed = new Dictionary<string, bool>(set.Count, StringComparer.Ordinal);
        foreach (var reg in set)
        {
            failed[reg.Id] = false;
        }
        return failed;
    }

    private void OnInbound(HelperConnection conn, HelperEnvelope env)
    {
        if (env.Type != HotkeyCommands.FiredType)
        {
            return;
        }
        if (HotkeyCommands.ParseFired(env) is { } id)
        {
            Fired?.Invoke(id);
        }
    }
}
#endif
