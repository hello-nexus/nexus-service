using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Service.Diagnostics.EventLog;
using Nexus.Service.Routes;

namespace Nexus.Service.Diagnostics;

/// <summary>Plain string replacement and filtering for bundle contents: user names in paths and as account names, host names where they carry a machine identity, other apps' crashes, NEXUS_* environment. MAC addresses, device serials and LAN IPs are deliberately left alone: they are diagnostic keys.</summary>
public sealed class BundleRedactor
{
    private static readonly HashSet<string> GenericMachineNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "NEXUS", "PC", "DESKTOP", "GAMING", "RYZEN", "INTEL", "AMD", "NVIDIA", "WINDOWS", "LAPTOP", "WORKSTATION", "SERVER",
        "HOME", "USER", "ADMIN", "LOCALHOST", "MACBOOK", "MSI", "ASUS", "HYTE", "CORSAIR", "COMPUTER", "BUILD", "TEST",
    };

    private readonly List<(string Needle, string Replacement)> _paths = new();
    private readonly List<string> _freeText = new();
    private readonly List<string> _users = new();
    private readonly List<Regex> _rules = new();

    /// <param name="userNames">Every profile and account name: redacted in home paths and after "as ", never as a bare word (a profile folder can be named like a word).</param>
    /// <param name="accountNames">Logon names that logs print bare (the console user, the service user): also redacted as whole tokens when distinctive.</param>
    public BundleRedactor(IEnumerable<string> userNames, string machineName, IEnumerable<string>? otherHostNames = null, IEnumerable<string>? accountNames = null)
    {
        foreach (var account in (accountNames ?? Array.Empty<string>()).Where(a => a.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var name in new[] { account, JsonEncodedText.Encode(account).ToString() }.Distinct())
            {
                // The helper bootstrapper logs the console logon name after "as", and a renamed account's name matches no profile folder.
                _paths.Add((" as " + name, " as <user>"));
                _paths.Add(("user=" + name, "user=<user>"));
                if (!IsGenericMachineName(account))
                {
                    _users.Add(name);
                }
            }
        }
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
                _paths.Add(("/media/" + name, "/media/<user>"));
                _paths.Add(("/run/media/" + name, "/run/media/<user>"));
            }
        }
        var hosts = new[] { machineName }.Concat(otherHostNames ?? Array.Empty<string>())
            .Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(h => h.Length).ToList();
        foreach (var host in hosts)
        {
            // The startup snapshot's host line and JSON name fields carry the machine name explicitly, so these are safe for any name.
            _paths.Add(("host: " + host, "host: <pc>"));
            foreach (var name in new[] { host, JsonEncodedText.Encode(host).ToString() }.Distinct())
            {
                var escaped = Regex.Escape(name);
                _rules.Add(new Regex("(\"(?:pcName|machineName)\"\\s*:\\s*\")" + escaped + "(\")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                // A bare name is also a device or product word ("TITAN 360"), so only carrier contexts count: DOMAIN\user, \\HOST\share, HOST.local and URL hosts.
                _rules.Add(new Regex(@"(?<![\w-])" + escaped + @"(?=\\{1,2}[\w.$-])|(?<![\w-])" + escaped + @"(?=\.(?:local|lan|home|localdomain)\b)|(?<=//)" + escaped + @"(?=[:/\s""]|$)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }
            // A name made of one plain word is ambiguous; a distinctive one (a digit or separator in it) is safe to replace anywhere.
            if (!IsGenericMachineName(host) && host.Any(c => char.IsDigit(c) || c is '-' or '_'))
            {
                _freeText.Add(host);
                var encoded = JsonEncodedText.Encode(host).ToString();
                if (encoded != host)
                {
                    _freeText.Add(encoded);
                }
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
        foreach (var rule in _rules)
        {
            text = rule.Replace(text, m => m.Groups.Count > 1 ? m.Groups[1].Value + "<pc>" + m.Groups[2].Value : "<pc>");
        }
        foreach (var name in _users)
        {
            text = ReplaceBounded(text, name, "<user>", pathSegment: false);
        }
        foreach (var name in _freeText)
        {
            text = ReplaceBounded(text, name, "<pc>", pathSegment: false);
        }
        // Account names this machine never reported still sit under a user directory.
        return Telemetry.ErrorReporter.RedactUserPaths(text);
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

/// <summary>Who the bundle must scrub: OS account names (home paths, account logons) and machine host names.</summary>
public sealed record BundleIdentity(IReadOnlyList<string> UserNames, IReadOnlyList<string> AccountNames, IReadOnlyList<string> HostNames);

public static class SupportBundleExtras
{
    private static readonly HashSet<string> NotAccountNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "root", "system", "administrator", "guest", "default", "public", "shared", "all users", "lost+found", "nobody",
    };

    /// <summary>Every name worth scrubbing. Names come from the process user, the Windows profiles, the interactive console logon (the helper bootstrapper logs it), the Linux session home (a root daemon runs as root) and the home directories on disk.</summary>
    internal static BundleIdentity CollectIdentity(string processUser, IEnumerable<string> windowsProfiles, string? consoleUser, string? linuxSessionHome,
        IEnumerable<string> homeDirectories, string machineName, string? dnsHostName)
    {
        var users = new List<string>();
        var accounts = new List<string>();
        void Account(string? name)
        {
            name = (name ?? "").Trim();
            if (name.Length > 0 && !NotAccountNames.Contains(name) && !name.EndsWith('$'))
            {
                accounts.Add(name);
                users.Add(name);
            }
        }
        Account(processUser);
        Account(consoleUser);
        if (!string.IsNullOrEmpty(linuxSessionHome))
        {
            Account(LastSegment(linuxSessionHome));
        }
        foreach (var profile in windowsProfiles)
        {
            var name = LastSegment(profile);
            if (!string.IsNullOrEmpty(name))
            {
                users.Add(name);
            }
        }
        foreach (var home in homeDirectories)
        {
            var name = LastSegment(home);
            if (!string.IsNullOrEmpty(name) && !NotAccountNames.Contains(name))
            {
                users.Add(name);
            }
        }
        var hosts = new List<string>();
        if (!string.IsNullOrWhiteSpace(dnsHostName) && !string.Equals(dnsHostName, machineName, StringComparison.OrdinalIgnoreCase))
        {
            hosts.Add(dnsHostName);
        }
        return new BundleIdentity(users, accounts, hosts);
    }

    /// <summary>True when a handler declares the VID/PID. IsConnected is not used: several handlers answer from live hub state whatever the probe holds, which would list every USB device.</summary>
    internal static bool IsKnownUsbId(IEnumerable<Devices.IDeviceHandler> handlers, int vendorId, int productId) =>
        handlers.Any(h => h.Identifiers.Any(i => i.VendorId == vendorId && i.ProductId == productId));

    /// <summary>The interactive console logon the helper bootstrapper logs; Windows only.</summary>
    internal static string? ConsoleUser()
    {
#if WINDOWS
        return Lifecycle.UserHelperBootstrapper.ResolveActiveConsoleUsername();
#else
        return null;
#endif
    }

    internal static string? TryDnsHostName()
    {
        try
        {
            return System.Net.Dns.GetHostName();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            return null;
        }
    }

    // Windows paths use backslashes whatever the host OS, which Path.GetFileName only splits on Windows.
    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        return trimmed[(trimmed.LastIndexOfAny(new[] { '\\', '/' }) + 1)..];
    }

    /// <summary>Immediate subdirectories of <paramref name="parent"/>; empty when it is missing or unreadable.</summary>
    internal static List<string> SubdirectoriesOrEmpty(string parent)
    {
        try
        {
            return Directory.Exists(parent) ? Directory.EnumerateDirectories(parent).ToList() : new List<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

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
