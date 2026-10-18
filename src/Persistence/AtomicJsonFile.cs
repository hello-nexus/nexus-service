using System.IO;
using System.Text;

namespace Nexus.Service.Persistence;

/// <summary>
/// Crash-safe text/JSON writer. Writes the payload to "&lt;path&gt;.tmp",
/// flushes it to disk, then renames it onto the target. If the target already
/// exists, uses File.Replace (preserves ACLs on Windows) and keeps the
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

    public static void Write(string path, string contents)
    {
        Write(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents));
    }

    public static void Write(string path, byte[] contents)
    {
        var tmp = TempPathFor(path);
        // FileMode.Create truncates an existing tmp in place, keeping any ACL set on it beforehand.
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(contents, 0, contents.Length);
            fs.Flush(flushToDisk: true);
        }
        if (File.Exists(path))
            File.Replace(tmp, path, BackupPathFor(path));
        else
            File.Move(tmp, path);
    }
}
