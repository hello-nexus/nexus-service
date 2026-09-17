using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>Slideshow settings for the panel's custom-upload library; the same four
/// knobs the gallery widget and the panel background slideshow expose.</summary>
public sealed class TryxSlideshowConfig
{
    public const int MinIntervalSec = 5;
    public const int MaxIntervalSec = 3600;
    public const int DefaultIntervalSec = 10;

    public bool Enabled { get; set; }
    /// <summary>Seconds each clip holds, <see cref="MinIntervalSec"/>..<see cref="MaxIntervalSec"/>.</summary>
    public int IntervalSec { get; set; } = DefaultIntervalSec;
    public bool Shuffle { get; set; }
    /// <summary>A clip plays whole (repeating until the interval has passed) instead of
    /// being cut at the interval.</summary>
    public bool FinishVideos { get; set; } = true;

    public static int ClampInterval(int seconds) => Math.Clamp(seconds, MinIntervalSec, MaxIntervalSec);
}

/// <summary>
/// Paces the custom-media slideshow: decides when the panel is due for the next clip
/// and which one. The hub owns the panel writes and calls <see cref="Tick"/> from the
/// heartbeat; every manual selection re-arms the hold via <see cref="Rearm"/> so a pick
/// gets its full interval. The panel loops whatever clip it shows and reports no
/// playback events, so "finish videos" is computed from the clip's probed duration:
/// whole plays until the interval has passed, like the gallery counts `ended` events.
/// </summary>
public sealed class TryxSlideshow
{
    private readonly Func<string, double> _durationSec;
    private readonly Func<double> _random;
    private readonly object _lock = new();
    private TryxSlideshowConfig _config = new();
    private long _deadlineMs = long.MaxValue;
    private string _current = "";
    // Shuffle walks one lap of every clip once, rebuilt when the library changes or
    // the lap runs out; a lap never opens on the clip just shown.
    private List<string> _lap = new();
    private int _lapPos = -1;

    public TryxSlideshow(Func<string, double> durationSec, Func<double>? random = null)
    {
        _durationSec = durationSec;
        _random = random ?? Random.Shared.NextDouble;
    }

    public TryxSlideshowConfig Config
    {
        get { lock (_lock) return Copy(_config); }
    }

    /// <summary>Applies new settings. The clip on screen keeps a full hold, except that
    /// enabling the slideshow while something outside the library (a preset) is showing
    /// advances on the next tick, so the toggle visibly starts the cycle.</summary>
    public void Configure(TryxSlideshowConfig config, string current, IReadOnlyList<string> library, long nowMs)
    {
        lock (_lock)
        {
            var wasEnabled = _config.Enabled;
            _config = Copy(config);
            _config.IntervalSec = TryxSlideshowConfig.ClampInterval(_config.IntervalSec);
            _current = current;
            _lap.Clear();
            _lapPos = -1;
            var foreign = IndexOf(library, current) < 0;
            _deadlineMs = _config.Enabled && !wasEnabled && foreign ? nowMs : nowMs + HoldMsLocked(current);
        }
    }

    /// <summary>A clip (or preset) was just put on screen: hold it for a full interval.</summary>
    public void Rearm(string current, long nowMs)
    {
        lock (_lock)
        {
            _current = current;
            _deadlineMs = nowMs + HoldMsLocked(current);
        }
    }

    /// <summary>The clip to switch to now, or null while the current one still holds, the
    /// slideshow is off, or the library has fewer than two clips. The caller selects it and
    /// re-arms through <see cref="Rearm"/>. <paramref name="library"/> is only read once due.</summary>
    public string? Tick(Func<IReadOnlyList<string>> library, long nowMs)
    {
        lock (_lock)
        {
            if (!_config.Enabled || nowMs < _deadlineMs) return null;
            // Push the deadline out first so a failed select, or a library with nothing to
            // cycle, is re-checked at the interval cadence rather than every heartbeat.
            _deadlineMs = nowMs + _config.IntervalSec * 1000L;
            var clips = library();
            if (clips.Count < 2) return null;
            return _config.Shuffle ? NextShuffledLocked(clips) : NextInOrderLocked(clips);
        }
    }

    private string NextInOrderLocked(IReadOnlyList<string> library)
    {
        // A current clip outside the library (a preset, or one just deleted) starts the
        // cycle from the top.
        var index = IndexOf(library, _current);
        return library[(index + 1) % library.Count];
    }

    private string NextShuffledLocked(IReadOnlyList<string> library)
    {
        for (var hops = 0; hops <= library.Count; hops++)
        {
            if (!SameSet(_lap, library) || _lapPos + 1 >= _lap.Count)
            {
                _lap = ShuffledLap(library, _current);
                _lapPos = -1;
            }
            _lapPos++;
            var next = _lap[_lapPos];
            if (!string.Equals(next, _current, StringComparison.Ordinal)) return next;
        }
        return NextInOrderLocked(library);
    }

    /// <summary>Every clip once in random order; <paramref name="avoid"/> never leads
    /// when there is an alternative, so a lap boundary never repeats a clip.</summary>
    internal List<string> ShuffledLap(IReadOnlyList<string> library, string avoid)
    {
        var lap = new List<string>(library);
        for (var i = lap.Count - 1; i > 0; i--)
        {
            var j = (int)Math.Floor(_random() * (i + 1));
            (lap[i], lap[j]) = (lap[j], lap[i]);
        }
        if (lap.Count > 1 && string.Equals(lap[0], avoid, StringComparison.Ordinal))
        {
            (lap[0], lap[1]) = (lap[1], lap[0]);
        }
        return lap;
    }

    // Whole plays until the interval has passed: a 3 s clip on a 10 s interval holds for
    // four plays (12 s), a 20 s clip on a 5 s interval for one. An unknown duration (a clip
    // Nexus did not upload, or a preset) holds for the plain interval.
    private long HoldMsLocked(string name)
    {
        var intervalMs = _config.IntervalSec * 1000L;
        if (!_config.FinishVideos) return intervalMs;
        var duration = _durationSec(name);
        if (!double.IsFinite(duration) || duration <= 0) return intervalMs;
        var durationMs = (long)Math.Round(duration * 1000);
        if (durationMs <= 0) return intervalMs;
        var plays = Math.Max(1, (intervalMs + durationMs - 1) / durationMs);
        return plays * durationMs;
    }

    private static int IndexOf(IReadOnlyList<string> library, string name)
    {
        for (var i = 0; i < library.Count; i++)
        {
            if (string.Equals(library[i], name, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private static bool SameSet(List<string> lap, IReadOnlyList<string> library)
    {
        if (lap.Count != library.Count) return false;
        var set = new HashSet<string>(lap, StringComparer.Ordinal);
        foreach (var name in library)
        {
            if (!set.Contains(name)) return false;
        }
        return true;
    }

    private static TryxSlideshowConfig Copy(TryxSlideshowConfig c) => new()
    {
        Enabled = c.Enabled,
        IntervalSec = c.IntervalSec,
        Shuffle = c.Shuffle,
        FinishVideos = c.FinishVideos,
    };
}
