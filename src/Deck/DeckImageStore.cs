using System;
using System.IO;
using System.Security.Cryptography;
using Nexus.Service.Media;

namespace Nexus.Service.Deck;

/// <summary>
/// Global content-addressed store for deck key icon images (DeckIcon kind
/// "image"), shared across every physical Stream Deck and the virtual deck
/// widget: <c>&lt;MediaLibrary root&gt;/Nexus/deck-images/&lt;sha256&gt;.png|.jpg</c>.
/// Unlike StreamDeckImageCache (per-serial rendered key-face bitmaps), this
/// keeps the user's original uploaded bytes so one icon id can back any
/// slot. No refcounting or GC in v1 - an id no slot references anymore just
/// sits on disk, matching the panel-backgrounds precedent.
/// </summary>
public sealed class DeckImageStore
{
    public const int MaxBytes = 512 * 1024;

    private readonly string _rootDir;

    public DeckImageStore()
        : this(MediaLibrary.MediaStoreDir("deck-images"))
    {
    }

    public DeckImageStore(string rootDir)
    {
        _rootDir = rootDir;
        Directory.CreateDirectory(_rootDir);
    }

    public string RootDir => _rootDir;

    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 64)
        {
            return false;
        }
        foreach (var ch in id)
        {
            var isLowerHex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');
            if (!isLowerHex)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Stores the bytes under their sha256 id and returns it, or null when
    /// the bytes are not a PNG/JPEG or exceed MaxBytes. Idempotent: an
    /// existing file for the same content is left untouched.
    /// </summary>
    public string? Store(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes)
        {
            return null;
        }
        var ext = SniffExtension(bytes);
        if (ext is null)
        {
            return null;
        }
        var id = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var path = Path.Combine(_rootDir, id + ext);
        if (File.Exists(path))
        {
            return id;
        }
        // Write to a unique temp file then rename into place: a reader never
        // sees a half-written content-addressed file, and two concurrent
        // uploads of identical bytes resolve to the same final path without a
        // sharing violation (the rename loser's identical file is discarded).
        var tmp = Path.Combine(_rootDir, id + "." + Path.GetRandomFileName() + ".tmp");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path);
        }
        catch (IOException) when (File.Exists(path))
        {
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
        return id;
    }

    /// <summary>Returns the stored bytes and content type, or null when id is malformed or unknown.</summary>
    public (byte[] Bytes, string ContentType)? TryLoad(string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }
        var pngPath = Path.Combine(_rootDir, id + ".png");
        if (File.Exists(pngPath))
        {
            return (File.ReadAllBytes(pngPath), "image/png");
        }
        var jpgPath = Path.Combine(_rootDir, id + ".jpg");
        if (File.Exists(jpgPath))
        {
            return (File.ReadAllBytes(jpgPath), "image/jpeg");
        }
        return null;
    }

    private static string? SniffExtension(byte[] bytes)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
            bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return ".png";
        }
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ".jpg";
        }
        return null;
    }
}
