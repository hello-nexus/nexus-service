using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Routes;

namespace Nexus.Service.Diagnostics;

/// <summary>Plain string replacement and filtering for bundle contents: usernames in home paths, the machine name, other apps' crashes, NEXUS_* environment.</summary>
public sealed class BundleRedactor
{
    private static readonly HashSet<string> GenericMachineNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "NEXUS", "PC", "DESKTOP", "GAMING", "RYZEN", "INTEL", "AMD", "NVIDIA", "WINDOWS", "LAPTOP", "WORKSTATION", "SERVER",
        "HOME", "USER", "ADMIN", "LOCALHOST", "MACBOOK", "MSI", "ASUS", "HYTE", "CORSAIR", "COMPUTER", "BUILD", "TEST",
    };

    private readonly List<(string Needle, string Replacement)> _paths = new();
    private readonly List<string> _machine = new();

    public BundleRedactor(IEnumerable<string> userNames, string machineName)
    {
        // Longest first, so a profile "alice" is never cut short by a profile "al".
        foreach (var user in userNames.Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(u => u.Length))
        {
            // The JSON-encoded name covers non-ASCII names, which the default encoder escapes.
            foreach (var name in new[] { user, JsonEncodedText.Encode(user).ToString() }.Distinct())
            {
                _paths.Add((@"\\Users\\" + name, @"\\Users\\<user>"));
                _paths.Add((@"\Users\" + name, @"\Users\<user>"));
                _paths.Add(("/Users/" + name, "/Users/<user>"));
                _paths.Add(("/home/" + name, "/home/<user>"));
            }
        }
        if (!IsGenericMachineName(machineName))
        {
            _machine.Add(machineName);
            var encoded = JsonEncodedText.Encode(machineName).ToString();
            if (encoded != machineName)
            {
                _machine.Add(encoded);
            }
        }
    }

    /// <summary>Too short or too common to replace inside free text without corrupting it; such a name is redacted only in fields that carry it explicitly.</summary>
    internal static bool IsGenericMachineName(string name) => name.Length < 4 || GenericMachineNames.Contains(name);

    public string Apply(string text)
    {
        foreach (var (needle, replacement) in _paths)
        {
            text = ReplaceBounded(text, needle, replacement, pathSegment: true);
        }
        foreach (var name in _machine)
        {
            text = ReplaceBounded(text, name, "<pc>", pathSegment: false);
        }
        return text;
    }

    // A path segment ends at a separator, quote, whitespace or the end; a machine name is a whole token, so letters, digits, '-' and '_' on either side keep it.
    private static string ReplaceBounded(string text, string needle, string replacement, bool pathSegment)
    {
        var at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return text;
        }
        var sb = new System.Text.StringBuilder(text.Length);
        var last = 0;
        while (at >= 0)
        {
            var end = at + needle.Length;
            var okAfter = end >= text.Length || (pathSegment ? IsSegmentEnd(text[end]) : !IsTokenChar(text[end]));
            var okBefore = pathSegment || at == 0 || !IsTokenChar(text[at - 1]);
            if (okAfter && okBefore)
            {
                sb.Append(text, last, at - last).Append(replacement);
                last = end;
            }
            at = text.IndexOf(needle, end, StringComparison.OrdinalIgnoreCase);
        }
        return sb.Append(text, last, text.Length - last).ToString();
    }

    private static bool IsSegmentEnd(char c) => c is '\\' or '/' or '"' or '\'' or ',' or ';' or ')' or ']' || char.IsWhiteSpace(c);

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';
}

public static class SupportBundleExtras
{
    internal const int MaxOverrideFileChars = 1024;

    private static readonly string[] SecretWords = { "TOKEN", "KEY", "SECRET", "PASSWORD" };

    /// <summary>Every NEXUS_* variable, value redacted when the name contains TOKEN, KEY, SECRET or PASSWORD.</summary>
    public static Dictionary<string, string> NexusEnvironment(System.Collections.IDictionary environment)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry kv in environment)
        {
            var name = kv.Key as string ?? "";
            if (!name.StartsWith("NEXUS_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            result[name] = SecretWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)) ? "<redacted>" : kv.Value as string ?? "";
        }
        return result;
    }

    /// <summary>Existing helper-disable style files by path relative to <paramref name="dataRoot"/>, with their (truncated) contents.</summary>
    public static Dictionary<string, string> OverrideFiles(IEnumerable<string> candidatePaths, string dataRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in candidatePaths.Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }
                var text = File.ReadAllText(path).Trim();
                result[Path.GetRelativePath(dataRoot, path)] = text.Length > MaxOverrideFileChars ? text[..MaxOverrideFileChars] : text;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return result;
    }

    /// <summary>Drops appCrash incidents of apps other than Nexus and the allowlisted vendor apps; every other incident source passes.</summary>
    public static IncidentsResponse FilterCrashes(IncidentsResponse response, IReadOnlyCollection<string> allowedProcessNames)
    {
        bool Keep(DiagnosticIncident i)
        {
            if (i.Source != DiagnosticEventCatalog.SourceAppCrash)
            {
                return true;
            }
            var name = ProcessName(i.App?.Path is { Length: > 0 } p ? p : i.App?.Name ?? "");
            return name.Contains("nexus", StringComparison.OrdinalIgnoreCase)
                || allowedProcessNames.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        }
        return response with { Incidents = response.Incidents.Where(Keep).ToList() };
    }

    // Windows paths use backslashes whatever the host OS, which Path.GetFileName only splits on Windows.
    private static string ProcessName(string pathOrName)
    {
        var file = pathOrName[(pathOrName.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
    }
}
