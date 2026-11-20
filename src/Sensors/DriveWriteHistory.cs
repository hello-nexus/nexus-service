using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Sensors;

/// <summary>One cumulative reading, in the unit of the published sensors (GiB, labelled GB on the wire).</summary>
public sealed class DriveWriteSample
{
    public long T { get; set; }
    public double Gb { get; set; }
}

public sealed class DriveWriteSeries
{
    /// <summary>"counter" (the drive's own cumulative sensor) or "rate" (write throughput integrated here).</summary>
    public string Source { get; set; } = "";
    /// <summary>Running total of the integrated rate, in GiB. Unused for "counter".</summary>
    public double Integrated { get; set; }
    public long LastT { get; set; }
    public List<DriveWriteSample> Samples { get; set; } = new();
}

public sealed class DriveWriteHistoryFile
{
    public Dictionary<string, DriveWriteSeries> Drives { get; set; } = new(StringComparer.Ordinal);
}

internal readonly record struct DriveWrittenWindows(double LastHourGb, double LastDayGb);

/// <summary>
/// Turns a drive's cumulative write counter (or, lacking one, its write rate) into data written over the
/// last hour and day. Keeps bounded snapshots, persisted so the day window survives a restart. Compiles on
/// every platform: it sees only values, never LibreHardwareMonitor types.
/// </summary>
internal sealed class DriveWriteHistory
{
    internal const string CounterSource = "counter";
    internal const string RateSource = "rate";
    internal const int SnapshotSeconds = 300;
    internal const int HourSeconds = 3600;
    internal const int DaySeconds = 86400;
    // Longest span one rate reading is assumed to hold; a longer gap (sleep, restart) counts as no writes, a lower bound.
    internal const int MaxIntegrateSeconds = 10;
    private const int MaxSamples = 400;
    private const double BytesPerGb = 1024.0 * 1024.0 * 1024.0;

    private readonly object _lock = new();
    private readonly string? _path;
    private readonly Dictionary<string, DriveWriteSeries> _series;
    private bool _dirty;

    public DriveWriteHistory(string? path)
    {
        _path = path;
        _series = Load(path);
    }

    internal static string DefaultPath() =>
        Path.Combine(Nexus.Service.Media.MediaLibrary.NexusDataDir(), "sensors", "drive-writes.json");

    /// <summary>Null when the drive has neither a usable counter nor a usable write rate.</summary>
    public DriveWrittenWindows? Observe(string driveId, double? counterGb, double? bytesPerSecond, DateTimeOffset now)
    {
        var nowT = now.ToUnixTimeSeconds();
        lock (_lock)
        {
            string source;
            if (counterGb is { } c && double.IsFinite(c) && c > 0)
            {
                source = CounterSource;
            }
            else if (bytesPerSecond is { } r && double.IsFinite(r) && r >= 0)
            {
                source = RateSource;
            }
            else
            {
                return null;
            }

            if (!_series.TryGetValue(driveId, out var series) || series.Source != source)
            {
                series = new DriveWriteSeries { Source = source, LastT = nowT };
                _series[driveId] = series;
                _dirty = true;
            }

            double cumulative;
            if (source == CounterSource)
            {
                cumulative = counterGb!.Value;
            }
            else
            {
                var dt = Math.Clamp(nowT - series.LastT, 0, MaxIntegrateSeconds);
                series.Integrated += bytesPerSecond!.Value * dt / BytesPerGb;
                cumulative = series.Integrated;
            }
            series.LastT = nowT;

            var samples = series.Samples;
            if (samples.Count > 0 && cumulative < samples[^1].Gb)
            {
                samples.Clear();
            }
            if (samples.Count == 0 || nowT - samples[^1].T >= SnapshotSeconds)
            {
                samples.Add(new DriveWriteSample { T = nowT, Gb = cumulative });
                _dirty = true;
                Prune(samples, nowT);
                Save();
            }

            return new DriveWrittenWindows(Delta(samples, cumulative, nowT - HourSeconds), Delta(samples, cumulative, nowT - DaySeconds));
        }
    }

