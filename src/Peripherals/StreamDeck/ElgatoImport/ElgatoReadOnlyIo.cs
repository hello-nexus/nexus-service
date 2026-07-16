using System.IO;

namespace Nexus.Service.Peripherals.StreamDeck.ElgatoImport;

/// <summary>
/// Every read under an Elgato store directory goes through here:
/// FileAccess.Read + FileShare.ReadWrite, since Elgato's own app may hold
/// these files open. Nothing under an Elgato directory is ever written,
/// renamed, or deleted by the importer.
/// </summary>
internal static class ElgatoReadOnlyIo
{
    public static string ReadAllText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static byte[] ReadAllBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
