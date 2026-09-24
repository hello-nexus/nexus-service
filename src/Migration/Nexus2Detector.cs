using System.IO;
using System.Threading.Tasks;
#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;
using Nexus.Service.Lifecycle;
#endif

namespace Nexus.Service.Migration;

/// <summary>Detection result for a legacy HYTE Nexus (Nexus 2) install.
/// Detected means present now; ImportAvailable and AutostartTaskPresent are
/// leftovers that outlive an uninstall. InstallLocation is the ARP-reported
/// install root, scoping the close-app OpenRGB kill to Nexus 2's own copy.</summary>
public sealed record Nexus2DetectionResult(
    bool Detected, bool ImportAvailable, string? Version, bool AutostartTaskPresent,
    bool Running = false, string? InstallLocation = null)
{
    public static readonly Nexus2DetectionResult None = new(false, false, null, false);
}

/// <summary>Turns the raw probes into a result. Cross-platform (the probes
/// themselves are Windows-only) so the presence-vs-leftover rule stays under
/// test.</summary>
internal static class Nexus2DetectionRules
{
    /// <summary>Nexus 2 is present when its uninstall entry is live or one of
    /// its processes runs. Importable config data and a registered autostart
    /// task survive an uninstall, so neither implies presence.</summary>
    public static Nexus2DetectionResult Compose(
        bool installed, bool running, bool importAvailable, bool autostartTaskPresent,
        string? version, string? installLocation) =>
        new(installed || running, importAvailable, version, autostartTaskPresent, running, installLocation);

    /// <summary>An uninstall entry counts only while the install directory it
    /// records is still on disk; a partial uninstall can leave the entry
    /// behind. An entry recording no location is taken at face value.</summary>
    public static bool UninstallEntryIsLive(string? installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
        {
            return true;
        }
        try { return Directory.Exists(installLocation); } catch { return false; }
    }
}

/// <summary>What the silent uninstall runs, from the ARP values. Pure so the
/// command shaping and the trust rules stay under test; the run itself is
/// Windows-only.</summary>
internal static class Nexus2UninstallRules
{
    public const string UninstallerFileName = "Uninstall HYTE Nexus.exe";
    public const string InstallDirName = "HYTE Nexus";
    // Subject CNs Nexus 2 has shipped under: HYTE's parent company on current
    // builds, HYTE itself on older ones. Exact, so a look-alike CN under any
    // trusted CA is not enough.
    private static readonly string[] TrustedSignerNames = { "American Future Technology Corp.", "HYTE" };
    // Where electron-builder puts the product: the per-user and the
    // all-users install roots. Anywhere else is a copy.
    private static readonly string[] InstallParents =
    {
        Path.Combine("AppData", "Local", "Programs"),
        "Program Files",
    };

