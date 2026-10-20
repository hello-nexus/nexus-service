using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Platform;

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
    public const int MaxAnimatedBytes = 8 * 1024 * 1024;

    private static readonly string[] Extensions = [".png", ".jpg", ".gif", ".mp4", ".webm"];

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
    /// the bytes are not a PNG/JPEG/GIF/MP4/WebM or exceed the cap for their
    /// type (MaxBytes for stills, MaxAnimatedBytes for GIF/MP4/WebM). Idempotent: an
    /// existing file for the same content is left untouched.
    /// </summary>
    public string? Store(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxAnimatedBytes)
        {
            return null;
        }
        var ext = SniffExtension(bytes);
        if (ext is null || bytes.Length > MaxBytesFor(ext))
        {
            return null;
        }
        var id = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        WriteAtomic(id, ext, bytes);
        return id;
    }

    /// <summary>
    /// Writes bytes at a caller-computed content id, skipping Store()'s
    /// interactive-upload cap - a DeckPresetPackage asset has its own, larger
    /// cap, already validated (hash and size) before this is
    /// called. Idempotent like Store().
    /// </summary>
    public void StoreValidated(string id, string ext, byte[] bytes) => WriteAtomic(id, ext, bytes);

    private void WriteAtomic(string id, string ext, byte[] bytes)
    {
        var path = Path.Combine(_rootDir, id + ext);
        if (File.Exists(path))
        {
            return;
        }
        // Write to a unique temp file then rename into place: a reader never
        // sees a half-written content-addressed file, and two concurrent
        // writers of identical bytes resolve to the same final path without a
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
    }

    /// <summary>Returns the stored bytes and content type, or null when id is malformed or unknown.</summary>
    public (byte[] Bytes, string ContentType)? TryLoad(string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }
        foreach (var ext in Extensions)
        {
            var path = Path.Combine(_rootDir, id + ext);
            if (File.Exists(path))
            {
                return (File.ReadAllBytes(path), ContentTypeFor(ext));
            }
        }
        return null;
    }

    /// <summary>
    /// Bytes a renderer can decode as a still: the stored PNG/JPEG/GIF itself
    /// (a GIF decodes to its first frame), or for a video the poster frame
    /// EnsurePosterAsync extracted. Null when unknown or no poster exists yet.
    /// </summary>
    public byte[]? TryLoadStill(string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }
        foreach (var ext in Extensions)
        {
            var path = Path.Combine(_rootDir, id + ext);
            if (!File.Exists(path))
            {
                continue;
            }
            return ReadOrNull(IsVideoExtension(ext) ? PosterPath(id) : path);
        }
        return null;
    }

    private string PosterPath(string id) => Path.Combine(_rootDir, id + ".poster.png");

    private static byte[]? ReadOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts a first-frame PNG next to a stored MP4/WebM so a physical key
    /// (which shows a still) has something to draw. Best effort: no ffmpeg or
    /// a failed decode leaves no poster and the key shows its default icon.
    /// </summary>
    public async Task EnsurePosterAsync(string id, CancellationToken ct = default)
    {
        if (!IsValidId(id) || File.Exists(PosterPath(id)) || FfmpegResolver.Path is null)
        {
            return;
        }
        var source = Extensions.Where(IsVideoExtension).Select(e => Path.Combine(_rootDir, id + e)).FirstOrDefault(File.Exists);
        if (source is null)
        {
            return;
        }
        var tmp = Path.Combine(_rootDir, id + "." + Path.GetRandomFileName() + ".tmp.png");
        try
        {
            await MediaImporter.RunFfmpeg(PosterTimeoutSeconds, ct, ["-y", "-i", source, "-frames:v", "1", tmp]);
            if (File.Exists(tmp) && !File.Exists(PosterPath(id)))
            {
                File.Move(tmp, PosterPath(id));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ServiceLog.Warn($"[deck] poster frame for {id} failed: {ex.Message}");
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    private const int PosterTimeoutSeconds = 15;

    public static int MaxBytesFor(string ext) => ext is ".png" or ".jpg" ? MaxBytes : MaxAnimatedBytes;

    public static string ContentTypeFor(string ext) => ext switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".gif" => "image/gif",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        _ => "application/octet-stream",
    };

    public static bool IsVideoExtension(string ext) => ext is ".mp4" or ".webm";

    public static string? SniffExtension(byte[] bytes)
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
        if (bytes.Length >= 6 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == '8' &&
            (bytes[4] == '7' || bytes[4] == '9') && bytes[5] == 'a')
        {
            return ".gif";
        }
        if (bytes.Length >= 8 && bytes[4] == 'f' && bytes[5] == 't' && bytes[6] == 'y' && bytes[7] == 'p')
        {
            return ".mp4";
        }
        if (bytes.Length >= 4 && bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3)
        {
            return ".webm";
        }
        return null;
    }
}
