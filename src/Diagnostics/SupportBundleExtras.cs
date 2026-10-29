using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Routes;

namespace Nexus.Service.Diagnostics;

/// <summary>Plain string replacement and filtering for bundle contents: usernames in home paths, the machine name, other apps' crashes, NEXUS_* environment.</summary>
public sealed class BundleRedactor
{
    private readonly List<(string Needle, string Replacement)> _rules = new();

    public BundleRedactor(IEnumerable<string> userNames, string machineName)
    {
        foreach (var user in userNames.Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // The escaped-backslash form covers paths inside JSON text.
            _rules.Add((@"\Users\" + user, @"\Users\<user>"));
            _rules.Add((@"\\Users\\" + user, @"\\Users\\<user>"));
            _rules.Add(("/Users/" + user, "/Users/<user>"));
            _rules.Add(("/home/" + user, "/home/<user>"));
        }
        if (machineName.Length >= 3)
        {
            _rules.Add((machineName, "<pc>"));
        }
    }

    public string Apply(string text)
    {
        foreach (var (needle, replacement) in _rules)
        {
            text = text.Replace(needle, replacement, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }
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
