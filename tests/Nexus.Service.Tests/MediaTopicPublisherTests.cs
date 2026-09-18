using System.Text;
using Nexus.Service.Activity;
using Nexus.Service.Models.Activity;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

public sealed class MediaTopicPublisherTests
{
    private sealed class FakeMedia : IMediaProvider, IMediaChangeSource
    {
        public Dictionary<string, MediaSession> Sessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int Reads;
        public event Action? Changed;
        public void RaiseChanged() => Changed?.Invoke();
        public IReadOnlyDictionary<string, MediaSession> GetSessions() { Reads++; return Sessions; }
        public void Control(string source, string action) { }
        public void Seek(string source, long positionMs) { }
        public byte[] GetAlbumArt(string source) => Array.Empty<byte>();
    }

    private static MediaSession Session(bool playing, double positionMs = 10_000, string title = "Song")
        => new()
        {
            SourceAppName = "Spotify",
            Song = new MediaSong { Title = title, Artist = "Artist", Album = "Album" },
            Playback = new MediaPlayback { Playing = playing, PositionMs = positionMs, DurationMs = 200_000 },
            Controls = new MediaControls { IsPlayEnabled = !playing, IsPauseEnabled = playing, IsNextEnabled = true },
        };

    private static (MediaTopicPublisher publisher, FakeMedia media, MultiplexHub hub, List<string> frames) Rig()
    {
        var hub = new MultiplexHub();
        var media = new FakeMedia();
        var frames = new List<string>();
        hub.OnBroadcastForTest += (topic, payload) =>
        {
            if (topic == PanelTopics.Media) frames.Add(Encoding.UTF8.GetString(payload.ToArray()));
        };
        var publisher = new MediaTopicPublisher(media, hub);
        return (publisher, media, hub, frames);
    }

    [Fact]
    public void ChangeEvent_WithSubscriber_BroadcastsSessionsInline()
    {
        var (_, media, hub, frames) = Rig();
        using var sub = hub.AddTestSubscription(PanelTopics.Media);
        media.Sessions["Spotify"] = Session(playing: true);

        media.RaiseChanged();

        var frame = Assert.Single(frames);
        Assert.StartsWith("{\"t\":\"media\",\"d\":{\"Spotify\":", frame);
        Assert.Contains("\"playing\":true", frame);
    }

    [Fact]
    public void ChangeEvent_UnchangedState_StillSendsFrame()
    {
        var (publisher, media, hub, frames) = Rig();
        using var sub = hub.AddTestSubscription(PanelTopics.Media);
        media.Sessions["Spotify"] = Session(playing: true);
        publisher.Publish(force: false);

        media.RaiseChanged();

        Assert.Equal(2, frames.Count);
    }

    [Fact]
    public void ChangeEvent_NoSubscribers_DoesNotHitWire()
    {
        var (_, media, _, frames) = Rig();
        media.Sessions["Spotify"] = Session(playing: true);

        media.RaiseChanged();

        Assert.Empty(frames);
    }

    [Fact]
    public void Poll_UnchangedState_SendsNothing()
    {
        var (publisher, media, hub, frames) = Rig();
        using var sub = hub.AddTestSubscription(PanelTopics.Media);
        media.Sessions["Spotify"] = Session(playing: true, positionMs: 10_000);
        publisher.Publish(force: false);

        // Same track, position where the client's own ticker already is.
        media.Sessions = new(StringComparer.OrdinalIgnoreCase) { ["Spotify"] = Session(playing: true, positionMs: 10_400) };
        publisher.Publish(force: false);

        Assert.Single(frames);
    }

    [Fact]
    public void Poll_PlayToPause_SendsFrame()
    {
        var (publisher, media, hub, frames) = Rig();
        using var sub = hub.AddTestSubscription(PanelTopics.Media);
        media.Sessions["Spotify"] = Session(playing: true);
        publisher.Publish(force: false);

        media.Sessions = new(StringComparer.OrdinalIgnoreCase) { ["Spotify"] = Session(playing: false) };
        publisher.Publish(force: false);

        Assert.Equal(2, frames.Count);
        Assert.Contains("\"playing\":false", frames[1]);
    }

