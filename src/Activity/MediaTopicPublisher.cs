using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Models.Activity;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Activity;

/// <summary>
/// Pushes the media session set on the <c>media</c> multiplex topic so the
/// media widget does not poll GET /api/media. One poll per box, only while a
/// client is subscribed, and a frame only when something the widget renders
/// changed. A provider that is also an <see cref="IMediaChangeSource"/>
/// (Windows, fed by the helper's GSMTC events) publishes on the event; macOS
/// and Linux have no event API, so the poll is their latency floor.
/// </summary>
public sealed class MediaTopicPublisher : BackgroundService
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The widget ticks the position locally between frames, so a position
    /// that sits where the last frame predicted carries nothing new. Anything
    /// further off (a seek inside the player, a track loop) is a change.
    /// </summary>
    internal const double PositionSlackMs = 1500;

    private readonly IMediaProvider _media;
    private readonly MultiplexHub _hub;
    private readonly object _lock = new();
    private IReadOnlyDictionary<string, MediaSession>? _last;
    private long _lastAt;

    public MediaTopicPublisher(IMediaProvider media, MultiplexHub hub)
    {
        _media = media;
        _hub = hub;
        _hub.RegisterSnapshotProvider(PanelTopics.Media, GetSnapshot);
        // The provider only raises this for a real player event, so the frame
        // goes out even when the diff would call it unchanged: a seek shorter
        // than the position slack still has to release the widget's seek hold.
        if (media is IMediaChangeSource source)
            source.Changed += () => Publish(force: true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_hub.TopicHasSubscribers(PanelTopics.Media))
                Publish(force: false);

            try
            { await Task.Delay(PollInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>
    /// Built fresh rather than replayed: a subscriber arriving minutes into a
    /// track would otherwise start its local ticker from the position of the
    /// last frame that changed anything.
    /// </summary>
    private ReadOnlyMemory<byte>? GetSnapshot()
    {
        try
        {
            lock (_lock)
            {
                var sessions = _media.GetSessions();
                _last = sessions;
                _lastAt = Stopwatch.GetTimestamp();
                return Build(sessions);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[media-topic] snapshot failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Called from the poll loop, the provider's change event and tests. The
    /// read, the diff, the store and the broadcast kick-off all run under one
    /// lock: a read taken before the lock could otherwise lose to a fresher
    /// one and be sent after it, leaving every subscriber on the older state.
    /// BroadcastTopicAsync returns at its first socket await and each client
    /// queues sends in order, so the wire order matches the stored anchor.
    /// </summary>
    internal void Publish(bool force)
    {
        try
        {
            lock (_lock)
            {
                var sessions = _media.GetSessions();
                if (!force && _last is not null && SameForDisplay(_last, sessions, Stopwatch.GetElapsedTime(_lastAt)))
                    return;
                _last = sessions;
                _lastAt = Stopwatch.GetTimestamp();
                if (!_hub.TopicHasSubscribers(PanelTopics.Media))
                    return;
                _ = _hub.BroadcastTopicAsync(PanelTopics.Media, Build(sessions));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[media-topic] publish failed: {ex.Message}");
        }
    }

    private static ReadOnlyMemory<byte> Build(IReadOnlyDictionary<string, MediaSession> sessions)
        => WsEnvelope.Build(PanelTopics.Media, sessions, AppJsonContext.Default.IReadOnlyDictionaryStringMediaSession);

    /// <summary>
    /// True when <paramref name="next"/> renders the same as <paramref name="prev"/>
    /// advanced by <paramref name="elapsed"/>: every field the widget shows is
    /// equal and the position is within <see cref="PositionSlackMs"/> of where
    /// the client's own ticker already is. That ticker runs at 1x and knows no
    /// rate, so the prediction does too; a session at any other rate frames
    /// every slack's worth of drift, which is what keeps it anchored.
    /// </summary>
    internal static bool SameForDisplay(
        IReadOnlyDictionary<string, MediaSession> prev,
        IReadOnlyDictionary<string, MediaSession> next,
        TimeSpan elapsed)
    {
        if (prev.Count != next.Count) return false;
        foreach (var (key, n) in next)
        {
            if (!prev.TryGetValue(key, out var p)) return false;
            if (p.IsFocused != n.IsFocused) return false;
            if (p.Song.Title != n.Song.Title || p.Song.Artist != n.Song.Artist || p.Song.Album != n.Song.Album) return false;

            var pp = p.Playback;
            var np = n.Playback;
            if (pp.Playing != np.Playing || pp.Stopped != np.Stopped || pp.Shuffled != np.Shuffled
                || pp.RepeatMode != np.RepeatMode || pp.DurationMs != np.DurationMs || pp.PlaybackRate != np.PlaybackRate)
            {
                return false;
            }
            var expected = pp.Playing && !pp.Stopped && pp.DurationMs > 0
                ? MediaPositionMath.Advance(pp.PositionMs, pp.DurationMs, elapsed, playbackRate: 1.0)
                : pp.PositionMs;
            if (Math.Abs(np.PositionMs - expected) > PositionSlackMs) return false;

            var pc = p.Controls;
            var nc = n.Controls;
            if (pc.IsPrevEnabled != nc.IsPrevEnabled || pc.IsNextEnabled != nc.IsNextEnabled
                || pc.IsShuffleEnabled != nc.IsShuffleEnabled || pc.IsRepeatModeEnabled != nc.IsRepeatModeEnabled
                || pc.IsPlayEnabled != nc.IsPlayEnabled || pc.IsPauseEnabled != nc.IsPauseEnabled
                || pc.IsSeekEnabled != nc.IsSeekEnabled)
            {
                return false;
            }
        }
        return true;
    }
}