    /// <summary>The values come from the console user's own hive and the exe
    /// gets that user's elevated token without a prompt: only
    /// electron-builder's fixed uninstaller name, inside the product's own
    /// directory, under one of its install roots, is accepted.</summary>
    public static bool IsUninstallerPath(string exe)
    {
        try
        {
            var full = Path.GetFullPath(exe);
            var dir = Path.GetDirectoryName(full) ?? "";
            var parent = Path.GetDirectoryName(dir) ?? "";
            if (!string.Equals(Path.GetFileName(full), UninstallerFileName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(dir), InstallDirName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            foreach (var root in InstallParents)
            {
                if (parent.EndsWith(Path.DirectorySeparatorChar + root, StringComparison.OrdinalIgnoreCase)
                    || parent.EndsWith(Path.AltDirectorySeparatorChar + root, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>Exact match on the signer's common name.</summary>
    public static bool IsTrustedSigner(string commonName)
    {
        foreach (var name in TrustedSignerNames)
        {
            if (string.Equals(commonName, name, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Exe plus the RAW argument string (never an ArgumentList: NSIS
    /// takes everything after <c>_?=</c> verbatim and rejects quotes around
    /// it). Only the install-mode switch is taken from the hive; <c>/S</c> and
    /// <c>_?=root</c> are always appended: silent, and run in place instead of
    /// from a temp copy so the process waited on is the one doing the work (it
    /// then cannot delete its own exe, which the caller sweeps). Root is the
    /// uninstaller's own directory, never the recorded InstallLocation: the
    /// sweep deletes it recursively, and the ARP value is user-writable (and
    /// empty on the builds in the field).</summary>
    public static (string Exe, string Arguments, string Root)? Compose(string? quietUninstallString, string? uninstallString)
    {
        var command = !string.IsNullOrWhiteSpace(quietUninstallString) ? quietUninstallString
            : !string.IsNullOrWhiteSpace(uninstallString) ? uninstallString
            : null;
        if (command is null)
        {
            return null;
        }
        var (exe, args) = Lifecycle.UserSessionTaskXml.SplitCommand(command);
        if (!IsUninstallerPath(exe))
        {
            return null;
        }
        var mode = args.Contains("/allusers", StringComparison.OrdinalIgnoreCase) ? "/allusers " : "/currentuser ";
        var root = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        return (exe, $"{mode}/S _?={root}", root);
    }
}

/// <summary>
/// Detects a legacy HYTE Nexus (Nexus 2) install and offers coexistence
/// actions. Nexus 2 was Windows-only, so the non-Windows implementation is a
/// stub. Every check in <see cref="Detect"/> is read-only and independent -
/// one failing (missing key, unloaded hive, locked file) never blocks the
/// others.
/// </summary>
public interface INexus2Detector
{
    Nexus2DetectionResult Detect();

    /// <summary>Deletes the Nexus 2 autostart scheduled task. Returns true when
    /// the task was deleted or was already absent (idempotent).</summary>
    bool DisableAutostart();

    /// <summary>Closes a running Nexus 2: graceful CloseMainWindow first (the
    /// AW5 failure log entry - hard-killing a process that owns a device HID
    /// can wedge the hardware until reboot), then kills any survivors plus
    /// Nexus 2's own bundled OpenRGB.exe. Idempotent: true when nothing ends
    /// up running, including when nothing was running to begin with.</summary>
    Task<bool> CloseAppAsync();

    /// <summary>Silently uninstalls Nexus 2 through its own NSIS uninstaller,
    /// after <see cref="CloseAppAsync"/>. Runs in the console user's session
    /// with the user's elevated token (a Task Scheduler one-shot, no UAC
    /// prompt for an administrator), and only when the uninstaller's path and
    /// Authenticode signer pass <see cref="Nexus2UninstallRules"/>. True when
    /// no live install remains afterwards, including when none was there to
    /// begin with; false while another run is in flight.</summary>
    Task<bool> UninstallAsync();
}

#if WINDOWS
public sealed class Nexus2Detector : INexus2Detector
{
    // electron-builder UUIDv5 of Nexus 2's appId com.hyte.desktop.
    private const string UninstallGuid = "95721908-4b6d-50cf-8ca2-e0db9e34d737";
    // Existence gate for ConsoleUserSid.Resolve - always present on any real
    // profile, unlike the GUID subkey which only exists when Nexus 2 is installed.
    private const string HkuUninstallSubPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string HkuUninstallKeyPath = HkuUninstallSubPath + @"\" + UninstallGuid;
    private const string HklmUninstall64Path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallGuid;
    private const string HklmUninstall32Path = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + UninstallGuid;
    private const string TaskName = "HYTE Nexus";
    private const string ConfigJsonRelativePath = @"AppData\Roaming\HYTE Nexus\config.json";
    // The Electron main process; the one that owns a top-level window CloseMainWindow can target.
    private const string MainProcessName = "HYTE Nexus";
    private static readonly string[] ProcessNames = { "HYTE Nexus", "HYTE.Nexus.Service" };

    private string _lastLoggedSignals = "";

    public Nexus2DetectionResult Detect()
    {
        var arp = CheckArpUninstallKey();
        var result = Nexus2DetectionRules.Compose(
            installed: arp.Live,
            running: CheckProcessRunning(),
            importAvailable: ResolveExistingProfileFile(ConfigJsonRelativePath) is not null,
            autostartTaskPresent: ScheduledTaskFileExists(),
            version: arp.Version,
            installLocation: arp.InstallLocation);

        LogSignalsWhenChanged(result, arp.Found);
        return result;
    }

    // Which probes fired, so a "Nexus 2 detected but it is not installed"
    // report names its own cause. Logged on change, not per poll.
    private void LogSignalsWhenChanged(Nexus2DetectionResult result, bool uninstallEntryFound)
    {
        if (!uninstallEntryFound && !result.Detected && !result.ImportAvailable && !result.AutostartTaskPresent)
        {
            return;
        }

        var line = $"[nexus2] detected={result.Detected} uninstallEntry={uninstallEntryFound} "
            + $"installDir={result.InstallLocation ?? "(none)"} running={result.Running} "
            + $"configData={result.ImportAvailable} autostartTask={result.AutostartTaskPresent}";
        if (Interlocked.Exchange(ref _lastLoggedSignals, line) != line)
        {
            Console.Error.WriteLine(line);
        }
    }

    public bool DisableAutostart()
    {
        if (!ScheduledTaskFileExists())
        {
            return true;
        }
        return RunSchtasksDelete();
    }

    public async Task<bool> CloseAppAsync()
    {
        try
        {
            await CloseGracefullyThenKillAsync(Process.GetProcessesByName(MainProcessName), TimeSpan.FromSeconds(10));

            foreach (var name in ProcessNames)
            {
                KillSurvivors(Process.GetProcessesByName(name));
            }

            // Nexus's own bundled OpenRGB owns device HID too, so it gets the
            // same graceful-first treatment as the main process (the AW5
            // failure-log entry: a hard kill mid-transaction can wedge the
            // hardware until reboot).
            await CloseGracefullyThenKillAsync(BundledOpenRgbProcesses(), TimeSpan.FromSeconds(5));

            var stillRunning = CheckProcessRunning();
            var leftoverOpenRgb = BundledOpenRgbProcesses();
            KillSurvivors(leftoverOpenRgb); // disposes; nothing left alive to kill at this point
            return !stillRunning && leftoverOpenRgb.Length == 0;
        }
        catch
        {
            return false;
        }
    }

    // Nexus 2's uninstaller does its own taskkill sweep plus RmDir /r; a
    // slow disk or a wedged HYTE.Nexus.Service can hold it for a while.
    private static readonly TimeSpan UninstallTimeout = TimeSpan.FromMinutes(3);
    private readonly SemaphoreSlim _uninstallGate = new(1, 1);

    public async Task<bool> UninstallAsync()
    {
        // A second POST while one run is in flight would start a second
        // uninstaller against a half-removed tree.
        if (!await _uninstallGate.WaitAsync(0))
        {
            return false;
        }
        try
        {
            return await UninstallOnceAsync();
        }
        finally
        {
            _uninstallGate.Release();
        }
    }

    private async Task<bool> UninstallOnceAsync()
    {
        var arp = CheckArpUninstallKey();
        if (!arp.Live)
        {
            return !CheckProcessRunning();
        }
        var command = Nexus2UninstallRules.Compose(arp.QuietUninstallString, arp.UninstallString);
        if (command is null)
        {
            Console.Error.WriteLine($"[nexus2] uninstall: refused ARP uninstall command '{arp.QuietUninstallString ?? arp.UninstallString ?? "(none)"}'");
            return false;
        }

        // Graceful close first: the uninstaller's own sweep is a hard taskkill,
        // which on a process holding device HID can wedge the hardware.
        await CloseAppAsync();

        var (exe, arguments, root) = command.Value;
        // Checked after the close so the file checked is the file run.
        if (!IsTrustedUninstaller(exe))
        {
            return false;
        }
        var exit = await RunUninstallerInUserSessionAsync(exe, arguments);
        Console.Error.WriteLine($"[nexus2] uninstall: \"{exe}\" {arguments} -> exit {exit}");
        if (exit != 0)
        {
            return false;
        }

        // Run in place, the uninstaller cannot remove its own exe or its
        // directory; everything else under the root is already gone.
        SweepInstallRoot(root, exe);
        // The uninstaller removes its own ARP entry and task; re-done here
        // because Live is what the next Detect() reads.
        if (arp.UserSid is not null)
        {
            DeleteUserArpEntry(arp.UserSid);
        }
        DisableAutostart();

        var after = CheckArpUninstallKey();
        return !after.Live && !CheckProcessRunning();
    }

    // The path came from a user-writable hive and gets the user's elevated
    // token without a prompt: it must carry a valid Authenticode chain from a
    // signer Nexus 2 ships under.
    private static bool IsTrustedUninstaller(string exe)
    {
        try
        {
            if (!File.Exists(exe))
            {
                Console.Error.WriteLine("[nexus2] uninstall: uninstaller missing");
                return false;
            }
            using var cert = Nexus.Service.Platform.Windows.AuthenticodeSigner.TryGetSignerCertificate(exe);
            var signer = cert?.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, forIssuer: false);
            if (signer is null || !Nexus2UninstallRules.IsTrustedSigner(signer))
            {
                Console.Error.WriteLine($"[nexus2] uninstall: signer not trusted ({cert?.Subject ?? "unsigned"})");
                return false;
            }
            // No revocation fetch: a box without internet must still be able
            // to uninstall, and the signature itself is what proves origin.
            Update.UpdateIntegrity.VerifyTrustChain(exe, revocation: false);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[nexus2] uninstall: signature check failed ({ex.Message})");
            return false;
        }
    }

    private const string UninstallerProcessName = "Uninstall HYTE Nexus";
    private const string UninstallTaskPrefix = "NexusUninstallNexus2_";
    private static readonly TimeSpan UninstallLaunchTimeout = TimeSpan.FromSeconds(20);

    // Runs in the console user's session with the user's elevated token, the
    // way Apps & Features runs it: as LocalSystem in session 0 the NSIS
    // uninstaller dies with STATUS_ACCESS_VIOLATION before doing anything.
    // Task Scheduler reports nothing back about the process it starts, so the
    // uninstaller process itself is what gets awaited.
    private static async Task<int> RunUninstallerInUserSessionAsync(string exe, string arguments)
    {
        var username = UserHelperBootstrapper.ResolveActiveConsoleUsername();
        if (string.IsNullOrEmpty(username))
        {
            Console.Error.WriteLine("[nexus2] uninstall: no console user to run the uninstaller as");
            return -1;
        }
        var taskName = $"{UninstallTaskPrefix}{Environment.ProcessId}_{DateTime.UtcNow.Ticks}";
        string? xmlPath = null;
        var registered = false;
        try
        {
            xmlPath = UserSessionTaskXml.WriteTempFile(
                UserSessionTaskXml.Build(username, $"\"{exe}\" {arguments}", elevated: true));
            if (Schtasks("/Create", "/TN", taskName, "/XML", xmlPath, "/F") != 0)
            {
                return -1;
            }
            registered = true;
            var before = LivePids(UninstallerProcessName);
            if (Schtasks("/Run", "/TN", taskName) != 0)
            {
                return -1;
            }
            using var proc = await WaitForNewProcessAsync(UninstallerProcessName, before, UninstallLaunchTimeout);
            if (proc is null)
            {
                Console.Error.WriteLine("[nexus2] uninstall: the task ran but no uninstaller process appeared (limited token?)");
                return -1;
            }
            using var cts = new CancellationTokenSource(UninstallTimeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine($"[nexus2] uninstall: timed out after {UninstallTimeout.TotalSeconds:F0}s");
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return -2;
            }
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[nexus2] uninstall: could not run the uninstaller: {ex.Message}");
            return -1;
        }
        finally
        {
            if (xmlPath is not null)
            {
                try { File.Delete(xmlPath); } catch { /* best-effort */ }
            }
            if (registered)
            {
                Schtasks("/Delete", "/TN", taskName, "/F");
            }
        }
    }

    private static int Schtasks(params string[] args)
    {
        var exit = Nexus.Service.Platform.ShellExecutor.RunExit("schtasks.exe", 10000, args);
        if (exit != 0)
        {
            Console.Error.WriteLine($"[nexus2] uninstall: schtasks {args[0]} exit {exit}");
        }
        return exit;
    }

    private static HashSet<int> LivePids(string processName)
    {
        var pids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(processName))
        {
            pids.Add(p.Id);
            p.Dispose();
        }
        return pids;
    }

    private static async Task<Process?> WaitForNewProcessAsync(string processName, HashSet<int> before, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Process? found = null;
            foreach (var p in Process.GetProcessesByName(processName))
            {
                if (found is null && !before.Contains(p.Id))
                {
                    found = p;
                    continue;
                }
                p.Dispose();
            }
            if (found is not null)
            {
                return found;
            }
            await Task.Delay(250);
        }
        return null;
    }

    private static void SweepInstallRoot(string root, string exe)
    {
        try { if (File.Exists(exe)) File.Delete(exe); } catch { /* best-effort */ }
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[nexus2] uninstall: install root left behind ({ex.Message})");
        }
    }

    private static void DeleteUserArpEntry(string sid)
    {
        try
        {
            using var parent = Registry.Users.OpenSubKey($@"{sid}\{HkuUninstallSubPath}", writable: true);
            parent?.DeleteSubKeyTree(UninstallGuid, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[nexus2] uninstall: user ARP entry left behind ({ex.Message})");
        }
    }

    private static async Task CloseGracefullyThenKillAsync(Process[] processes, TimeSpan gracePeriod)
    {
        using (var cts = new CancellationTokenSource(gracePeriod))
        {
            foreach (var proc in processes)
            {
                try { if (!proc.HasExited) proc.CloseMainWindow(); }
                catch { /* per-process swallow */ }
            }
            foreach (var proc in processes)
            {
                try { await proc.WaitForExitAsync(cts.Token); }
                catch { /* timed out or already gone; killed as a survivor below */ }
            }
        }
        KillSurvivors(processes);
    }

    private static void KillSurvivors(Process[] processes)
    {
        foreach (var proc in processes)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* per-process swallow */ }
            finally { proc.Dispose(); }
        }
    }

    // Only an OpenRGB.exe whose main module lives under Nexus 2's own install
    // root - never a user's standalone OpenRGB or Nexus's own copy.
    private static Process[] BundledOpenRgbProcesses()
    {
        var root = ResolveInstallRootPrefix();
        if (root is null)
        {
            return Array.Empty<Process>();
        }
        var matches = new List<Process>();
        foreach (var proc in Process.GetProcessesByName("OpenRGB"))
        {
            try
            {
                var modulePath = proc.MainModule?.FileName;
                if (modulePath is not null && modulePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(proc);
                    continue;
                }
            }
            catch { /* inaccessible module: not confirmed ours, leave it running */ }
            proc.Dispose();
        }
        return matches.ToArray();
    }

    private static string? ResolveInstallRootPrefix()
    {
        var installLocation = CheckArpUninstallKey().InstallLocation;
        if (string.IsNullOrEmpty(installLocation))
        {
            return null;
        }
        return Path.GetFullPath(installLocation).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
    }

    /// <summary>The ARP entry as found. <see cref="UserSid"/> is set when the
    /// live entry sits in the console user's hive (a <c>/currentuser</c>
    /// install), so the post-run sweep knows which hive to re-check.</summary>
    private sealed record ArpEntry(
        bool Found, bool Live, string? Version, string? InstallLocation,
        string? QuietUninstallString, string? UninstallString, string? UserSid);

    /// <summary>Live is true once an entry whose own recorded location still
    /// exists is found; the first such entry supplies Version and
    /// InstallLocation, so a stale per-user entry cannot veto a live
    /// machine-wide one.</summary>
    private static ArpEntry CheckArpUninstallKey()
    {
        var found = false;
        var live = false;
        string? version = null;
        string? installLocation = null;
        string? quietUninstall = null;
        string? uninstall = null;
        string? userSid = null;

        void Fold(RegistryKey? key, string? sid)
        {
            if (key is null)
            {
                return;
            }
            found = true;
            if (live)
            {
                return;
            }
            var location = key.GetValue("InstallLocation") as string;
            if (!Nexus2DetectionRules.UninstallEntryIsLive(location))
            {
                return;
            }
            live = true;
            version = key.GetValue("DisplayVersion") as string;
            installLocation = location;
            quietUninstall = key.GetValue("QuietUninstallString") as string;
            uninstall = key.GetValue("UninstallString") as string;
            userSid = sid;
        }

        try
        {
            var sid = ConsoleUserSid.Resolve(HkuUninstallSubPath);
            if (sid is not null)
            {
                using var key = Registry.Users.OpenSubKey($@"{sid}\{HkuUninstallKeyPath}");
                Fold(key, sid);
            }
        }
        catch { /* per-check swallow */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(HklmUninstall64Path);
            Fold(key, null);
        }
        catch { /* per-check swallow */ }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(HklmUninstall32Path);
            Fold(key, null);
        }
        catch { /* per-check swallow */ }

        return new ArpEntry(found, live, version, installLocation, quietUninstall, uninstall, userSid);
    }

    private static bool ScheduledTaskFileExists()
    {
        try
        {
            return File.Exists(Path.Combine(Environment.SystemDirectory, "Tasks", TaskName));
        }
        catch
        {
            return false;
        }
    }

    private static bool CheckProcessRunning()
    {
        try
        {
            foreach (var name in ProcessNames)
            {
                var procs = Process.GetProcessesByName(name);
                var any = procs.Length > 0;
                foreach (var p in procs)
                {
                    p.Dispose();
                }
                if (any)
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveExistingProfileFile(string relativePath)
    {
        try
        {
            foreach (var profileDir in Nexus2ProfileDirs.Candidates())
            {
                var candidate = Path.Combine(profileDir, relativePath);
                if (SafeFileExists(candidate) && SafeFileLength(candidate) > 0)
                {
                    return candidate;
                }
            }
        }
        catch { /* per-check swallow */ }
        return null;
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    private static long SafeFileLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static bool RunSchtasksDelete() =>
        Nexus.Service.Platform.ShellExecutor.RunExit("schtasks.exe", 10000, "/Delete", "/TN", TaskName, "/F") == 0;
}
#else
public sealed class Nexus2Detector : INexus2Detector
{
    public Nexus2DetectionResult Detect() => Nexus2DetectionResult.None;

    public bool DisableAutostart() => false;

    public Task<bool> CloseAppAsync() => Task.FromResult(false);

    public Task<bool> UninstallAsync() => Task.FromResult(false);
}
#endif
