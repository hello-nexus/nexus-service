using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Nexus.Service.Models.Conflicts;

namespace Nexus.Service.Conflicts;

/// <summary>
/// Reads, and on an explicit user action disables, the entries that launch a
/// conflicting app at boot.
///
/// Scope is deliberately narrow: only apps carrying an <see
/// cref="ConflictAppDefinition.Autostart"/> recipe are handled at all, and a
/// recipe is only added for a mechanism confirmed on a real install, disabled,
/// and then observed absent across a reboot. Generic discovery was tried and
/// reverted: a partial match reported an app disabled while it still started
/// with Windows. Nothing here guesses - an app with no recipe gets no action.
///
/// Run entries are disabled the way Task Manager's Startup tab does it, by
/// writing the StartupApproved record Explorer consults, rather than by
/// deleting the vendor's Run value. That keeps the operation reversible from
/// a place users already know, and it survives the vendor app rewriting its
/// own Run value, which a deletion does not.
/// </summary>
public static class ConflictAutostart
{
    public const string KindRunKeyMachine = "runKeyMachine";
    public const string KindRunKeyUser = "runKeyUser";
    public const string KindService = "service";
    /// <summary>A Task Scheduler entry that launches the app at logon. Disabling deletes it, which is what the Nexus 2 migration path has always done.</summary>
    public const string KindScheduledTask = "scheduledTask";

    private const string UserRunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UserApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string MachineRunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string MachineApprovedPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>Service Start value for "Automatic"; 3 is "Manual", which is what disabling leaves behind so the app still runs when launched by hand.</summary>
    private const int ServiceStartAutomatic = 2;
    private const int ServiceStartManual = 3;

    /// <summary>Whether this app has a verified recipe at all - the only apps the SPA offers the action for.</summary>
    public static bool IsSupported(ConflictAppDefinition def)
        => OperatingSystem.IsWindows() && def.Autostart.Count > 0;

#if WINDOWS
    /// <summary>Every recipe entry that is currently ENABLED. Empty means the app has nothing left starting it at boot, so the action is already done (or was never on).</summary>
    [SupportedOSPlatform("windows")]
    public static List<ConflictAutostartEntry> Find(ConflictAppDefinition def)
    {
        var found = new List<ConflictAutostartEntry>();
        foreach (var target in def.Autostart)
        {
            if (target.Kind == KindService)
            {
                if (ServiceStartValue(target.Name) == ServiceStartAutomatic)
                {
                    found.Add(new ConflictAutostartEntry { Kind = KindService, EntryName = target.Name });
                }
                continue;
            }

            if (target.Kind == KindScheduledTask)
            {
                if (ScheduledTaskExists(target.Name))
                {
                    found.Add(new ConflictAutostartEntry { Kind = KindScheduledTask, EntryName = target.Name });
                }
                continue;
            }

            var hive = HiveFor(target.Kind);
            if (hive is null) continue;
            // A Run value can be armed while the app still declines to start, so
            // an entry carrying a vendor gate is only live when that agrees.
            if (!VendorSaysItStarts(target)) continue;
            foreach (var valueName in MatchingRunValues(hive.Value, target))
            {
                if (RunEntryEnabled(hive.Value, valueName))
                {
                    found.Add(new ConflictAutostartEntry { Kind = target.Kind, EntryName = valueName });
                }
            }
        }
        return found;
    }

    /// <summary>Disables every entry and returns how many were verified disabled by a re-read. Only ever reached from an explicit per-app user action.</summary>
    [SupportedOSPlatform("windows")]
    public static int Disable(ConflictAppDefinition def, IEnumerable<ConflictAutostartEntry> entries)
    {
        var disabled = 0;
        foreach (var entry in entries)
        {
            if (Disable(def, entry)) disabled++;
        }
        return disabled;
    }

