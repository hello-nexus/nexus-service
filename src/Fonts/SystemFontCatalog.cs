using System.Diagnostics;

namespace Nexus.Service.Fonts;

/// <summary>
/// Installed font family names of the host OS, enumerated once and cached for the
/// life of the process. Any failure or unsupported OS yields an empty list.
/// </summary>
public static class SystemFontCatalog
{
    private static readonly Lazy<IReadOnlyList<string>> Cache = new(Enumerate);

    public static IReadOnlyList<string> Families => Cache.Value;

    private static IReadOnlyList<string> Enumerate()
    {
        try
        {
#if WINDOWS
            return Clean(WindowsFontEnumerator.Enumerate(), '@');
#elif MACOS
            return Clean(MacFontEnumerator.Enumerate(), '.');
#elif LINUX
            return Clean(ParseFcList(RunFcList()), null);
#else
            return [];
#endif
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Trims, drops empty names, names starting with <paramref name="hiddenPrefix"/> and names
    /// too long to store as a panel font, then de-duplicates and sorts case-insensitively.
    /// </summary>
    public static IReadOnlyList<string> Clean(IEnumerable<string?> names, char? hiddenPrefix)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in names)
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name) || (hiddenPrefix is char p && name[0] == p))
            {
                continue;
            }
            if (!Nexus.Service.Panel.PanelFontValue.IsValidFamily(name))
            {
                continue;
            }
            if (seen.Add(name))
            {
                result.Add(name);
            }
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>Each <c>fc-list : family</c> line may list comma-separated aliases; the first is the family.</summary>
    public static IEnumerable<string> ParseFcList(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var comma = line.IndexOf(',');
            yield return comma >= 0 ? line[..comma] : line;
        }
    }

#if LINUX
    private static string RunFcList()
    {
        var psi = new ProcessStartInfo("fc-list", ": family")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return string.Empty;
        }
        var output = proc.StandardOutput.ReadToEndAsync();
        if (!proc.WaitForExit(5000))
        {
            try { proc.Kill(); } catch { }
            return string.Empty;
        }
        return output.GetAwaiter().GetResult();
    }
#endif
}
