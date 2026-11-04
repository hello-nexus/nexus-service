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
    private readonly List<(Regex Rule, string Replacement)> _rules = new();

    /// <param name="userNames">Every profile and account name: redacted in home paths (the generic path rule also catches names not listed), never as a bare word.</param>
    /// <param name="accountNames">Logon names that logs print outside paths. A distinctive one (a digit or separator in it) is redacted as a whole token anywhere; a word-like one (User, Owner, Dell) only in carrier contexts, in its real case: after "as ", "user=", as the value of an identity JSON key (user, account, owner, login) or as HOST\name for a known host. A bare word would corrupt diagnostics.</param>
    public BundleRedactor(IEnumerable<string> userNames, string machineName, IEnumerable<string>? otherHostNames = null, IEnumerable<string>? accountNames = null)
    {
        var hosts = new[] { machineName }.Concat(otherHostNames ?? Array.Empty<string>())
            .Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(h => h.Length).ToList();
        // AzureAD\\name and MicrosoftAccount\\name are how Windows prints Entra and Microsoft-account logons; WORKGROUP is the default domain of an unjoined PC.
        var hostAlternation = string.Join("|", hosts.Select(Regex.Escape).Concat(new[] { "AzureAD", "MicrosoftAccount", "WORKGROUP" }));
        foreach (var account in (accountNames ?? Array.Empty<string>()).Where(a => a.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var name in new[] { account, JsonEncodedText.Encode(account).ToString() }.Distinct())
            {
                var escaped = Regex.Escape(name);
                if (IsDistinctive(account))
                {
                    // A digit or separator makes it safe as a bare token anywhere, and after "as " in any case.
                    _paths.Add((" as " + name, " as <user>"));
                    _paths.Add(("user=" + name, "user=<user>"));
                    _users.Add(name);
                    continue;
                }
                // A word-like name (User, Admin, Owner) is also a common word, JSON value and folder name ("Source":"user", Chrome\User Data), so it
                // matches only where a logon is printed, in its real case: after "as " or "user=", as an identity JSON value, or as HOST\name for a known host.
                _rules.Add((new Regex(@"(?<= as )" + escaped + @"(?![\w\-])|(?<=user=)" + escaped + @"(?![\w\-])"
                    + @"|(?<=""(?i:user|userName|username|account|accountName|owner|login|logonUser)""\s*:\s*"")" + escaped + @"(?="")"
                    + @"|(?<=(?<![\w.\-])(?i:" + hostAlternation + @")\\{1,2})" + escaped + @"(?![\w\-\\/])",
                    RegexOptions.CultureInvariant), "<user>"));
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
        foreach (var host in hosts)
        {
            // The startup snapshot's host line and JSON name fields carry the machine name explicitly, so these are safe for any name.
            _paths.Add(("host: " + host, "host: <pc>"));
            var generic = IsGenericMachineName(host);
            foreach (var name in new[] { host, JsonEncodedText.Encode(host).ToString() }.Distinct())
            {
                var escaped = Regex.Escape(name);
                _rules.Add((new Regex("(\"(?:pcName|machineName)\"\\s*:\\s*\")" + escaped + "(\")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "$1<pc>$2"));
                if (generic)
                {
                    // Nexus, Windows, localhost and vendor names are also directories and URL hosts; only the explicit labels above carry them.
                    continue;
                }
                // A bare name is also a device or product word ("TITAN 360"), so only carrier contexts count. None may follow a path separator, which would hit a directory of that name.
                const string notAfterPathOrWord = @"(?<![\w.\-\\/:])";
                _rules.Add((new Regex(@"(?<=(?<![\w.\-:])\\\\)" + escaped + @"(?=\\)"
                    + "|" + notAfterPathOrWord + escaped + @"(?=\\{1,2}[\w.$<-])"
                    + "|" + notAfterPathOrWord + escaped + @"(?=\.(?:local|lan|home|localdomain)\b)"
                    + "|" + @"(?<=://)" + escaped + @"(?=[:/\s""]|$)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "<pc>"));
            }
            // A name made of one plain word is ambiguous; a distinctive one is safe to replace anywhere.
            if (!generic && IsDistinctive(host))
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

    private static bool IsDistinctive(string name) => !IsGenericMachineName(name) && name.Any(c => char.IsDigit(c) || c is '-' or '_');

    /// <summary>Too short or too common to replace inside free text without corrupting it; such a name is redacted only where it follows a host label.</summary>
    internal static bool IsGenericMachineName(string name) => name.Length < 4 || GenericMachineNames.Contains(name);

    public string Apply(string text)
    {
        foreach (var (needle, replacement) in _paths)
        {
            text = ReplaceBounded(text, needle, replacement, pathSegment: true);
        }
        foreach (var (rule, replacement) in _rules)
        {
            text = rule.Replace(text, replacement);
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

    /// <summary>The launchd plist written by MacStartupProvider points StandardOutPath and StandardErrorPath here; that file is macOS-only, so the paths live in this cross-platform file and the plist reads them.</summary>
    internal const string LaunchdStdoutPath = "/tmp/nexus-service.log";
    internal const string LaunchdStderrPath = "/tmp/nexus-service.err";

    internal static readonly string[] MacLaunchdLogPaths = { LaunchdStdoutPath, LaunchdStderrPath };

    /// <summary>The root system unit installer/linux/install.sh installs.</summary>
    internal const string JournalUnitName = Lifecycle.SystemdUnit.NexusUnitName;

    /// <summary>The adopted session user's home on a Linux root daemon; null elsewhere (LinuxSession is compiled only into Linux builds).</summary>
    internal static string? LinuxSessionHome()
    {
#if LINUX
        return Platform.Linux.LinuxSession.SessionHome;
#else
        return null;
#endif
    }

    internal const int MaxJournalChars = 4 * 1024 * 1024;
    internal const int JournalTimeoutMs = 15_000;

    /// <summary>Other vendors' Inno installers write the same "Setup Log" file name, so a log counts only when its head carries the Nexus AppId, an "Original Setup EXE:" line (timestamp-prefixed in a real log) naming Nexus-Setup or the web installer Nexus-Installer, or one of Nexus.iss's own "[nexus] " lines. The web installer's log is kept on purpose: it records the download and hash-check failures that stop an install before Nexus-Setup ever runs.</summary>
    internal static bool IsNexusInstallerLog(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var buffer = new char[InstallerLogProbeChars];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var head = new string(buffer, 0, read);
            if (head.Contains(InstallerAppId, StringComparison.OrdinalIgnoreCase) || head.Contains(" [nexus] ", StringComparison.Ordinal))
            {
                return true;
            }
            foreach (var line in head.Split('\n'))
            {
                if (line.Contains("Original Setup EXE:", StringComparison.OrdinalIgnoreCase)
                    && (line.Contains("Nexus-Setup", StringComparison.OrdinalIgnoreCase) || line.Contains("Nexus-Installer", StringComparison.OrdinalIgnoreCase)))
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

    /// <summary>Machine-wide temp folders: Temp, and SystemTemp where Windows 11 24H2 and later give SYSTEM its own.</summary>
    internal static string[] WindowsSystemTempDirectories(string? systemRoot)
    {
        var root = string.IsNullOrEmpty(systemRoot) ? @"C:\Windows" : systemRoot;
        return new[] { Path.Combine(root, "Temp"), Path.Combine(root, "SystemTemp") };
    }

    /// <summary>The launchd output files sit in world-writable /tmp, where another local user can plant a symlink or a file of their own; only regular, unlinked files owned by the service's own uid ship. <paramref name="run"/> runs a tool and returns its stdout.</summary>
    internal static List<string> TrustedLaunchdLogs(IEnumerable<string> paths, Func<string, string[], string> run)
    {
        var uid = run("/usr/bin/id", new[] { "-u" }).Trim();
        var trusted = new List<string>();
        if (uid.Length == 0)
        {
            return trusted;
        }
        foreach (var path in paths)
        {
            if (!File.Exists(path) || Lifecycle.UserDataSafety.IsReparsePoint(path))
            {
                continue;
            }
            // A hard link to a root-only file shows the root owner's uid, but a file created for us has a link count of 1.
            if (run("/usr/bin/stat", new[] { "-f", "%u %l", path }).Trim() == uid + " 1")
            {
                trusted.Add(path);
            }
        }
        return trusted;
    }

    /// <summary>The last 7 days of the Nexus systemd system unit (it runs as a root system unit, see LinuxStartupProvider), tail-capped; empty when journalctl is missing, fails or times out.</summary>
    internal static string ReadJournal(Func<string[], string> run)
    {
        try
        {
            var text = run(new[] { "-q", "-u", JournalUnitName, "--since", "7 days ago", "--no-pager", "-o", "short-iso", "-n", "50000" });
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