    /// <summary>Counter minus the earliest snapshot inside the window, else the newest one: a lower bound until a full window exists.</summary>
    internal static double Delta(List<DriveWriteSample> samples, double cumulative, long cutoff)
    {
        DriveWriteSample? baseline = null;
        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].T >= cutoff)
            {
                baseline = samples[i];
                break;
            }
        }
        baseline ??= samples.Count > 0 ? samples[^1] : null;
        return baseline is null ? 0 : Math.Max(0, cumulative - baseline.Gb);
    }

    private static void Prune(List<DriveWriteSample> samples, long nowT)
    {
        var cutoff = nowT - DaySeconds - SnapshotSeconds;
        var drop = 0;
        while (drop < samples.Count && samples[drop].T < cutoff)
        {
            drop++;
        }
        drop = Math.Max(drop, samples.Count - MaxSamples);
        if (drop > 0)
        {
            samples.RemoveRange(0, drop);
        }
    }

    // Caller holds _lock. Only runs when a snapshot is added, so a writer thread is never hot.
    private void Save()
    {
        if (_path is null || !_dirty)
        {
            return;
        }
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var file = new DriveWriteHistoryFile { Drives = _series };
            AtomicJsonFile.Write(_path, JsonSerializer.Serialize(file, PersistenceJsonContext.Default.DriveWriteHistoryFile), flushToDisk: false);
            _dirty = false;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[drive-writes] save failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Dictionary<string, DriveWriteSeries> Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                var file = JsonSerializer.Deserialize(File.ReadAllText(path), PersistenceJsonContext.Default.DriveWriteHistoryFile);
                if (file?.Drives is { } drives)
                {
                    return new Dictionary<string, DriveWriteSeries>(drives, StringComparer.Ordinal);
                }
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[drive-writes] load failed: {ex.GetType().Name}: {ex.Message}");
        }
        return new Dictionary<string, DriveWriteSeries>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Adds the two derived sensors for a drive to <paramref name="sensors"/> when it has a cumulative write counter
    /// ("Data Written" family, a Data sensor in GB) or, failing that, a "Write Rate" throughput sensor in B/s.
    /// </summary>
    internal static void Append(DriveWriteHistory history, string hwId, string hwName, List<HardwareSensor> sensors, DateTimeOffset now)
    {
        double? counter = null;
        double? rate = null;
        for (var i = 0; i < sensors.Count; i++)
        {
            var s = sensors[i];
            if (s.Stale)
            {
                continue;
            }
            if (counter is null && string.Equals(s.Type, "Data", StringComparison.OrdinalIgnoreCase) && IsWrittenCounterName(s.Name))
            {
                counter = s.Value;
            }
            else if (rate is null && string.Equals(s.Type, "Throughput", StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Name, "Write Rate", StringComparison.OrdinalIgnoreCase))
            {
                rate = s.Value;
            }
        }
        if (history.Observe(hwId, counter, rate, now) is not { } w)
        {
            return;
        }
        var parent = new SensorParent { Id = hwId, Name = hwName };
        sensors.Add(Build(hwId + "/derived/written-1h", "Data Written (1 h)", w.LastHourGb, parent));
        sensors.Add(Build(hwId + "/derived/written-24h", "Data Written (24 h)", w.LastDayGb, parent));
    }

    internal static bool IsWrittenCounterName(string name) =>
        name.Equals("Data Written", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Total Host Writes", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Total Bytes Written", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Host Writes", StringComparison.OrdinalIgnoreCase);

    private static HardwareSensor Build(string id, string name, double gb, SensorParent parent)
    {
        var value = (float)gb;
        return new HardwareSensor
        {
            Id = id,
            Name = name,
            Type = "Data",
            Value = value,
            Units = "GB",
            Formatted = $"{value:F2} GB",
            FormattedMax = $"{0f:F2} GB",
            FormattedMin = $"{0f:F2} GB",
            Parent = parent,
        };
    }
}
