using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Nexus.Service.Migration;

/// <summary>One packed file inside an Electron app.asar; Offset is relative to the archive's data block.</summary>
internal readonly record struct Nexus2AsarEntry(string Path, long Size, long Offset);

/// <summary>Reads files out of Nexus 2's app.asar, where its bundled renderer assets live.</summary>
internal static class Nexus2AsarReader
{
    // Header: uint32 4, uint32 header pickle size, uint32 pickle payload size,
    // uint32 JSON length, then the JSON. File data starts at 8 + header pickle size.
    private const int PreambleSize = 16;
    private const int MaxHeaderBytes = 64 * 1024 * 1024;

    /// <summary>Packed (not unpacked) entries whose file name matches, in header order. Empty on any read or parse failure.</summary>
    public static List<Nexus2AsarEntry> Find(string asarPath, Func<string, bool> fileNameMatches)
    {
        var found = new List<Nexus2AsarEntry>();
        try
        {
            using var stream = Nexus2ReadOnlyIo.OpenRead(asarPath);
            var preamble = new byte[PreambleSize];
            stream.ReadExactly(preamble);
            var jsonLength = BitConverter.ToUInt32(preamble, 12);
            if (jsonLength == 0 || jsonLength > MaxHeaderBytes)
            {
                return found;
            }
            var json = new byte[jsonLength];
            stream.ReadExactly(json);
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(json));
            Walk(doc.RootElement, "", fileNameMatches, found);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or EndOfStreamException)
        {
            found.Clear();
        }
        return found;
    }

    /// <summary>Copies one entry's bytes to destPath; the archive is only read.</summary>
    public static void Extract(string asarPath, Nexus2AsarEntry entry, string destPath)
    {
        using var stream = Nexus2ReadOnlyIo.OpenRead(asarPath);
        var preamble = new byte[PreambleSize];
        stream.ReadExactly(preamble);
        var dataStart = 8L + BitConverter.ToUInt32(preamble, 4);
        stream.Seek(dataStart + entry.Offset, SeekOrigin.Begin);

        using var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        var remaining = entry.Size;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException("app.asar ended inside an entry");
            }
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void Walk(JsonElement node, string prefix, Func<string, bool> fileNameMatches, List<Nexus2AsarEntry> found)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        foreach (var child in files.EnumerateObject())
        {
            if (child.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var path = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
            if (child.Value.TryGetProperty("files", out _))
            {
                Walk(child.Value, path, fileNameMatches, found);
                continue;
            }
            if (!fileNameMatches(child.Name) || child.Value.TryGetProperty("unpacked", out _))
            {
                continue;
            }
            // asar writes offset as a decimal string (it can exceed 2^53).
            if (child.Value.TryGetProperty("size", out var sizeEl) && sizeEl.ValueKind == JsonValueKind.Number
                && sizeEl.TryGetInt64(out var size)
                && child.Value.TryGetProperty("offset", out var offsetEl) && offsetEl.ValueKind == JsonValueKind.String
                && long.TryParse(offsetEl.GetString(), out var offset))
            {
                found.Add(new Nexus2AsarEntry(path, size, offset));
            }
        }
    }
}
