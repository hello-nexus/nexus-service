using System.Diagnostics;
using Nexus.Service.Platform;

namespace Nexus.Service.Fonts;

/// <summary>
/// Installed font family names of the host OS, enumerated once and cached for the
/// life of the process. Any failure or unsupported OS yields an empty list.
/// </summary>
public static class SystemFontCatalog
{
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);
    private static readonly object Gate = new();
    private static IReadOnlyList<string> _cached = [];
    private static DateTime _validUntilUtc = DateTime.MinValue;

    /// <summary>
    /// A non-empty result is cached for a while (the console user's fonts can appear later); an
    /// empty or failed one is retried after a short backoff so a request storm cannot re-enumerate.
    /// </summary>
    public static IReadOnlyList<string> Families
    {
        get
        {
            lock (Gate)
            {
                var now = DateTime.UtcNow;
                if (now < _validUntilUtc)
                {
                    return _cached;
                }
                var result = Enumerate();
                _cached = result;
                _validUntilUtc = now + (result.Count > 0 ? SuccessTtl : FailureBackoff);
                return result;
            }
        }
    }

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
        catch (Exception ex)
        {
            ServiceLog.Warn($"[system-fonts] enumeration failed: {ex.Message}");
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
