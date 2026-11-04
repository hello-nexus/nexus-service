using Nexus.Service.Persistence;

namespace Nexus.Service.Platform;

/// <summary>
/// Sole writer of <c>Theme.ResolvedThemeMode</c>: the host OS light/dark theme
/// reported by the platform helper. HTTP clients cannot set it.
/// </summary>
public static class HostThemeSync
{
    /// <summary>Stores the host theme; returns true when the value changed.</summary>
    public static bool Apply(IConfigStore store, bool dark)
    {
        var resolved = dark ? "dark" : "light";
        if (store.Load().Theme.ResolvedThemeMode == resolved) return false;
        var changed = false;
        store.Update(s =>
        {
            if (s.Theme.ResolvedThemeMode == resolved) return;
            s.Theme.ResolvedThemeMode = resolved;
            changed = true;
        });
        return changed;
    }

    /// <summary>
    /// For hosts with no OS theme source: drops a stale persisted value so panels
    /// fall back to their local media query. Returns true when it cleared one.
    /// </summary>
    public static bool ClearUnsourced(IConfigStore store)
    {
        if (store.Load().Theme.ResolvedThemeMode.Length == 0) return false;
        store.Update(s => s.Theme.ResolvedThemeMode = "");
        return true;
    }
}
