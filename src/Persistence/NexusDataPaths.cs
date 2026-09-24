using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Nexus.Service.Persistence;

/// <summary>
/// Single resolver for the machine-scope config root the service's history
/// stores (screen time, metrics/temperature history) live under.
///
/// Distinct from MediaLibrary.NexusDataDir(): that resolver targets the DATA
/// root (XDG_DATA_HOME on Linux) for device media; this one targets the
/// CONFIG root (XDG_CONFIG_HOME on Linux) these history stores have always
/// used. Windows and macOS resolve to the same directory either way, so the
/// two roots only diverge on Linux - and not for the root system daemon,
/// which collapses both onto <see cref="SystemDaemonRoot"/>.
/// </summary>
internal static class NexusDataPaths
{
    /// <summary><c>&lt;config-root&gt;/Nexus</c>: %ProgramData%\Nexus (Windows,
    /// machine-scope since the service runs as LocalSystem),
    /// ~/Library/Application Support/Nexus (macOS), $XDG_CONFIG_HOME/Nexus or
    /// ~/.config/Nexus (Linux), /var/lib/nexus for the Linux root system
    /// daemon (see <see cref="SystemDaemonRoot"/>). NEXUS_DATA_ROOT, when set to a non-blank
    /// value, takes precedence on every platform so a verification host can
    /// point at a throwaway directory instead of the real machine store.</summary>
    public static string NexusRoot() => ResolveRoot(Environment.GetEnvironmentVariable("NEXUS_DATA_ROOT"));

    /// <summary>
    /// The one machine-scope root a Linux ROOT SYSTEM DAEMON keeps every store
    /// under, or null on any other platform/install shape (macOS, Windows, a
    /// <c>systemd --user</c> install, a dev run), which keep their existing
    /// per-user layout.
    ///
    /// A system daemon has no user of its own: <c>HOME</c> is root's, and the
    /// logged-in user's home is knowable only once a graphical session exists.
    /// Deriving a store path from that made the daemon load a DIFFERENT
    /// settings.json depending on whether it started before or after login -
    /// two persistent configs on one box, each with its own galleries. So the
    /// daemon's data hangs off a fixed path instead, exactly as the Windows
    /// service uses %ProgramData% rather than a user profile. Config, data and
    /// cache collapse into it the same way they collapse into %ProgramData%.
    ///
    /// Callers resolving their own store path use it as an early return:
    /// <c>if (NexusDataPaths.SystemDaemonRoot is { } root) return Path.Combine(root, "...");</c>
    /// </summary>
    internal static string? SystemDaemonRoot => IsLinuxSystemDaemon ? LinuxSystemRoot : null;

    /// <summary>FHS state directory for a system daemon's variable data.</summary>
    internal const string LinuxSystemRoot = "/var/lib/nexus";

    private static bool IsLinuxSystemDaemon =>
#if LINUX
        Platform.Linux.LinuxSession.IsRootDaemon;
#else
        false;
#endif

    /// <summary>
    /// Make a file owner-readable only, BEFORE anything is written into it. The
    /// system-daemon root is world-readable so the logged-in user can still open
    /// their own logs in a file manager - which is exactly why the files
    /// carrying secrets (settings.json's local auth token and cloud session,
    /// the local HTTPS private key) must not inherit that.
    ///
    /// Create-then-restrict is the wrong order and the reason this creates the
    /// file itself: a write followed by a chmod publishes the secret for the
    /// length of the write, and an atomic-write temp file that a crash strands
    /// keeps whatever the umask gave it forever. Callers point this at the temp
    /// file they are about to write, then let the rename carry the mode over.
    ///
    /// No-op off the daemon path: those files sit inside a per-user home already.
    /// </summary>
    internal static void CreateRestricted(string path)
    {
        if (SystemDaemonRoot is null || !OperatingSystem.IsLinux())
            return;
        try
        {
            if (!File.Exists(path))
            {
                using var _ = File.Create(path);
            }
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[data-paths] could not restrict {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Owner-only a directory under the system-daemon root, for trees that hold
    /// personal history rather than something the user browses: db/ carries
    /// screen time, app usage and AI history, which sat inside a private home
    /// before the daemon moved to a shared root. No-op off the daemon path.
    /// </summary>
    internal static void CreateRestrictedDirectory(string path)
    {
        if (SystemDaemonRoot is null || !OperatingSystem.IsLinux())
            return;
        try
        {
            Directory.CreateDirectory(path);
            File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[data-paths] could not restrict {path}: {ex.Message}");
        }
    }

    /// <summary>Shared database directory every history store's files live
    /// under: <c>&lt;NexusRoot&gt;/db</c>. A pure resolver - the stores under it
    /// create it, and boot calls <see cref="CreateRestrictedDirectory"/> once so
    /// it exists owner-only before any of them get there.</summary>
    public static string DatabaseDir() => Path.Combine(NexusRoot(), "db");

    /// <summary>Test seam: resolves the root from an explicit override value
    /// instead of reading the environment, so tests never mutate process-wide
    /// state.</summary>
    internal static string ResolveRoot(string? overrideRoot) =>
        ResolveRoot(overrideRoot, IsLinuxSystemDaemon);

    /// <summary>Test seam: the daemon flag passed in rather than probed, so the
    /// system-daemon root is assertable off a Linux box.</summary>
    internal static string ResolveRoot(string? overrideRoot, bool linuxSystemDaemon)
    {
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Path.GetFullPath(overrideRoot.Trim());
        }
        if (linuxSystemDaemon)
        {
            return LinuxSystemRoot;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "Nexus");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Nexus");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(xdg, "Nexus");
    }
}
