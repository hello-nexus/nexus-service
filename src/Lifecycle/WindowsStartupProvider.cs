using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

#if WINDOWS
using Microsoft.Win32;
#endif

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Per-user "start at logon" toggle for the Nexus user-session helper. The
/// daemon itself runs as a LocalSystem Windows Service from boot, so it
/// doesn't need a startup hook. This provider only controls whether the
/// helper companion (Nexus.exe --helper) auto-launches at sign-in.
///
/// Backed by <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\HelloNexus</c>.
/// HKCU is per-user and writable without elevation, so the dashboard can
/// flip the toggle on/off without UAC.
/// </summary>
public sealed class WindowsStartupProvider : IStartupProvider
{
#if WINDOWS
    private const string HkcuRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "HelloNexus";
    // Other products register a "Nexus" value too, so this name is only
    // touched when it launches our exe.
    private const string LegacyValueName = "Nexus";
#endif

    public bool IsEnabled()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
#if WINDOWS
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(HkcuRunKey, writable: false);
            if (key is null) return false;
            return key.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch { return false; }
#else
        return false;
#endif
    }

    public bool SetEnabled(bool enabled, string path, string arguments)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
#if WINDOWS
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(HkcuRunKey, writable: true);
            if (key is null) return false;
            RemoveOwnLegacyValue(key, path, enabled);

            if (enabled)
            {
                // path is the daemon's installed EXE. For helper autostart we
                // always want --helper mode regardless of any extra arguments
                // the caller passes.
                var command = $"\"{path}\" --helper";
                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch { return false; }
#else
        return false;
#endif
    }

#if WINDOWS
    /// <summary>Deletes the legacy value when it launches our exe; when enabling, Task Manager's enabled/disabled record moves to the new name. Never fails the caller's write.</summary>
    [SupportedOSPlatform("windows")]
    private static void RemoveOwnLegacyValue(RegistryKey run, string path, bool enabled)
    {
        try
        {
            if (run.GetValue(LegacyValueName) is not string command) return;
            if (!OwnNexusExe.IsOwnCommand(command, OwnNexusExe.Known(path))
                && !OwnNexusExe.HasOurPayload(Nexus.Service.Conflicts.ConflictAutostart.ExecutablePath(command)))
            {
                return;
            }
            run.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedRunKey, writable: true);
            if (approved?.GetValue(LegacyValueName) is byte[] record)
            {
                if (enabled) approved.SetValue(ValueName, record, RegistryValueKind.Binary);
                approved.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    private const string ServiceStartKey = @"SYSTEM\CurrentControlSet\Services\NexusService";

    /// <summary>
    /// Point the helper's sign-in Run key at <paramref name="exePath"/> when the
    /// service is set to start at boot, and remove it when the service is
    /// demand/disabled, so the per-user helper autostart mirrors the "start on
    /// boot" choice. The service start type (HKLM) is the single source of
    /// truth; a LocalSystem service cannot write the real user's HKCU, so this
    /// must run in the user context (the --helper and --sync-autostart entries).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void SyncHelperAutostart(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return;
        new WindowsStartupProvider().SetEnabled(ServiceStartsAtBoot(), exePath, string.Empty);
    }

    /// <summary>True when the service Start DWORD is 2 (auto). Defaults true if unreadable so a broken read never silently disables autostart.</summary>
    [SupportedOSPlatform("windows")]
    private static bool ServiceStartsAtBoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceStartKey, writable: false);
            return key?.GetValue("Start") is not int start || start == 2;
        }
        catch { return true; }
    }

    /// <summary>
    /// Delete the autostart Run key from LocalSystem's own hive (S-1-5-18). An
    /// older SYSTEM-context installer wrote it there via Registry.CurrentUser,
    /// where it never triggers a sign-in launch and points at a possibly-removed
    /// path. Guarded on the LocalSystem SID so a dev/console run as a real user
    /// never scrubs a legitimate key. Self-heals already-affected installs.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void ScrubSystemHiveAutostart()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (!id.IsSystem) return;
            using var key = Registry.CurrentUser.OpenSubKey(HkcuRunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        catch { }
    }
#endif
}
