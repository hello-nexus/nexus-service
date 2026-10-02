using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>Records custom uploads in Kanali's own media library (store.json
/// <c>waterBlockScreenCustomMedia</c>) the way Kanali records its uploads. Whenever the panel
/// reports its file list, Kanali removes every /userdata/user/ file whose name is not the
/// <c>fileName[0][0]</c> of an entry here, so an upload missing from this list is deleted
/// from the panel the next time Kanali starts.</summary>
public static class TryxKanaliStore
{
    private const string MediaKey = "waterBlockScreenCustomMedia";
    private static readonly object Gate = new();

    /// <summary>One upload: the clip under <c>kanali\media</c>, its length, and the panel file it became.</summary>
    public sealed record Upload(
        string MediaPath, string? ThumbPath, long Size, string DeviceFileName, string Serial);

    /// <summary>Kanali's upload timestamp (local time), the stem of every file it names.</summary>
    public static string Timestamp(DateTime local)
        => local.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture);

    public static string MediaDir(string dataDir) => Path.Combine(dataDir, "media");

    public static string ThumbDir(string dataDir) => Path.Combine(dataDir, "media", "thumb");

    /// <summary>Appends <paramref name="upload"/>, replacing any entry for the same panel file; false when store.json is unreadable.</summary>
    public static bool Register(string dataDir, Upload upload)
    {
        lock (Gate)
        {
            return TryRegister(dataDir, upload);
        }
    }

    private static bool TryRegister(string dataDir, Upload upload)
    {
        try
        {
            var storePath = Path.Combine(dataDir, "store.json");
            if (JsonNode.Parse(File.ReadAllBytes(storePath)) is not JsonObject root) return false;
            if (root[MediaKey] is not JsonArray media)
            {
                media = new JsonArray();
                root[MediaKey] = media;
            }
            RemoveEntries(media, upload.DeviceFileName);
            // Kanali's field order; it omits thumb when it got no frame.
            var entry = new JsonObject
            {
                ["type"] = "MP4",
                ["path"] = upload.MediaPath,
                ["ratio"] = "2:1",
                ["name"] = Path.GetFileName(upload.MediaPath),
            };
            if (!string.IsNullOrEmpty(upload.ThumbPath)) entry["thumb"] = upload.ThumbPath;
            entry["size"] = upload.Size;
            entry["outPath"] = upload.MediaPath + DeviceSuffix(upload.DeviceFileName);
            entry["fileName"] = new JsonArray(new JsonArray(upload.DeviceFileName));
            entry["sn"] = upload.Serial;
            entry["source"] = "LOCAL";
            media.Add((JsonNode)entry);
            Write(storePath, root);
            return true;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[tryx] Kanali library register of {upload.DeviceFileName} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Drops every entry for <paramref name="deviceFileName"/> and deletes its clip, as Kanali's delete does.</summary>
    public static void Unregister(string dataDir, string deviceFileName)
    {
        lock (Gate)
        {
            try
            {
                var storePath = Path.Combine(dataDir, "store.json");
                if (JsonNode.Parse(File.ReadAllBytes(storePath)) is not JsonObject root) return;
                if (root[MediaKey] is not JsonArray media) return;
                var removed = RemoveEntries(media, deviceFileName);
                if (removed.Length == 0) return;
                Write(storePath, root);
                foreach (var path in removed)
                {
                    try { File.Delete(path); } catch { /* Kanali ignores a failed delete too */ }
                }
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[tryx] Kanali library unregister of {deviceFileName} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // The ".h264_WxH" suffix of a panel file name.
    private static string DeviceSuffix(string deviceFileName)
    {
        var i = deviceFileName.LastIndexOf(".h264_", StringComparison.Ordinal);
        return i >= 0 ? deviceFileName[i..] : "";
    }

    // Removes the entries naming the panel file and returns their clip paths.
    private static string[] RemoveEntries(JsonArray media, string deviceFileName)
    {
        var paths = new List<string>();
        for (var i = media.Count - 1; i >= 0; i--)
        {
            if (media[i] is not JsonObject entry || FirstFileName(entry) != deviceFileName) continue;
            if (entry["path"] is JsonValue p && p.TryGetValue<string>(out var path) && Path.IsPathRooted(path))
            {
                paths.Add(path);
            }
            media.RemoveAt(i);
        }
        return paths.ToArray();
    }

    private static string? FirstFileName(JsonObject entry)
        => entry["fileName"] is JsonArray outer && outer.Count > 0
           && outer[0] is JsonArray inner && inner.Count > 0
           && inner[0] is JsonValue v && v.TryGetValue<string>(out var name)
            ? name
            : null;

    // Tab-indented like Kanali's own writes; temp + rename because a running Kanali re-reads the
    // file on every access.
    private static void Write(string storePath, JsonObject root)
    {
        var tmp = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            using (var writer = new Utf8JsonWriter(fs, new JsonWriterOptions
            {
                Indented = true,
                IndentCharacter = '\t',
                IndentSize = 1,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
            {
                root.WriteTo(writer);
            }
            File.Move(tmp, storePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* moved or never created */ }
        }
    }
}
