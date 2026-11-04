using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private readonly List<Regex> _jsonNames = new();

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
        if (machineName.Length > 0)
        {
            // The startup snapshot's host line and JSON name fields carry the machine name explicitly, so these are safe for any name.
            _paths.Add(("host: " + machineName, "host: <pc>"));
            foreach (var name in new[] { machineName, JsonEncodedText.Encode(machineName).ToString() }.Distinct())
            {
                _jsonNames.Add(new Regex("(\"(?:pcName|machineName)\"\\s*:\\s*\")" + Regex.Escape(name) + "(\")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
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

    /// <summary>Too short or too common to replace inside free text without corrupting it; such a name is redacted only where it follows a host label.</summary>
    internal static bool IsGenericMachineName(string name) => name.Length < 4 || GenericMachineNames.Contains(name);

    public string Apply(string text)
    {
        foreach (var (needle, replacement) in _paths)
        {
            text = ReplaceBounded(text, needle, replacement, pathSegment: true);
        }
        foreach (var rule in _jsonNames)
        {
            text = rule.Replace(text, "$1<pc>$2");
        }
        foreach (var name in _machine)
        {
            text = ReplaceBounded(text, name, "<pc>", pathSegment: false);
        }
        return text;
    }

    // A path segment ends at any character that cannot be part of a name or the end; a machine name is a whole token, so letters, digits, '-' and '_' on either side keep it.
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

    private static bool IsSegmentEnd(char c) => !char.IsLetterOrDigit(c) && c is not ('_' or '-');

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';
}

public static class SupportBundleExtras
{
    internal const int MaxOverrideFileChars = 1024;

    internal const int InstallerLogKeep = 3;
    internal const string InstallerLogPattern = "Setup Log *.txt";
    private const int InstallerLogProbeChars = 1024 * 1024;

    /// <summary>The installer's AppId from installer/Nexus.iss; Inno writes it into the log as the uninstall key name.</summary>
    internal const string InstallerAppId = "8F2E3A4D-9C5B-4E7A-B1F8-3C2A5E9D0F12";

    /// <summary>Non-.log diagnostics that land in the logs dir. helper-disable.txt is not here: <see cref="OverrideFiles"/> already carries it.</summary>
    internal static readonly string[] ExtraLogPatterns = { "qseries-home-diag-*.txt", "lighting-bench-*.json", "lighting-bench-*.txt" };

    internal static bool IsExtraLogCandidate(string path) => !path.EndsWith(".child.json", StringComparison.OrdinalIgnoreCase);

    internal static readonly string[] MacLaunchdLogPaths = { Lifecycle.MacStartupProvider.LaunchdStdoutPath, Lifecycle.MacStartupProvider.LaunchdStderrPath };

    internal const int MaxJournalChars = 4 * 1024 * 1024;
    internal const int JournalTimeoutMs = 15_000;

    /// <summary>Other vendors' Inno installers write the same "Setup Log" file name, so a log counts only when its head carries the Nexus AppId or names Nexus-Setup as the original setup exe.</summary>
    internal static bool IsNexusInstallerLog(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var buffer = new char[InstallerLogProbeChars];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var head = new string(buffer, 0, read);
            if (head.Contains(InstallerAppId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            foreach (var line in head.Split('\n'))
            {
                if (line.StartsWith("Original Setup EXE:", StringComparison.OrdinalIgnoreCase) && line.Contains("Nexus-Setup", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The last 7 days of the Nexus systemd system unit (it runs as a root system unit, see LinuxStartupProvider), tail-capped; empty when journalctl is missing, fails or times out.</summary>
    internal static string ReadJournal(Func<string[], string> run)
    {
        try
        {
            var text = run(new[] { "-u", Lifecycle.LinuxStartupProvider.UnitName, "--since", "7 days ago", "--no-pager", "-o", "short-iso", "-n", "50000" });
            if (string.IsNullOrWhiteSpace(text))
            {
                return "";
            }
            return text.Length > MaxJournalChars
                ? $"[support-bundle] last {MaxJournalChars / (1024 * 1024)} MB of {text.Length} chars\n" + text[^MaxJournalChars..]
                : text;
        }
        catch (Exception)
        {
            return "";
        }
    }

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
