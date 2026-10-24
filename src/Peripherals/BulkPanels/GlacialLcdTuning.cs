using System;
using System.Collections.Generic;
using System.Globalization;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>The effective upload knobs for one upload, with the id of the settings version they came from.</summary>
public sealed record GlacialLcdTuningValues(
    int Id, double MaxFps, int PacePercent, bool ReplyDriven, int ScalePercent, string ScaleMode, int JpegQuality, int ChunkKb, bool DoubleBuffer, bool CloseWriteMode, bool RawStream);

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
    public bool? CloseWriteMode { get; set; }
    public bool? RawStream { get; set; }
}

/// <summary>Counters of the display's replies: how many arrived, how many waits ran out, and how long the last upload spent waiting.</summary>
public sealed record GlacialLcdAckStats(
    int Replies, int Timeouts, long LastUploadAckWaitMs, long LastCommitWaitMs, int LastUploadReplies, int LastUploadTimeouts);

public sealed record GlacialLcdTuningStats(
    int UploadsOk, int UploadsFailed, long LastUploadMs, double UploadsPerSec, int LastFrameBytes, long LastEncodeMs,
    string? LastError, DateTimeOffset? LastErrorAt, bool Stalled, GlacialLcdAckStats Acks, int RawFramesOk, int RawFramesFailed, bool RawActive);

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
    private int _ackReplies;
    private int _ackTimeouts;
    private long _lastAckWaitMs;
    private long _lastCommitWaitMs;
    private int _lastUploadReplies;
    private int _lastUploadTimeouts;
    private int _rawOk;
    private int _rawFailed;
    private volatile bool _rawActive;
    private int _rawEnables;

    public GlacialLcdTuning(IConfigStore store, Func<long>? clock = null)
    {
        _store = store;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public bool Stalled => _stalled;

    /// <summary>How many times the raw stream knob has been turned on; lets the driver see an off-and-on between two of its passes.</summary>
    public int RawEnables => Volatile.Read(ref _rawEnables);

    public GlacialLcdTuningValues Current()
    {
        double? maxFps = null;
        int? pace = null, scale = null, quality = null, chunk = null;
        bool? replyDriven = null, doubleBuffer = null, closeWriteMode = null, rawStream = null;
        string? scaleMode = null;
        int legacyPace = 0;
        _store.Read(s =>
        {
            var t = s.Devices.GlacialLcdTuning;
            maxFps = t?.MaxFps;
            pace = t?.PacePercent;
            scale = t?.ScalePercent;
            quality = t?.JpegQuality;
            chunk = t?.ChunkKb;
            replyDriven = t?.ReplyDriven;
            doubleBuffer = t?.DoubleBuffer;
            closeWriteMode = t?.CloseWriteMode;
            rawStream = t?.RawStream;
            scaleMode = t?.ScaleMode;
            legacyPace = s.Devices.GlacialLcdUploadPacePercent;
        });
        int version;
        lock (_lock)
        {
            version = _version;
        }
        return new GlacialLcdTuningValues(
            version,
            Math.Clamp(maxFps ?? MaxMaxFps, MinMaxFps, MaxMaxFps),
            Math.Clamp(pace ?? (legacyPace > 0 ? legacyPace : DefaultPacePercent), 0, MaxPace),
            replyDriven ?? true,
            Math.Clamp(scale ?? DefaultScalePercent, MinScale, 100),
            scaleMode == ScaleUpscale ? ScaleUpscale : ScaleSend,
            Math.Clamp(quality ?? DefaultJpegQuality, MinQuality, MaxQuality),
            Math.Clamp(chunk ?? DefaultChunkKb, 1, MaxChunkKb),
            doubleBuffer ?? false,
            closeWriteMode ?? true,
            rawStream ?? false);
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
            var t = s.Devices.GlacialLcdTuning ??= new GlacialLcdTuningSettings();
            if (c.MaxFps.HasValue) { t.MaxFps = c.MaxFps; }
            if (c.PacePercent.HasValue) { t.PacePercent = c.PacePercent; }
            if (c.ReplyDriven.HasValue) { t.ReplyDriven = c.ReplyDriven; }
            if (c.ScalePercent.HasValue) { t.ScalePercent = c.ScalePercent; }
            if (c.ScaleMode is not null) { t.ScaleMode = c.ScaleMode; }
            if (c.JpegQuality.HasValue) { t.JpegQuality = c.JpegQuality; }
            if (c.ChunkKb.HasValue) { t.ChunkKb = c.ChunkKb; }
            if (c.DoubleBuffer.HasValue) { t.DoubleBuffer = c.DoubleBuffer; }
            if (c.CloseWriteMode.HasValue) { t.CloseWriteMode = c.CloseWriteMode; }
            if (c.RawStream.HasValue) { t.RawStream = c.RawStream; }
        });
        int version;
        lock (_lock)
        {
            version = ++_version;
            if (c.RawStream == true)
            {
                _rawEnables++;
            }
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
        Diff(changes, "closeWriteMode", before.CloseWriteMode, after.CloseWriteMode);
        Diff(changes, "rawStream", before.RawStream, after.RawStream);
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

    /// <summary>Adds one upload's reply counts and wait times; the last-upload figures are replaced.</summary>
    public void RecordAcks(int replies, int timeouts, long ackWaitMs, long commitWaitMs)
    {
        lock (_lock)
        {
            _ackReplies += replies;
            _ackTimeouts += timeouts;
            _lastUploadReplies = replies;
            _lastUploadTimeouts = timeouts;
            _lastAckWaitMs = ackWaitMs;
            _lastCommitWaitMs = commitWaitMs;
        }
    }

    public void RawFrame(bool ok)
    {
        lock (_lock)
        {
            if (ok)
            {
                _rawOk++;
            }
            else
            {
                _rawFailed++;
            }
        }
    }

    public void SetRawActive(bool active) => _rawActive = active;

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
            _ackReplies = 0;
            _ackTimeouts = 0;
            _lastAckWaitMs = 0;
            _lastCommitWaitMs = 0;
            _lastUploadReplies = 0;
            _lastUploadTimeouts = 0;
            _rawOk = 0;
            _rawFailed = 0;
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
                _ok, _failed, _lastUploadMs, _completions.Count * 1000.0 / RateWindowMs, _lastBytes, _lastEncodeMs, _lastError, _lastErrorAt, _stalled,
                new GlacialLcdAckStats(_ackReplies, _ackTimeouts, _lastAckWaitMs, _lastCommitWaitMs, _lastUploadReplies, _lastUploadTimeouts),
                _rawOk, _rawFailed, _rawActive);
        }
    }
}
