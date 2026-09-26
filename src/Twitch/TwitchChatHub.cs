using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Service.Models.Twitch;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Twitch;

/// <summary>
/// Holds one anonymous (<c>justinfan</c>, read-only, no account) connection to
/// Twitch chat and fans messages out on the <c>twitch/chat/{channel}</c> topics.
/// A channel is joined on its topic's first subscriber and parted on its last;
/// the socket is only open while some channel is watched. It runs service-side
/// rather than in the panel because a Q-series panel reaches the service over a
/// USB reverse tunnel and has no route to the internet of its own.
/// </summary>
public sealed class TwitchChatHub : BackgroundService
{
    public const string TopicPrefix = "twitch/chat/";

    private const string TwitchIrcUrl = "wss://irc-ws.chat.twitch.tv:443";
    /// <summary>Messages retained per channel; also what a late subscriber is handed.</summary>
    private const int BufferCap = 120;
    /// <summary>Frames are batched to this cadence so a busy channel cannot spam the socket.</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan BackoffMin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BackoffMax = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(1);
    /// <summary>How long a JOIN has to draw a ROOMSTATE before the channel is called nonexistent.</summary>
    private static readonly TimeSpan ExistsProbeWindow = TimeSpan.FromSeconds(6);

    private readonly MultiplexHub _hub;
    private readonly ILogger<TwitchChatHub> _logger;

    private readonly object _lock = new();
    private readonly Dictionary<string, ChannelState> _channels = new(StringComparer.Ordinal);
    /// <summary>Clock-seeded so seq keeps rising across service restarts, which a clear's watermark relies on.</summary>
    private long _seq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
    private bool _socketConnected;

    /// <summary>Channels a JOIN has been written for on the current socket - guarded by <see cref="_lock"/>.</summary>
    private readonly HashSet<string> _sentJoins = new(StringComparer.Ordinal);

    /// <summary>Wakes the run loop when the watched-channel set changes.</summary>
    private readonly SemaphoreSlim _wake = new(0, 1);

    public TwitchChatHub(MultiplexHub hub, ILogger<TwitchChatHub> logger)
    {
        _hub = hub;
        _logger = logger;
        _hub.OnTopicFirstSubscriber += OnFirstSubscriber;
        _hub.OnTopicLastUnsubscriber += OnLastUnsubscriber;
    }

    private sealed class ChannelState
    {
        public List<TwitchChatMessage> Buffer { get; } = new();
        /// <summary>Appended-since-last-flush; drained by the flush pass.</summary>
        public List<TwitchChatMessage> Pending { get; } = new();
        public bool Joined { get; set; }
        /// <summary>Set once the JOIN has been written for the current socket.</summary>
        public bool JoinSent { get; set; }
        /// <summary>When the JOIN went out, so a silent channel can be timed out.</summary>
        public DateTime JoinSentAtUtc { get; set; }
        /// <summary>Null until the probe resolves; see TwitchChatFrame.Exists.</summary>
        public bool? Exists { get; set; }
    }

