using System.IO;

namespace Nexus.Service.Migration;

/// <summary>
/// Every read of a Nexus 2 file goes through here: FileAccess.Read +
/// FileShare.ReadWrite, since Nexus 2 itself may hold config.json open.
/// Nothing under a Nexus 2 directory is ever written, renamed, or deleted.
/// </summary>
internal static class Nexus2ReadOnlyIo
{
    public static string ReadAllText(string path)
    {
        using var stream = OpenRead(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    /// <summary>Copies a Nexus 2 file out without touching the source.</summary>
    public static void CopyTo(string sourcePath, string destPath)
    {
        using var source = OpenRead(sourcePath);
        using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(dest);
    }
}
