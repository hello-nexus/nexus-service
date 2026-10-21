using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>The effective upload knobs for one upload, with the id of the settings version they came from.</summary>
public sealed record GlacialLcdTuningValues(
    int Id, double MaxFps, int PacePercent, bool ReplyDriven, int ScalePercent, string ScaleMode, int JpegQuality, int ChunkKb, bool DoubleBuffer);

/// <summary>A partial change to the knobs; only the fields set are applied.</summary>
public sealed class GlacialLcdTuningChange
{
    public double? MaxFps { get; set; }
    public int? PacePercent { get; set; }
    public bool? ReplyDriven { get; set; }
    public int? ScalePercent { get; set; }
    public string? ScaleMode { get; set; }
    public int? JpegQuality { get; set; }
    public int? ChunkKb { get; set; }
    public bool? DoubleBuffer { get; set; }
}

public sealed record GlacialLcdTuningStats(
    int UploadsOk, int UploadsFailed, long LastUploadMs, double UploadsPerSec, int LastFrameBytes, long LastEncodeMs,
    string? LastError, DateTimeOffset? LastErrorAt, bool Stalled);

/// <summary>Settings and counters of the Glacial LCD Nexus panel upload, read by the driver at the start of each upload.</summary>
public sealed class GlacialLcdTuning
{
    public const double MinMaxFps = 0.2;
    public const double MaxMaxFps = 30;
    public const int MaxPace = 100;
    public const int MinScale = 25;
    public const int MinQuality = 30;
    public const int MaxQuality = 95;
    public const int MaxChunkKb = 16;
    public const string ScaleSend = "send";
    public const string ScaleUpscale = "upscale";

    /// <summary>The last upload settings that worked in the field.</summary>
    public const int DefaultPacePercent = 19;
    public const int DefaultScalePercent = 100;
    public const int DefaultJpegQuality = 65;
    public const int DefaultChunkKb = 4;

    private const int RateWindowMs = 10_000;

    private readonly IConfigStore _store;
    private readonly Func<long> _clock;
    private readonly object _lock = new();
    private readonly Queue<long> _completions = new();
    private int _version = 1;
    private int _ok;
    private int _failed;
    private long _lastUploadMs;
    private int _lastBytes;
    private long _lastEncodeMs;
    private string? _lastError;
    private DateTimeOffset? _lastErrorAt;
    private volatile bool _stalled;

