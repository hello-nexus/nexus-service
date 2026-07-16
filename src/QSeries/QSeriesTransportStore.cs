using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.QSeries;

/// <summary>
/// Disk-backed map of <c>(USB serial → TCP transport record)</c>. Lets the
/// watcher remember which Q-series devices it has promoted to adb-over-TCP
/// so a service restart (or USB drop) can re-establish the panel transport
/// without waiting for the device to re-enumerate over USB.
///
/// File layout: <c>&lt;data-root&gt;/Nexus/devices/transports/qseries-transports.json</c>. Written
/// atomically via <see cref="AtomicJsonFile"/> - a power loss mid-write
/// leaves either the previous file or the new file intact, never a
/// half-written one.
///
/// Concurrency: this is reached from a single <see cref="System.Threading.Tasks.Task"/>
/// per service (the watcher's tick loop), so no internal locking. If the
/// service is ever stopped and another writer touches the file in parallel,
/// the next read picks up whichever version of the file survived.
/// </summary>
public sealed class QSeriesTransportStore
{
    private readonly string _path;

    public QSeriesTransportStore() : this(DefaultPath()) { }

    /// <summary>
    /// Test seam: lets tests point the store at a tmp file under
    /// <c>Path.GetTempPath()</c> without touching real ProgramData.
    /// </summary>
    public QSeriesTransportStore(string path)
    {
        _path = path;
    }

    private static string DefaultPath()
        => Path.Combine(Nexus.Service.Media.MediaLibrary.DeviceStoreDir("transports"), "qseries-transports.json");

    /// <summary>
    /// Read the store from disk. Missing file, empty file, or unparseable
    /// JSON all return an empty dictionary - the watcher treats "no
    /// records" identically to "fresh install", which is the correct
    /// fail-soft behavior here (worst case: one extra USB-bootstrap on
    /// the next attach).
    /// </summary>
    public Dictionary<string, QSeriesTransportRecord> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new Dictionary<string, QSeriesTransportRecord>(StringComparer.Ordinal);
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, QSeriesTransportRecord>(StringComparer.Ordinal);
            var parsed = JsonSerializer.Deserialize(
                json,
                PersistenceJsonContext.Default.DictionaryStringQSeriesTransportRecord);
            return parsed is null
                ? new Dictionary<string, QSeriesTransportRecord>(StringComparer.Ordinal)
                : new Dictionary<string, QSeriesTransportRecord>(parsed, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // A corrupt store should not crash the watcher. Log and treat
            // as empty; the next save overwrites the bad file.
            Console.Error.WriteLine($"[qseries-transport-store] load failed: {ex.GetType().Name}: {ex.Message}");
            return new Dictionary<string, QSeriesTransportRecord>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Persist the full map atomically. Creating <c>%ProgramData%\Nexus\</c>
    /// on first write is harmless even when running unelevated because
    /// the installer creates that directory with Authenticated Users +
    /// Modify rights; if the directory is missing entirely we create it
    /// here so dev builds still work.
    /// </summary>
    public void Save(IReadOnlyDictionary<string, QSeriesTransportRecord> records)
    {
        // Materialize to a concrete Dictionary so the source generator
        // only needs the one accessor registered. IReadOnlyDictionary
        // doesn't have its own source-gen accessor in this project.
        var snapshot = records as Dictionary<string, QSeriesTransportRecord>
            ?? new Dictionary<string, QSeriesTransportRecord>(records, StringComparer.Ordinal);
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(
                snapshot,
                PersistenceJsonContext.Default.DictionaryStringQSeriesTransportRecord);
            AtomicJsonFile.Write(_path, json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[qseries-transport-store] save failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
