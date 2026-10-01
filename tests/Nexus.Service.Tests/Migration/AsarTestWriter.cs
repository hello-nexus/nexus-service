using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nexus.Service.Tests.Migration;

/// <summary>Writes an Electron app.asar holding the given files under one directory, laid out the way
/// the asar package does: uint32 4, uint32 header pickle size, then the pickle (uint32 payload size,
/// uint32 JSON length, JSON, padding to 4), then the file bytes back to back.</summary>
internal static class AsarTestWriter
{
    public static void Write(string asarPath, string directory, IReadOnlyList<(string Name, byte[] Bytes)> files)
    {
        var entries = new StringBuilder();
        long offset = 0;
        for (var i = 0; i < files.Count; i++)
        {
            if (i > 0)
            {
                entries.Append(',');
            }
            entries.Append($"\"{files[i].Name}\":{{\"size\":{files[i].Bytes.Length},\"offset\":\"{offset}\"}}");
            offset += files[i].Bytes.Length;
        }
        var json = Encoding.UTF8.GetBytes($"{{\"files\":{{\"{directory}\":{{\"files\":{{{entries}}}}}}}}}");
        var padding = (4 - json.Length % 4) % 4;
        var payloadSize = 4 + json.Length + padding;

        Directory.CreateDirectory(Path.GetDirectoryName(asarPath)!);
        using var stream = File.Create(asarPath);
        using var writer = new BinaryWriter(stream);
        writer.Write(4u);
        writer.Write((uint)(4 + payloadSize));
        writer.Write((uint)payloadSize);
        writer.Write((uint)json.Length);
        writer.Write(json);
        writer.Write(new byte[padding]);
        foreach (var (_, bytes) in files)
        {
            writer.Write(bytes);
        }
    }
}
