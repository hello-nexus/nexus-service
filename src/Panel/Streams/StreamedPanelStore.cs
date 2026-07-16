using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Panel.Streams;

/// <summary>
/// Disk-backed map of <c>(USB serial -&gt; StreamedPanelRecord)</c>. Keeps a
/// streamed panel's device record identity and headless config overrides
/// stable across service restarts and device re-attaches.
///
/// File layout: <c>&lt;data-root&gt;/Nexus/devices/transports/streamed-panels.json</c>. Written
/// atomically via <see cref="AtomicJsonFile"/> - a power loss mid-write
/// leaves either the previous file or the new file intact, never a
/// half-written one.
/// </summary>
public sealed class StreamedPanelStore
{
    private readonly string _path;

    public StreamedPanelStore() : this(DefaultPath()) { }

    /// <summary>
    /// Test seam: lets tests point the store at a tmp file under
    /// <c>Path.GetTempPath()</c> without touching real ProgramData.
    /// </summary>
    public StreamedPanelStore(string path)
    {
        _path = path;
    }

    private static string DefaultPath()
        => Path.Combine(Nexus.Service.Media.MediaLibrary.DeviceStoreDir("transports"), "streamed-panels.json");

    /// <summary>
    /// Read the store from disk. Missing file, empty file, or unparseable
    /// JSON all return an empty dictionary - the discovery loop treats "no
    /// records" identically to "fresh install", re-minting profiles/records
    /// on the next attach.
    /// </summary>
    public Dictionary<string, StreamedPanelRecord> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, StreamedPanelRecord>(StringComparer.Ordinal);
            }
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Dictionary<string, StreamedPanelRecord>(StringComparer.Ordinal);
            }
            var parsed = JsonSerializer.Deserialize(
                json,
                PersistenceJsonContext.Default.DictionaryStringStreamedPanelRecord);
            return parsed is null
                ? new Dictionary<string, StreamedPanelRecord>(StringComparer.Ordinal)
                : new Dictionary<string, StreamedPanelRecord>(parsed, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[streamed-panel-store] load failed: {ex.GetType().Name}: {ex.Message}");
            return new Dictionary<string, StreamedPanelRecord>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Persist the full map atomically. Creates the parent directory if
    /// missing so dev builds (no installer-provisioned ProgramData) still work.
    /// </summary>
    public void Save(IReadOnlyDictionary<string, StreamedPanelRecord> records)
    {
        var snapshot = records as Dictionary<string, StreamedPanelRecord>
            ?? new Dictionary<string, StreamedPanelRecord>(records, StringComparer.Ordinal);
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(
                snapshot,
                PersistenceJsonContext.Default.DictionaryStringStreamedPanelRecord);
            AtomicJsonFile.Write(_path, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[streamed-panel-store] save failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
