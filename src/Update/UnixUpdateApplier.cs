using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Nexus.Service.Lifecycle;
using Nexus.Service.Platform;

namespace Nexus.Service.Update;

/// <summary>
/// Installs a verified update payload off Windows. Linux runs the release
/// tarball's install.sh in its own systemd unit; macOS swaps Nexus.app for the
/// dmg's copy from a detached finalizer. Something outside this process stops
/// the old version and starts the new one, as the Windows installer does.
/// </summary>
internal static partial class UnixUpdateApplier
{
    /// <summary>Detached macOS finalizer: <c>--ota-swap-finalize &lt;pid&gt; &lt;staged .app&gt; &lt;target .app&gt;</c>.</summary>
    public const string MacSwapFlag = "--ota-swap-finalize";

    internal const string LinuxUnitFile = "/etc/systemd/system/nexus.service";
    internal const string LinuxAppBinary = "/opt/nexus/Nexus";
    private const string SystemdRun = "/usr/bin/systemd-run";

    private const string StagedBundleName = ".Nexus-update.app";
    private const string LinuxExtractDirName = "linux-install";

    /// <summary>False for installs this class cannot update (a dev run, a Linux
    /// install without the root unit, a Nexus.app in a folder the user cannot
    /// write); those keep the manual download.</summary>
    public static bool CanAutoInstall
    {
        get
        {
#if LINUX
            return Platform.Linux.LinuxSession.IsRootDaemon
                && File.Exists(LinuxUnitFile) && File.Exists(LinuxAppBinary) && File.Exists(SystemdRun);
#elif MACOS
            return MacBundlePath() is { } bundle && access(Path.GetDirectoryName(bundle)!, W_OK) == 0;
#else
            return false;
#endif
        }
    }

    /// <summary>Hands the payload to the platform installer. Returns once it is
    /// running detached; throws when nothing was launched.</summary>
    public static void Apply(string payloadPath, string version)
    {
        if (!UpdateInstaller.IsValidVersionTag(version))
        {
            throw new ArgumentException($"Invalid version tag: {version}");
        }
#if LINUX
        ApplyLinux(payloadPath, version);
#elif MACOS
        ApplyMac(payloadPath);
#else
        throw new PlatformNotSupportedException("No update installer for this platform.");
#endif
    }

    /// <summary>
    /// install.sh stops nexus.service first, so it runs in a transient unit of
    /// its own, outside the cgroup that stop kills. The trailing start brings
    /// Nexus back if install.sh fails after its stop.
    /// </summary>
    internal static List<string> LinuxInstallArgs(string script, string logPath, string? home) => new()
    {
        "--unit=nexus-ota-install",
        "--collect",
        "--quiet",
        // install.sh reads HOME under set -u.
        $"--setenv=HOME={(string.IsNullOrEmpty(home) ? "/root" : home)}",
        $"--property=StandardOutput=append:{logPath}",
        $"--property=StandardError=append:{logPath}",
        "/bin/bash",
        "-c",
        "\"$0\" --update; rc=$?; systemctl start nexus.service; exit $rc",
        script,
    };

#if LINUX
    private static void ApplyLinux(string tarball, string version)
    {
        var extractDir = Path.Combine(UpdateDownloader.StagingDir, LinuxExtractDirName);
        if (Directory.Exists(extractDir))
        {
            Directory.Delete(extractDir, recursive: true);
        }
        Directory.CreateDirectory(extractDir);
        // Extracted as root: without --no-same-owner the archive's build-user uid
        // owns the tree install.sh copies into /opt/nexus.
        if (ShellExecutor.RunExit("tar", 120_000, "--no-same-owner", "-xzf", tarball, "-C", extractDir) != 0)
        {
            throw new InvalidOperationException("Extracting the update failed.");
        }

        var script = Path.Combine(extractDir, "nexus", "install.sh");
        if (!File.Exists(script))
        {
            throw new InvalidDataException("The update has no install.sh.");
        }

        UpdateInstaller.PruneOldInstallLogs(UpdateDownloader.StagingDir);
        var log = Path.Combine(UpdateDownloader.StagingDir, $"ota-install-{version}.log");
        var args = LinuxInstallArgs(script, log, Environment.GetEnvironmentVariable("HOME"));
        var rc = ShellExecutor.RunExit(SystemdRun, ShellExecutor.DefaultTimeoutMs, args.ToArray());
        if (rc != 0)
        {
            throw new InvalidOperationException($"systemd-run exited {rc}.");
        }
    }
#endif

    /// <summary>Drops the extracted Linux tarball once the new version is running.</summary>
    public static void DeleteExtractedPayload()
    {
        try
        {
            var dir = Path.Combine(UpdateDownloader.StagingDir, LinuxExtractDirName);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] could not remove the extracted update: {ex.Message}");
        }
    }

    /// <summary>The <c>.app</c> containing <paramref name="exe"/>, or null when it is not a bundle's main executable.</summary>
    internal static string? BundleFromExe(string? exe)
    {
        var macOsDir = Path.GetDirectoryName(exe);
        var contents = Path.GetDirectoryName(macOsDir);
        var bundle = Path.GetDirectoryName(contents);
        return Path.GetFileName(macOsDir) == "MacOS"
            && Path.GetFileName(contents) == "Contents"
            && bundle is not null
            && bundle.EndsWith(".app", StringComparison.Ordinal)
            ? bundle
            : null;
    }

    /// <summary>Value of a <c>key=value</c> line in <c>codesign -dv</c> output.</summary>
    internal static string? CodesignField(string output, string key)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(key + "=", StringComparison.Ordinal))
            {
                return line[(key.Length + 1)..];
            }
        }
        return null;
    }

