#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
/// registrar pushes the combo set to the helper of the active console session
/// and an empty set to every other helper, reads the per-id outcome, turns the
/// helper's <c>hotkeys.fired</c> pushes into <see cref="Fired"/> (console session
/// only), and asks for a re-push whenever a helper connects, since a fresh helper
/// holds no hotkeys. With no helper for the console session nothing can be
/// registered, so every id reports failure.
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
    public bool ReleaseOnStop => true;

    public event Action<string, string>? Fired;
    public event Action? NeedsReapply;

    public async Task<IReadOnlyDictionary<string, bool>> ApplyAsync(IReadOnlyList<HotkeyRegistration> set, CancellationToken ct)
    {
        var active = ActiveConnection();
        // Helpers of other sessions must not hold the combos or two desktops would both fire.
        foreach (var other in _registry.GetAll().Where(c => !ReferenceEquals(c, active)))
        {
            try
            {
                await HotkeyCommands.SetAsync(other, Array.Empty<HotkeyRegistration>(), ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A helper that dropped re-registers nothing on reconnect until pushed again.
            }
        }
        var outcome = active is null ? null : await HotkeyCommands.SetAsync(active, set, ct).ConfigureAwait(false);
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

    private HelperConnection? ActiveConnection()
    {
        var console = (int)WTSGetActiveConsoleSessionId();
        return _registry.GetForSession(console);
    }

    private void OnInbound(HelperConnection conn, HelperEnvelope env)
    {
        if (env.Type != HotkeyCommands.FiredType || conn.SessionId != (int)WTSGetActiveConsoleSessionId())
        {
            return;
        }
        if (HotkeyCommands.ParseFired(env) is { } fired)
        {
            Fired?.Invoke(fired.Id, fired.Combo);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
#endif
