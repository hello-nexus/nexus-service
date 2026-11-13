using System;
using System.Collections.Generic;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>
/// Wall-clock advancement of cached media positions: GSMTC reports the position
/// a player last SET, and snapshots only travel helper-to-service on session
/// events, so a playing track needs pushing forward by real elapsed time.
/// </summary>
public static class MediaPositionMath
{
    /// <summary>A stamp older than this is treated as unusable rather than a real gap.</summary>
    private static readonly TimeSpan MaxPlausibleStamp = TimeSpan.FromHours(24);

    /// <summary>Position advanced by elapsed time at <paramref name="playbackRate"/>, clamped to the track.</summary>
    public static double Advance(double positionMs, double durationMs, TimeSpan elapsed, double playbackRate = 1.0)
    {
        var advanced = positionMs + Math.Max(0, elapsed.TotalMilliseconds) * Math.Max(0, playbackRate);
        if (advanced < 0) return 0;
        if (durationMs > 0 && advanced > durationMs) return durationMs;
        return advanced;
    }

    /// <summary>
    /// Helper-side decision to push a snapshot for one session. Mirrors what the
    /// service can predict: it advances a playing session at 1x only when the
    /// duration is known, and holds a paused one still.
    /// </summary>
    public static bool ShouldPush(
        bool pushedPlaying, double pushedPositionMs, double durationMs, TimeSpan sincePush,
        bool playing, double livePositionMs, double slackMs)
    {
        if (pushedPlaying != playing) return true;
        if (durationMs <= 0) return false;
        var expected = playing ? Advance(pushedPositionMs, durationMs, sincePush) : pushedPositionMs;
        return Math.Abs(livePositionMs - expected) > slackMs;
    }

    /// <summary>Elapsed time since a player's timeline stamp; zero when the stamp is missing or implausible.</summary>
    public static TimeSpan SinceTimelineStamp(DateTimeOffset lastUpdated, DateTimeOffset now)
    {
        var elapsed = now - lastUpdated;
        if (elapsed < TimeSpan.Zero || elapsed > MaxPlausibleStamp) return TimeSpan.Zero;
        return elapsed;
    }

    /// <summary>
    /// Serve-time view of a cached snapshot with each playing session's position
    /// advanced by <paramref name="elapsed"/>. Copies rather than mutates, so
    /// repeated polls off one snapshot never compound. A session with no known
    /// duration is left alone - nothing would bound how far it advances.
    /// </summary>
    public static Dictionary<string, MediaSession> AdvanceSnapshot(
        Dictionary<string, MediaSession> snapshot, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero || snapshot.Count == 0) return snapshot;
        var result = new Dictionary<string, MediaSession>(snapshot.Count, snapshot.Comparer);
        foreach (var (key, session) in snapshot)
        {
            var p = session.Playback;
            result[key] = p.Playing && !p.Stopped && p.DurationMs > 0
                ? session.WithPositionMs(Advance(p.PositionMs, p.DurationMs, elapsed, p.PlaybackRate))
                : session;
        }
        return result;
    }
}