#if MACOS
    private const int W_OK = 2;
    private const int SIGTERM = 15;

    private static string? MacBundlePath() => BundleFromExe(Environment.ProcessPath);

    private static string BundleExe(string bundle) => Path.Combine(bundle, "Contents", "MacOS", "Nexus");

    private static void ApplyMac(string dmg)
    {
        var bundle = MacBundlePath() ?? throw new InvalidOperationException("Not running from an app bundle.");
        var staged = Path.Combine(Path.GetDirectoryName(bundle)!, StagedBundleName);
        var mount = Path.Combine(UpdateDownloader.StagingDir, "mnt");
        Directory.CreateDirectory(mount);
        if (ShellExecutor.RunExit("/usr/bin/hdiutil", 120_000,
                "attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount, dmg) != 0)
        {
            throw new InvalidOperationException("Mounting the update failed.");
        }
        try
        {
            var incoming = Path.Combine(mount, "Nexus.app");
            VerifySameSigner(incoming, bundle);
            if (Directory.Exists(staged))
            {
                Directory.Delete(staged, recursive: true);
            }
            // Staged beside the target so the finalizer's swap is a same-volume rename.
            if (ShellExecutor.RunExit("/usr/bin/ditto", 300_000, incoming, staged) != 0)
            {
                throw new InvalidOperationException("Copying the update failed.");
            }
        }
        finally
        {
            ShellExecutor.RunExit("/usr/bin/hdiutil", 60_000, "detach", mount, "-force");
        }

        var psi = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(MacSwapFlag);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add(staged);
        psi.ArgumentList.Add(bundle);
        using var finalizer = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start the update finalizer.");
    }

    // The SHA-256 already matched SHA256SUMS; the signer check keeps a validly
    // signed app from another developer (or an ad-hoc build) from replacing this one.
    private static void VerifySameSigner(string incoming, string current)
    {
        if (ShellExecutor.RunExit("/usr/bin/codesign", 120_000, "--verify", "--deep", "--strict", incoming) != 0)
        {
            throw new InvalidDataException("The update's code signature is invalid.");
        }
        var inc = CodesignDisplay(incoming);
        var cur = CodesignDisplay(current);
        var team = CodesignField(cur, "TeamIdentifier");
        if (team is null || team == "not set"
            || team != CodesignField(inc, "TeamIdentifier")
            || CodesignField(inc, "Identifier") != CodesignField(cur, "Identifier"))
        {
            throw new InvalidDataException("The update is not signed by the same developer as this app.");
        }
    }

    // codesign -dv prints on stderr; read it to the end.
    private static string CodesignDisplay(string app)
    {
        ShellExecutor.RunWithStdinExit("/usr/bin/codesign", "", 30_000, out var stderr, "-dv", app);
        return stderr;
    }

    // The launchd agent (start at login) runs this bundle with KeepAlive, so it
    // must be booted out, or launchd relaunches the old binary mid-swap.
    private static bool AgentRunsBundle(string bundle)
    {
        try
        {
            return File.Exists(MacStartupProvider.PlistPath)
                && File.ReadAllText(MacStartupProvider.PlistPath).Contains(BundleExe(bundle), StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
#endif

    /// <summary>Detached child: stops the running app, swaps the staged bundle in, relaunches.</summary>
    public static int FinalizeMacSwap(string[] args)
    {
#if MACOS
        // Leave the app's process group first: launchd's bootout signals the whole group.
        setsid();
        if (args.Length < 4 || !int.TryParse(args[1], out var parentPid))
        {
            return 2;
        }
        var staged = args[2];
        var bundle = args[3];
        var viaAgent = AgentRunsBundle(bundle);

        if (viaAgent)
        {
            FactoryReset.BootoutMacAgent();
        }
        kill(parentPid, SIGTERM);
        FactoryReset.WaitForProcessExit(parentPid, TimeSpan.FromSeconds(30));
        FactoryReset.KillOtherInstancesOfThisBinary();

        // Per-run name: a leftover the user cannot delete (a bundle another admin
        // owned) must not block every later swap.
        var old = Path.Combine(Path.GetDirectoryName(bundle)!, $".Nexus-old-{Environment.ProcessId}.app");
        var swapped = false;
        try
        {
            Directory.Move(bundle, old);
            try
            {
                Directory.Move(staged, bundle);
                swapped = true;
            }
            catch
            {
                Directory.Move(old, bundle);
                throw;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[update] bundle swap failed: {ex.Message}");
        }

        var uid = ShellExecutor.Run("/usr/bin/id", "-u").Trim();
        var started = viaAgent && uid.Length > 0
            && ShellExecutor.RunExit("/bin/launchctl", ShellExecutor.DefaultTimeoutMs,
                "bootstrap", $"gui/{uid}", MacStartupProvider.PlistPath) == 0;
        if (!started)
        {
            ShellExecutor.RunExit("/usr/bin/open", ShellExecutor.DefaultTimeoutMs, bundle);
        }

        if (swapped)
        {
            foreach (var leftover in Directory.EnumerateDirectories(Path.GetDirectoryName(bundle)!, ".Nexus-old-*.app"))
            {
                try { Directory.Delete(leftover, recursive: true); }
                catch (Exception ex) { Console.Error.WriteLine($"[update] could not remove {leftover}: {ex.Message}"); }
            }
        }
        return swapped ? 0 : 1;
#else
        _ = args;
        return 1;
#endif
    }

#if MACOS
    [LibraryImport("libc", SetLastError = true)]
    private static partial int setsid();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int sig);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int access(string path, int mode);
#endif
}
