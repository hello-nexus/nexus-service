using System;
using System.Collections.Generic;
using System.IO;
using Nexus.Service.Media;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// TEMPORARY one-shot migration - delete this class, its tests
/// (<c>DataLayoutMigrationTests</c>), and the <c>Program.cs</c> call site after
/// ~2026-07-20, once deployed installs have upgraded past the flat->grouped
/// layout change. Nothing else depends on it.
///
/// Migrates the flat <c>&lt;data-root&gt;/Nexus/&lt;store&gt;</c> layout to the grouped
/// <c>devices/</c> + <c>media/</c> layout. Runs before any store resolves its
/// directory, so a store never creates the new (empty) target ahead of the move.
///
/// Only irreplaceable user data is MOVED (device media, per-device records). The
/// driver cache and thumbnail caches are DELETED at their old path instead - they
/// re-download (hash-pinned) or rebuild at the new path, so moving them is waste.
/// firmware/ is left untouched (unchanged path).
///
/// Idempotent and best-effort: a move only fires when the old path exists and the
/// new one does not, and any entry that throws (locked file, permission) is logged
/// and retried on the next boot because its old path still exists.
/// </summary>
internal static class DataLayoutMigration
{
    internal enum EntryKind { MoveDir, MoveFile, DeleteDir }

    internal readonly record struct Entry(string Old, string New, EntryKind Kind);

    public static void Run()
    {
        try { Execute(BuildEntries()); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[data-migration] aborted: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static List<Entry> BuildEntries()
    {
        var data = MediaLibrary.NexusDataDir();
        string flat(string name) => Path.Combine(data, name);

        // Old roots that differed from the DATA root before this change.
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nexus");
        var tryx = MediaLibrary.DeviceStoreDir("tryx");

        return new List<Entry>
        {
            // Device media -> devices/<family>/
            new(OldStreamDeckRoot(), MediaLibrary.DeviceStoreDir("streamdeck"), EntryKind.MoveDir),
            new(flat("corsair-lcd"), MediaLibrary.DeviceStoreDir("corsair-lcd"), EntryKind.MoveDir),
            new(flat("lianli-wireless-lcd"), MediaLibrary.DeviceStoreDir("lianli-wireless"), EntryKind.MoveDir),
            new(flat("panel-backgrounds"), MediaLibrary.DeviceStoreDir("panel-backgrounds"), EntryKind.MoveDir),
            new(Path.Combine(appData, "tryx-media"), Path.Combine(tryx, "media"), EntryKind.MoveDir),

            // Lighting/widget content -> media/<kind>/. The effects entry is a
            // descendant hop (old `media` becomes the new `media/` parent) and MUST
            // precede the other media/* entries: they CreateDirectory the `media/`
            // parent, and if one ran first the effects hop would bury the whole
            // populated media/ tree under media/effects.
            new(flat("media"), MediaLibrary.MediaStoreDir("effects"), EntryKind.MoveDir),
            new(flat("gallery"), MediaLibrary.MediaStoreDir("gallery"), EntryKind.MoveDir),
            new(flat("deck-images"), MediaLibrary.MediaStoreDir("deck-images"), EntryKind.MoveDir),

            // Per-device records -> devices/transports/
            new(Path.Combine(appData, "streamed-panels.json"),
                Path.Combine(MediaLibrary.DeviceStoreDir("transports"), "streamed-panels.json"), EntryKind.MoveFile),
            new(Path.Combine(appData, "qseries-transports.json"),
                Path.Combine(MediaLibrary.DeviceStoreDir("transports"), "qseries-transports.json"), EntryKind.MoveFile),

            // Regenerable / re-downloaded: remove the stray old dir, do not move.
            new(Path.Combine(appData, "tryx-thumbs"), "", EntryKind.DeleteDir),
            new(Path.Combine(appData, "tryx-kanali-thumbs"), "", EntryKind.DeleteDir),
            new(OldDriverCacheRoot(), "", EntryKind.DeleteDir),
        };
    }

    internal static void Execute(IEnumerable<Entry> entries)
    {
        foreach (var e in entries)
        {
            try
            {
                switch (e.Kind)
                {
                    case EntryKind.MoveDir: MoveDir(e.Old, e.New); break;
                    case EntryKind.MoveFile: MoveFile(e.Old, e.New); break;
                    case EntryKind.DeleteDir: DeleteDir(e.Old); break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[data-migration] {e.Kind} {e.Old} failed (retried next boot): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void MoveDir(string oldPath, string newPath)
    {
        var tmp = oldPath + ".migrate-tmp";

        // Resume an interrupted descendant move: the data is parked in tmp and the
        // target was never created. old is already gone, so the normal guard below
        // would orphan tmp - finish the move instead.
        if (!Directory.Exists(oldPath) && Directory.Exists(tmp)
            && !Directory.Exists(newPath) && !File.Exists(newPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            Directory.Move(tmp, newPath);
            Console.WriteLine($"[data-migration] resumed {tmp} -> {newPath}");
            return;
        }

        if (!Directory.Exists(oldPath)) return;
        if (Directory.Exists(newPath) || File.Exists(newPath)) return; // already migrated; never clobber
        if (PathsEqual(oldPath, newPath)) return;

        if (IsDescendant(oldPath, newPath))
        {
            // new is inside old (media -> media/effects): hop via a temp sibling.
            // old still exists here, so a leftover tmp is a stale partial - clear it.
            if (Directory.Exists(tmp))
            {
                Directory.Delete(tmp, recursive: true);
            }
            Directory.Move(oldPath, tmp);
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            Directory.Move(tmp, newPath);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            Directory.Move(oldPath, newPath);
        }
        Console.WriteLine($"[data-migration] moved {oldPath} -> {newPath}");
    }

    private static void MoveFile(string oldPath, string newPath)
    {
        if (!File.Exists(oldPath)) return;
        if (File.Exists(newPath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.Move(oldPath, newPath);
        Console.WriteLine($"[data-migration] moved {oldPath} -> {newPath}");
    }

    private static void DeleteDir(string oldPath)
    {
        if (!Directory.Exists(oldPath)) return;
        Directory.Delete(oldPath, recursive: true);
        Console.WriteLine($"[data-migration] removed stale {oldPath}");
    }

    // Stream Deck key images lived on the CONFIG root before this change (the one
    // device store that did); every other device store was already on DATA.
    private static string OldStreamDeckRoot()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
            return Path.Combine(MediaLibrary.NexusDataDir(), "streamdeck");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(xdg, "Nexus", "streamdeck");
    }

    // The downloaded-driver cache: %ProgramData%\Nexus\tools on Windows, ~/.cache/Nexus/tools elsewhere.
    private static string OldDriverCacheRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nexus", "tools");
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        }
        return Path.Combine(xdg, "Nexus", "tools");
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Norm(a), Norm(b), Cmp());

    private static bool IsDescendant(string ancestor, string descendant)
    {
        var a = Norm(ancestor) + Path.DirectorySeparatorChar;
        return Norm(descendant).StartsWith(a, Cmp());
    }

    private static string Norm(string p) =>
        Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);

    private static StringComparison Cmp() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
