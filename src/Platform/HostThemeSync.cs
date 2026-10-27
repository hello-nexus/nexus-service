using Nexus.Service.Persistence;

namespace Nexus.Service.Platform;

/// <summary>
/// Sole writer of <c>Theme.ResolvedThemeMode</c>: the host OS light/dark theme
/// reported by the platform helper. HTTP clients cannot set it.
/// </summary>
public static class HostThemeSync
{
    /// <summary>macOS AppleInterfaceStyle is "Dark" in dark mode and absent in light mode.</summary>
    public static bool IsDarkInterfaceStyle(string? appleInterfaceStyle) =>
        string.Equals(appleInterfaceStyle, "Dark", StringComparison.OrdinalIgnoreCase);

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
}