    [SupportedOSPlatform("windows")]
    private static bool Disable(ConflictAppDefinition def, ConflictAutostartEntry entry)
    {
        // Re-check the entry against the recipe rather than trusting the name
        // handed in: a request can only ever act on something this app's own
        // recipe named, never on an arbitrary Run value or service.
        if (!InRecipe(def, entry))
        {
            // Reached when the entry stopped resolving between the read and the
            // click - a vendor flag flipped, or the console profile went away.
            // Nothing is wrong with the machine, so leave a line rather than a
            // silent "disabled 0 of 1".
            Console.Error.WriteLine($"[conflicts] autostart entry no longer resolves for {def.Id}: {entry.Kind}:{entry.EntryName}");
            return false;
        }
        try
        {
            if (entry.Kind == KindScheduledTask)
            {
                // Same call the Nexus 2 migration path has always used.
                Nexus.Service.Platform.ShellExecutor.RunExit("schtasks.exe", 10000, "/Delete", "/TN", entry.EntryName, "/F");
                return !ScheduledTaskExists(entry.EntryName);
            }

            if (entry.Kind == KindService)
            {
                using (var key = Registry.LocalMachine.OpenSubKey($@"{ServicesPath}\{entry.EntryName}", writable: true))
                {
                    if (key is null) return false;
                    key.SetValue("Start", ServiceStartManual, RegistryValueKind.DWord);
                }
                return ServiceStartValue(entry.EntryName) == ServiceStartManual;
            }

            var hive = HiveFor(entry.Kind);
            if (hive is null) return false;
            using (var approved = hive.Value.Root.CreateSubKey(hive.Value.ApprovedPath, writable: true))
            {
                if (approved is null) return false;
                approved.SetValue(entry.EntryName, DisabledRecord(), RegistryValueKind.Binary);
            }
            return !RunEntryEnabled(hive.Value, entry.EntryName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[conflicts] autostart disable failed for {entry.Kind}:{entry.EntryName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Whether the recipe for this app can produce this exact entry - the name for a service, a Run value still resolving to a recipe executable otherwise.</summary>
    [SupportedOSPlatform("windows")]
    private static bool InRecipe(ConflictAppDefinition def, ConflictAutostartEntry entry)
    {
        foreach (var target in def.Autostart)
        {
            if (target.Kind != entry.Kind) continue;
            if (target.Kind == KindService)
            {
                if (string.Equals(target.Name, entry.EntryName, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }
            if (target.Kind == KindScheduledTask)
            {
                if (string.Equals(target.Name, entry.EntryName, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }
            var hive = HiveFor(target.Kind);
            if (hive is null) continue;
            if (!VendorSaysItStarts(target)) continue;
            foreach (var valueName in MatchingRunValues(hive.Value, target))
            {
                if (string.Equals(valueName, entry.EntryName, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether the vendor's own settings file agrees the app starts at boot.
    /// True when the target declares no gate. An unreadable file, a missing
    /// value, or anything but "true" reads as "will not start", so a gate that
    /// stops matching a future version silently withdraws the action rather
    /// than offering one on a guess.
    ///
    /// Measured, not assumed: iCUE's Run value is identical on both lab boxes,
    /// and only config.cuecfg's StartOnStartup tracked whether iCUE actually
    /// came up after a reboot.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool VendorSaysItStarts(ConflictAutostartTarget target)
    {
        var hasPath = target.VendorConfigAppDataPath.Length > 0;
        var hasValue = target.VendorConfigXmlValue.Length > 0;
        if (!hasPath && !hasValue) return true;
        // Half a gate is a mistake in the recipe, not an ungated entry.
        if (!hasPath || !hasValue) return false;
        try
        {
            // The service runs as LocalSystem, whose own AppData is the SYSTEM
            // profile; the setting lives in the interactive user's.
            var profile = Nexus.Service.Lifecycle.ConsoleUserSid.ResolveProfilePath();
            if (string.IsNullOrEmpty(profile)) return false;
            var path = Path.Combine(profile!, "AppData", "Roaming", target.VendorConfigAppDataPath);
            if (!File.Exists(path)) return false;
            return XmlValueIsTrue(File.ReadAllText(path), target.VendorConfigXmlValue);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether a logon/boot task of this name is registered. Mirrors the Nexus 2 migration probe: the task's file under System32\Tasks is the marker.</summary>
    [SupportedOSPlatform("windows")]
    private static bool ScheduledTaskExists(string taskName)
    {
        try
        {
            return File.Exists(Path.Combine(Environment.SystemDirectory, "Tasks", taskName));
        }
        catch { return false; }
    }

    /// <summary>Run value names in this hive whose command resolves to one of the recipe's executables.</summary>
    [SupportedOSPlatform("windows")]
    private static List<string> MatchingRunValues(Hive hive, ConflictAutostartTarget target)
    {
        var names = new List<string>();
        try
        {
            using var key = hive.Root.OpenSubKey(hive.RunPath);
            if (key is null) return names;
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string command
                    || string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }
                // Matched on the executable's own file name, never on the
                // value name: the value name is vendor- and version-stamped
                // (iCUE runs from a value called "Corsair iCUE5 Software")
                // and substring-matching it collides with unrelated entries.
                if (MatchesExecutable(command, target.ExeNames)) names.Add(name);
            }
        }
        catch { /* an unreadable hive offers nothing rather than a guess */ }
        return names;
    }

    /// <summary>Whether an entry is on: an absent or even-flagged StartupApproved record means enabled, which is also the state of an entry Task Manager has never touched.</summary>
    [SupportedOSPlatform("windows")]
    private static bool RunEntryEnabled(Hive hive, string valueName)
    {
        try
        {
            using var approved = hive.Root.OpenSubKey(hive.ApprovedPath);
            if (approved?.GetValue(valueName) is not byte[] record || record.Length == 0) return true;
            return (record[0] & 1) == 0;
        }
        catch { return true; }
    }

    /// <summary>The record Task Manager writes for a disabled item: an odd flag DWORD followed by the FILETIME it was disabled at.</summary>
    private static byte[] DisabledRecord()
    {
        var record = new byte[12];
        record[0] = 3;
        BitConverter.TryWriteBytes(record.AsSpan(4), DateTime.UtcNow.ToFileTimeUtc());
        return record;
    }

    [SupportedOSPlatform("windows")]
    private static int ServiceStartValue(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ServicesPath}\{serviceName}");
            return key?.GetValue("Start") is int start ? start : -1;
        }
        catch { return -1; }
    }

    /// <summary>Where one Run kind lives. The user hive resolves through the console user's SID, since the service's own HKCU is the SYSTEM profile.</summary>
    private readonly record struct Hive(RegistryKey Root, string RunPath, string ApprovedPath);

    [SupportedOSPlatform("windows")]
    private static Hive? HiveFor(string kind)
    {
        switch (kind)
        {
            case KindRunKeyMachine:
                return new Hive(Registry.LocalMachine, MachineRunPath, MachineApprovedPath);
            case KindRunKeyUser:
                var sid = Nexus.Service.Lifecycle.ConsoleUserSid.Resolve(UserRunPath);
                return sid is null ? null : new Hive(Registry.Users, $@"{sid}\{UserRunPath}", $@"{sid}\{UserApprovedPath}");
            default:
                return null;
        }
    }
#else
    /// <summary>Autostart handling is Windows-only; every other platform resolves nothing and disables nothing.</summary>
    public static List<ConflictAutostartEntry> Find(ConflictAppDefinition def) => new();

    public static int Disable(ConflictAppDefinition def, IEnumerable<ConflictAutostartEntry> entries) => 0;
#endif

    /// <summary>
    /// Whether a <c>&lt;value name="NAME"&gt;true&lt;/value&gt;</c> element says true.
    /// The vendor writes a flat XML config, so this reads the one element rather
    /// than parsing the document.
    /// </summary>
    internal static bool XmlValueIsTrue(string xml, string valueName)
    {
        var needle = $"<value name=\"{valueName}\">";
        var at = xml.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;
        var start = at + needle.Length;
        var end = xml.IndexOf('<', start);
        if (end < 0) return false;
        return xml.AsSpan(start, end - start).Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a Run command line launches one of the named executables.</summary>
    internal static bool MatchesExecutable(string command, IReadOnlyList<string> exeNames)
    {
        var exe = ExecutablePath(command);
        if (exe.Length == 0) return false;
        var cut = exe.LastIndexOf('\\');
        var fileName = cut >= 0 ? exe.Substring(cut + 1) : exe;
        foreach (var name in exeNames)
        {
            if (string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>The executable out of a Run command line, dropping quotes and arguments.</summary>
    internal static string ExecutablePath(string command)
    {
        var value = command.Trim().Replace('/', '\\');
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value.Substring(1, end - 1) : value.Trim('"');
        }
        // An unquoted path may still contain spaces, so cut at the extension
        // rather than at the first space.
        var exe = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? value.Substring(0, exe + 4) : value;
    }
}