    /// <summary>Channel logins are <c>[a-zA-Z0-9_]{1,25}</c>; anything else never reaches the wire.</summary>
    public static bool IsValidChannel(string channel)
    {
        if (channel.Length == 0 || channel.Length > 25)
        {
            return false;
        }
        foreach (var c in channel)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    public static string TopicFor(string channel) => TopicPrefix + channel.ToLowerInvariant();

    private static string? ChannelFromTopic(string topic)
    {
        if (!topic.StartsWith(TopicPrefix, StringComparison.Ordinal))
        {
            return null;
        }
        var channel = topic[TopicPrefix.Length..];
        return IsValidChannel(channel) ? channel.ToLowerInvariant() : null;
    }

    private void OnFirstSubscriber(string topic)
    {
        var channel = ChannelFromTopic(topic);
        if (channel is null)
        {
            return;
        }

        lock (_lock)
        {
            if (!_channels.ContainsKey(channel))
            {
                _channels[channel] = new ChannelState();
            }
        }

        // The hub runs its snapshot pass straight after this event on the same
        // subscribe, so a late subscriber gets the retained buffer at once.
        _hub.RegisterSnapshotProvider(topic, () => BuildSnapshot(channel));
        Wake();
    }

    private void OnLastUnsubscriber(string topic)
    {
        var channel = ChannelFromTopic(topic);
        if (channel is null)
        {
            return;
        }

        _hub.UnregisterSnapshotProvider(topic);
        lock (_lock)
        {
            _channels.Remove(channel);
        }
        Wake();
    }

    private ReadOnlyMemory<byte>? BuildSnapshot(string channel)
    {
        TwitchChatFrame frame;
        lock (_lock)
        {
            if (!_channels.TryGetValue(channel, out var state))
            {
                return null;
            }
            frame = new TwitchChatFrame
            {
                Channel = channel,
                Connected = _socketConnected && state.Joined,
                Exists = state.Exists,
                Messages = new List<TwitchChatMessage>(state.Buffer),
            };
        }
        return WsEnvelope.Build(TopicPrefix + channel, frame, AppJsonContext.Default.TwitchChatFrame);
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; the loop will observe the current state.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down - a hub event can race Dispose.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = BackoffMin;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!HasWatchers())
            {
                await WaitForWorkAsync(IdlePoll, stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await RunConnectionAsync(stoppingToken).ConfigureAwait(false);
                backoff = BackoffMin;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[twitch] chat connection ended: {Message}", ex.Message);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, BackoffMax.Ticks));
            }

            MarkDisconnected();
            if (HasWatchers())
            {
                await WaitForWorkAsync(backoff, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task WaitForWorkAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _wake.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private bool HasWatchers()
    {
        lock (_lock)
        {
            return _channels.Count > 0;
        }
    }

    private async Task RunConnectionAsync(CancellationToken stoppingToken)
    {
        using var socket = new ClientWebSocket();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = linked.Token;

        await socket.ConnectAsync(new Uri(TwitchIrcUrl), ct).ConfigureAwait(false);

        // The tags capability carries display-name, colour, and emote ranges.
        await SendAsync(socket, "CAP REQ :twitch.tv/tags twitch.tv/commands", ct).ConfigureAwait(false);
        await SendAsync(socket, "NICK justinfan" + Random.Shared.Next(10000, 99999).ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);

        lock (_lock)
        {
            _socketConnected = true;
            _sentJoins.Clear();
            foreach (var state in _channels.Values)
            {
                state.JoinSent = false;
                state.Joined = false;
            }
        }

        var reader = ReadLoopAsync(socket, ct);
        var writer = MaintainAsync(socket, ct);

        var finished = await Task.WhenAny(reader, writer).ConfigureAwait(false);
        linked.Cancel();
        try
        {
            await Task.WhenAll(reader, writer).ConfigureAwait(false);
        }
        catch
        {
            // The losing task's cancellation is expected; the winner's fault is rethrown below.
        }
        await finished.ConfigureAwait(false);
    }

    /// <summary>Issues JOIN/PART as the watched set changes and flushes batched frames.</summary>
    private async Task MaintainAsync(ClientWebSocket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (toJoin, toPart) = ReconcileChannels();

            foreach (var channel in toJoin)
            {
                await SendAsync(socket, "JOIN #" + channel, ct).ConfigureAwait(false);
            }
            foreach (var channel in toPart)
            {
                await SendAsync(socket, "PART #" + channel, ct).ConfigureAwait(false);
            }

            ExpireExistsProbes();
            FlushPending();

            if (!HasWatchers())
            {
                // Nothing left to watch - drop the socket rather than hold it idle.
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "idle", CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort close; the read loop ends either way.
                }
                return;
            }

            try
            {
                await _wake.WaitAsync(FlushInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Channels needing a JOIN written, and parted channels still tracked on the socket.</summary>
    private (List<string> Join, List<string> Part) ReconcileChannels()
    {
        var join = new List<string>();
        var part = new List<string>();
        lock (_lock)
        {
            foreach (var (channel, state) in _channels)
            {
                if (!state.JoinSent)
                {
                    state.JoinSent = true;
                    state.JoinSentAtUtc = DateTime.UtcNow;
                    join.Add(channel);
                }
            }
            foreach (var channel in _sentJoins.Where(c => !_channels.ContainsKey(c)).ToList())
            {
                _sentJoins.Remove(channel);
                part.Add(channel);
            }
            foreach (var channel in join)
            {
                _sentJoins.Add(channel);
            }
        }
        return (join, part);
    }

    private void FlushPending()
    {
        List<(string Channel, TwitchChatFrame Frame)>? frames = null;
        lock (_lock)
        {
            foreach (var (channel, state) in _channels)
            {
                if (state.Pending.Count == 0)
                {
                    continue;
                }
                frames ??= new List<(string, TwitchChatFrame)>();
                frames.Add((channel, new TwitchChatFrame
                {
                    Channel = channel,
                    Connected = _socketConnected && state.Joined,
                    Exists = state.Exists,
                    Messages = new List<TwitchChatMessage>(state.Pending),
                }));
                state.Pending.Clear();
            }
        }

        if (frames is null)
        {
            return;
        }
        foreach (var (channel, frame) in frames)
        {
            Broadcast(channel, frame);
        }
    }

    private void Broadcast(string channel, TwitchChatFrame frame)
    {
        var topic = TopicPrefix + channel;
        if (!_hub.TopicHasSubscribers(topic))
        {
            return;
        }
        var env = WsEnvelope.Build(topic, frame, AppJsonContext.Default.TwitchChatFrame);
        _ = _hub.BroadcastTopicAsync(topic, env);
    }

    /// <summary>Pushes a messageless frame so the panel can show the connection state change.</summary>
    private void BroadcastState(string channel)
    {
        TwitchChatFrame frame;
        lock (_lock)
        {
            if (!_channels.TryGetValue(channel, out var state))
            {
                return;
            }
            frame = new TwitchChatFrame
            {
                Channel = channel,
                Connected = _socketConnected && state.Joined,
                Exists = state.Exists,
            };
        }
        Broadcast(channel, frame);
    }

    /// <summary>Empties the replay buffer for every viewer; a widget-only clear would return with the next subscribe snapshot.</summary>
    public void Clear(string channel)
    {
        TwitchChatFrame frame;
        lock (_lock)
        {
            if (!_channels.TryGetValue(channel, out var state))
            {
                return;
            }
            state.Buffer.Clear();
            state.Pending.Clear();
            frame = new TwitchChatFrame
            {
                Channel = channel,
                Connected = _socketConnected && state.Joined,
                Exists = state.Exists,
                ClearedThrough = _seq,
            };
        }
        Broadcast(channel, frame);
    }

    private void MarkDisconnected()
    {
        List<string> channels;
        lock (_lock)
        {
            _socketConnected = false;
            _sentJoins.Clear();
            foreach (var state in _channels.Values)
            {
                state.Joined = false;
                state.JoinSent = false;
            }
            channels = _channels.Keys.ToList();
        }
        foreach (var channel in channels)
        {
            BroadcastState(channel);
        }
    }

    private async Task ReadLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        var pending = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                // One WS frame can carry several IRC lines and one line can
                // straddle two frames, so lines are cut on the delimiter.
                pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                await DrainLinesAsync(socket, pending, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task DrainLinesAsync(ClientWebSocket socket, StringBuilder pending, CancellationToken ct)
    {
        while (true)
        {
            // Scanned in place - materializing the whole buffer per line would
            // be quadratic on a channel that delivers many lines per frame.
            int idx = -1;
            for (int i = 0; i + 1 < pending.Length; i++)
            {
                if (pending[i] == '\r' && pending[i + 1] == '\n')
                {
                    idx = i;
                    break;
                }
            }
            if (idx < 0)
            {
                // Guard against a peer that never sends a delimiter.
                if (pending.Length > 64 * 1024)
                {
                    pending.Clear();
                }
                return;
            }

            var line = pending.ToString(0, idx);
            pending.Remove(0, idx + 2);
            await HandleLineAsync(socket, line, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleLineAsync(ClientWebSocket socket, string line, CancellationToken ct)
    {
        var parsed = TwitchIrcParser.Parse(line);
        if (parsed is null)
        {
            return;
        }

        switch (parsed.Command)
        {
            case "PING":
                await SendAsync(socket, "PONG :tmi.twitch.tv", ct).ConfigureAwait(false);
                return;

            case "JOIN":
                // Twitch echoes our own JOIN back as the confirmation.
                MarkJoined(parsed.Channel);
                return;

            case "ROOMSTATE":
                // Only a real channel answers a JOIN with ROOMSTATE, and it
                // does so whether or not the stream is live.
                MarkExists(parsed.Channel, true);
                return;

            case "RECONNECT":
                throw new IOException("twitch asked us to reconnect");

            case "PRIVMSG":
                Append(parsed);
                return;

            default:
                return;
        }
    }

    /// <summary>Resolves the existence probe, broadcasting only on a change.</summary>
    private void MarkExists(string channel, bool exists)
    {
        if (channel.Length == 0)
        {
            return;
        }
        bool changed = false;
        lock (_lock)
        {
            if (_channels.TryGetValue(channel, out var state) && state.Exists != exists)
            {
                state.Exists = exists;
                changed = true;
            }
        }
        if (changed)
        {
            BroadcastState(channel);
        }
    }

    /// <summary>A channel that never answered its JOIN inside the window does not exist.</summary>
    private void ExpireExistsProbes()
    {
        List<string>? expired = null;
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            foreach (var (channel, state) in _channels)
            {
                if (state.Exists.HasValue || !state.JoinSent || now - state.JoinSentAtUtc < ExistsProbeWindow)
                {
                    continue;
                }
                state.Exists = false;
                (expired ??= new List<string>()).Add(channel);
            }
        }
        if (expired is null)
        {
            return;
        }
        foreach (var channel in expired)
        {
            BroadcastState(channel);
        }
    }

    private void MarkJoined(string channel)
    {
        if (channel.Length == 0)
        {
            return;
        }
        bool changed = false;
        lock (_lock)
        {
            if (_channels.TryGetValue(channel, out var state) && !state.Joined)
            {
                state.Joined = true;
                changed = true;
            }
        }
        if (changed)
        {
            BroadcastState(channel);
        }
    }

    internal void Append(TwitchIrcLine line)
    {
        if (line.Channel.Length == 0)
        {
            return;
        }

        lock (_lock)
        {
            if (!_channels.TryGetValue(line.Channel, out var state))
            {
                return;
            }
            var message = TwitchChatMessageFactory.FromPrivmsg(line, ++_seq);
            if (message is null)
            {
                return;
            }

            state.Buffer.Add(message);
            if (state.Buffer.Count > BufferCap)
            {
                state.Buffer.RemoveRange(0, state.Buffer.Count - BufferCap);
            }
            state.Pending.Add(message);
            if (state.Pending.Count > BufferCap)
            {
                state.Pending.RemoveRange(0, state.Pending.Count - BufferCap);
            }
        }
    }

    private static Task SendAsync(ClientWebSocket socket, string line, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(line + "\r\n"), WebSocketMessageType.Text, true, ct);

    public override void Dispose()
    {
        _hub.OnTopicFirstSubscriber -= OnFirstSubscriber;
        _hub.OnTopicLastUnsubscriber -= OnLastUnsubscriber;
        _wake.Dispose();
        base.Dispose();
    }
}
