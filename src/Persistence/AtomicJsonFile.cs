using System.IO;
using System.Text;

namespace Nexus.Service.Persistence;

/// <summary>
/// Crash-safe text/JSON writer. Writes the payload to "&lt;path&gt;.tmp",
/// flushes it to disk, then renames it onto the target. If the target already
/// exists, uses File.Replace (preserves ACLs on Windows), optionally keeping the
/// previous version at "&lt;path&gt;.bak". If not, falls back to File.Move.
///
/// The flush matters: NTFS journals metadata, not file data, so without it a
/// crash right after the rename can leave the target zero-filled. Use this for
/// any JSON file we read on startup.
/// </summary>
public static class AtomicJsonFile
{
    /// <summary>The scratch file <see cref="Write"/> stages into. Exposed so a
    /// caller that must set permissions on the bytes BEFORE they are written
    /// restricts the file this actually writes, not a guess at its name.</summary>
    public static string TempPathFor(string path) => path + ".tmp";

    public static string BackupPathFor(string path) => path + ".bak";

    /// <param name="keepBackup">Keeps the replaced version at <see cref="BackupPathFor"/>.</param>
    /// <param name="flushToDisk">False only for rebuildable caches.</param>
    public static void Write(string path, string contents, bool keepBackup = false, bool flushToDisk = true)
    {
        Write(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents), keepBackup, flushToDisk);
    }

    public static void Write(string path, byte[] contents, bool keepBackup = false, bool flushToDisk = true)
    {
        var tmp = TempPathFor(path);
        // FileMode.Create truncates an existing tmp in place, keeping any ACL set on it beforehand.
        Retry(() =>
        {
            using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
            fs.Write(contents, 0, contents.Length);
            if (flushToDisk)
            {
                fs.Flush(flushToDisk: true);
            }
        });
        Retry(() =>
        {
            if (File.Exists(path))
                File.Replace(tmp, path, keepBackup ? BackupPathFor(path) : null);
            else
                File.Move(tmp, path);
        });
    }

    // A virus scanner or indexer can hold a just-written file for a moment on Windows; that is a
    // sharing violation, not a failed write, so it gets a short bounded retry.
    private static void Retry(Action step)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                step();
                return;
            }
            catch (IOException ex) when (attempt < 6 && ex is not (DirectoryNotFoundException or FileNotFoundException))
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }
}