    public GlacialLcdTuning(IConfigStore store, Func<long>? clock = null)
    {
        _store = store;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public bool Stalled => _stalled;

    public GlacialLcdTuningValues Current()
    {
        GlacialLcdTuningSettings stored = new();
        int legacyPace = 0;
        _store.Read(s =>
        {
            stored = s.Devices.GlacialLcdTuning ?? new();
            legacyPace = s.Devices.GlacialLcdUploadPacePercent;
        });
        int pace = stored.PacePercent ?? (legacyPace > 0 ? legacyPace : DefaultPacePercent);
        int version;
        lock (_lock)
        {
            version = _version;
        }
        return new GlacialLcdTuningValues(
            version,
            Math.Clamp(stored.MaxFps ?? MaxMaxFps, MinMaxFps, MaxMaxFps),
            Math.Clamp(pace, 0, MaxPace),
            stored.ReplyDriven ?? false,
            Math.Clamp(stored.ScalePercent ?? DefaultScalePercent, MinScale, 100),
            stored.ScaleMode == ScaleUpscale ? ScaleUpscale : ScaleSend,
            Math.Clamp(stored.JpegQuality ?? DefaultJpegQuality, MinQuality, MaxQuality),
            Math.Clamp(stored.ChunkKb ?? DefaultChunkKb, 1, MaxChunkKb),
            stored.DoubleBuffer ?? false);
    }

    /// <summary>The message for the first field out of range, or null when the change is valid.</summary>
    public static string? Validate(GlacialLcdTuningChange c)
    {
        if (c.MaxFps is double f && !(f >= MinMaxFps && f <= MaxMaxFps))
        {
            return "maxFps out of range";
        }
        if (c.PacePercent is int p && (p < 0 || p > MaxPace))
        {
            return "pacePercent out of range";
        }
        if (c.ScalePercent is int sc && (sc < MinScale || sc > 100))
        {
            return "scalePercent out of range";
        }
        if (c.ScaleMode is string m && m != ScaleSend && m != ScaleUpscale)
        {
            return "scaleMode must be send or upscale";
        }
        if (c.JpegQuality is int q && (q < MinQuality || q > MaxQuality))
        {
            return "jpegQuality out of range";
        }
        if (c.ChunkKb is int k && (k < 1 || k > MaxChunkKb))
        {
            return "chunkKb out of range";
        }
        return null;
    }

    /// <summary>Applies a validated change, logging each field's old and new value; the next upload uses it.</summary>
    public GlacialLcdTuningValues Apply(GlacialLcdTuningChange c)
    {
        var before = Current();
        _store.Update(s =>
        {
            var t = s.Devices.GlacialLcdTuning ??= new();
            if (c.MaxFps.HasValue) { t.MaxFps = c.MaxFps; }
            if (c.PacePercent.HasValue) { t.PacePercent = c.PacePercent; }
            if (c.ReplyDriven.HasValue) { t.ReplyDriven = c.ReplyDriven; }
            if (c.ScalePercent.HasValue) { t.ScalePercent = c.ScalePercent; }
            if (c.ScaleMode is not null) { t.ScaleMode = c.ScaleMode; }
            if (c.JpegQuality.HasValue) { t.JpegQuality = c.JpegQuality; }
            if (c.ChunkKb.HasValue) { t.ChunkKb = c.ChunkKb; }
            if (c.DoubleBuffer.HasValue) { t.DoubleBuffer = c.DoubleBuffer; }
        });
        int version;
        lock (_lock)
        {
            version = ++_version;
        }
        var after = Current();
        var changes = new List<string>();
        Diff(changes, "maxFps", before.MaxFps, after.MaxFps);
        Diff(changes, "pacePercent", before.PacePercent, after.PacePercent);
        Diff(changes, "replyDriven", before.ReplyDriven, after.ReplyDriven);
        Diff(changes, "scalePercent", before.ScalePercent, after.ScalePercent);
        Diff(changes, "scaleMode", before.ScaleMode, after.ScaleMode);
        Diff(changes, "jpegQuality", before.JpegQuality, after.JpegQuality);
        Diff(changes, "chunkKb", before.ChunkKb, after.ChunkKb);
        Diff(changes, "doubleBuffer", before.DoubleBuffer, after.DoubleBuffer);
        GlacialLcdLog.Write($"settings id={version} changed: {(changes.Count == 0 ? "nothing" : string.Join(", ", changes))}");
        return after;
    }

    private static void Diff<T>(List<string> changes, string name, T before, T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
        {
            changes.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {before} -> {after}"));
        }
    }

    public void UploadOk(long uploadMs, int frameBytes, long encodeMs)
    {
        lock (_lock)
        {
            _ok++;
            _lastUploadMs = uploadMs;
            _lastBytes = frameBytes;
            _lastEncodeMs = encodeMs;
            _completions.Enqueue(_clock());
        }
    }

    public void UploadFailed(string error)
    {
        lock (_lock)
        {
            _failed++;
            _lastError = error;
            _lastErrorAt = DateTimeOffset.UtcNow;
        }
    }

    public void Stall(string reason)
    {
        UploadFailed("stalled: " + reason);
        _stalled = true;
        GlacialLcdLog.Write($"STALL {reason}; uploads stopped until resumed");
    }

    public void Resume()
    {
        if (_stalled)
        {
            GlacialLcdLog.Write("stall cleared");
        }
        _stalled = false;
    }

    public void ResetStats()
    {
        lock (_lock)
        {
            _ok = 0;
            _failed = 0;
            _lastUploadMs = 0;
            _lastBytes = 0;
            _lastEncodeMs = 0;
            _lastError = null;
            _lastErrorAt = null;
            _completions.Clear();
        }
        GlacialLcdLog.Write("stats reset");
    }

    public GlacialLcdTuningStats Stats()
    {
        lock (_lock)
        {
            long now = _clock();
            while (_completions.Count > 0 && now - _completions.Peek() > RateWindowMs)
            {
                _completions.Dequeue();
            }
            return new GlacialLcdTuningStats(
                _ok, _failed, _lastUploadMs, _completions.Count * 1000.0 / RateWindowMs, _lastBytes, _lastEncodeMs, _lastError, _lastErrorAt, _stalled);
        }
    }
}