    [Fact]
    public void Poll_SessionGone_SendsEmptySet()
    {
        var (publisher, media, hub, frames) = Rig();
        using var sub = hub.AddTestSubscription(PanelTopics.Media);
        media.Sessions["Spotify"] = Session(playing: true);
        publisher.Publish(force: false);

        media.Sessions = new(StringComparer.OrdinalIgnoreCase);
        publisher.Publish(force: false);

        Assert.Equal(2, frames.Count);
        Assert.Equal("{\"t\":\"media\",\"d\":{}}", frames[1]);
    }

    [Fact]
    public void Snapshot_IsBuiltFreshFromProvider()
    {
        var (_, media, hub, _) = Rig();
        media.Sessions["Spotify"] = Session(playing: true, title: "First");
        Assert.True(hub.TryGetTopicSnapshot(PanelTopics.Media, out var first));
        Assert.Contains("First", Encoding.UTF8.GetString(first.ToArray()));

        media.Sessions["Spotify"] = Session(playing: true, title: "Second");
        Assert.True(hub.TryGetTopicSnapshot(PanelTopics.Media, out var second));
        Assert.Contains("Second", Encoding.UTF8.GetString(second.ToArray()));
    }

    [Fact]
    public void SameForDisplay_PositionWithinSlackOfPrediction_IsSame()
    {
        var prev = new Dictionary<string, MediaSession> { ["Spotify"] = Session(playing: true, positionMs: 10_000) };
        var next = new Dictionary<string, MediaSession> { ["Spotify"] = Session(playing: true, positionMs: 15_200) };

        Assert.True(MediaTopicPublisher.SameForDisplay(prev, next, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void SameForDisplay_SeekInsidePlayer_IsChange()
    {
        var prev = new Dictionary<string, MediaSession> { ["Spotify"] = Session(playing: true, positionMs: 10_000) };
        var next = new Dictionary<string, MediaSession> { ["Spotify"] = Session(playing: true, positionMs: 60_000) };

        Assert.False(MediaTopicPublisher.SameForDisplay(prev, next, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void SameForDisplay_PausedPositionMustNotDrift()
    {
        var prev = new Dictionary<string, MediaSession> { ["Spotify"] = Session(playing: false, positionMs: 10_000) };
        var drifted = new Dictionary<string, MediaSession> { ["Spotify"] = Session(playing: false, positionMs: 15_000) };
        var held = new Dictionary<string, MediaSession> { ["Spotify"] = Session(playing: false, positionMs: 10_000) };

        Assert.False(MediaTopicPublisher.SameForDisplay(prev, drifted, TimeSpan.FromSeconds(5)));
        Assert.True(MediaTopicPublisher.SameForDisplay(prev, held, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void SameForDisplay_RateIsIgnored_ClientTicksAtOneX()
    {
        var prev = new Dictionary<string, MediaSession> { ["Spotify"] = Session(playing: true, positionMs: 10_000) };
        prev["Spotify"].Playback.PlaybackRate = 2.0;
        var atTwoX = new Dictionary<string, MediaSession> { ["Spotify"] = Session(playing: true, positionMs: 20_000) };
        atTwoX["Spotify"].Playback.PlaybackRate = 2.0;

        Assert.False(MediaTopicPublisher.SameForDisplay(prev, atTwoX, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void SameForDisplay_ControlFlagFlip_IsChange()
    {
        var prev = new Dictionary<string, MediaSession> { ["Spotify"] = Session(playing: true) };
        var next = new Dictionary<string, MediaSession> { ["Spotify"] = Session(playing: true) };
        next["Spotify"].Controls.IsNextEnabled = false;

        Assert.False(MediaTopicPublisher.SameForDisplay(prev, next, TimeSpan.Zero));
    }
}
