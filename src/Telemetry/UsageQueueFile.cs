using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Nexus.Service.Telemetry;

/// <summary>Hand-written JSON snapshot of the pending usage queue, so events survive a process exit that never runs the final flush.</summary>
internal static class UsageQueueFile
{
    internal const string FileName = "usage-queue.json";
    // Well above a full queue of fitted events; anything bigger is not ours.
    internal const long MaxFileBytes = 8 * 1024 * 1024;

    public static string DefaultPath() => Path.Combine(Persistence.NexusDataPaths.NexusRoot(), FileName);

    public static void Write(string path, IReadOnlyList<UsageEvent> events)
    {
        if (events.Count == 0)
        {
            TryDelete(path);
            return;
        }
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartArray("events");
            foreach (var e in events)
            {
                w.WriteStartObject();
                w.WriteString("id", e.Id);
                w.WriteString("name", e.Name);
                w.WriteNumber("ts", e.Timestamp.ToUnixTimeMilliseconds());
                w.WriteNumber("bytes", e.Bytes);
                w.WriteStartObject("props");
                foreach (var p in e.Props)
                {
                    w.WritePropertyName(p.Key);
                    PostHogSink.WriteValue(w, p.Value);
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, buffer.WrittenSpan.ToArray());
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Reads and deletes the file. A missing, oversized or corrupt file yields an empty list.</summary>
    public static List<UsageEvent> ReadAndDelete(string path)
    {
        var result = new List<UsageEvent>();
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return result;
            if (info.Length <= MaxFileBytes)
                Parse(File.ReadAllBytes(path), result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            result.Clear();
        }
        TryDelete(path);
        return result;
    }

    private static void Parse(byte[] bytes, List<UsageEvent> into)
    {
        using var doc = JsonDocument.Parse(bytes);
        foreach (var el in doc.RootElement.GetProperty("events").EnumerateArray())
        {
            var props = new List<KeyValuePair<string, object?>>();
            foreach (var p in el.GetProperty("props").EnumerateObject())
                props.Add(new(p.Name, ReadValue(p.Value)));
            into.Add(new UsageEvent
            {
                Id = el.GetProperty("id").GetString() ?? "",
                Name = el.GetProperty("name").GetString() ?? "",
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(el.GetProperty("ts").GetInt64()),
                Bytes = el.GetProperty("bytes").GetInt32(),
                Props = props.ToArray(),
            });
        }
    }

    private static object? ReadValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
        JsonValueKind.Array => ReadArray(v),
        _ => null,
    };

    private static string[] ReadArray(JsonElement v)
    {
        var items = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                items.Add(item.GetString() ?? "");
        }
        return items.ToArray();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
