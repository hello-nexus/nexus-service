using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Sockets;

/// <summary>
/// Single multiplexed WebSocket hub. Clients connect to one endpoint and send
/// subscribe/unsubscribe commands to choose which topics they receive.
///
/// Protocol:
///   Client → Server:  {"sub":["processes","network"]}
///   Client → Server:  {"unsub":["processes"]}
///   Server → Client:  {"t":"processes","d":{…}}
///
/// Broadcasters call <see cref="BroadcastTopicAsync"/> with pre-built envelope
/// bytes; the hub fans out only to clients subscribed to that topic.
/// </summary>
public sealed class MultiplexHub
{
    private readonly ConcurrentDictionary<Guid, SubscribedClient> _clients = new();
    private readonly ConcurrentDictionary<string, Func<ReadOnlyMemory<byte>?>> _snapshotProviders =
        new(StringComparer.OrdinalIgnoreCase);

    public delegate void TopicEvent(string topic);
    public event TopicEvent? OnTopicFirstSubscriber;
    public event TopicEvent? OnTopicLastUnsubscriber;

    /// <summary>
    /// Wire by which a phone-session client reached the hub: a direct LAN
    /// WebSocket (<see cref="Lan"/>), a frame bridged through the cloud relay
    /// (<see cref="Relay"/>), or a WebRTC DataChannel direct P2P connection
    /// (<see cref="Direct"/>). Carried per-client so the sessions list can
    /// report how each currently-connected paired phone is talking to the PC.
    /// </summary>
    public enum ClientTransport
    {
        Lan,
        Relay,
        Direct,
    }

    /// <summary>
    /// Register a snapshot provider for a topic whose broadcast cadence is slow
    /// or event-driven. When a client subscribes, the hub immediately sends the
    /// provider's current envelope (if any) to that one client, so late
    /// subscribers do not wait for the next periodic or change-driven tick.
    /// The provider should return a pre-built envelope (use <see cref="WsEnvelope.Build"/>)
    /// or <c>null</c> when no state has been produced yet.
    ///
    /// Ordering: the snapshot send is fire-and-forget, so a fresh broadcast
    /// fired between subscribe and send-completion can arrive at the client
    /// before the snapshot. Only register snapshot providers for topics whose
    /// payloads are idempotent / order-tolerant (latest-wins). Do not use this
    /// pattern for topics carrying sequence numbers or deltas.
    /// </summary>
    public void RegisterSnapshotProvider(string topic, Func<ReadOnlyMemory<byte>?> provider)
    {
        _snapshotProviders[topic] = provider;
    }

    public void UnregisterSnapshotProvider(string topic)
    {
        _snapshotProviders.TryRemove(topic, out _);
    }

    internal bool TryGetTopicSnapshot(string topic, out ReadOnlyMemory<byte> envelope)
    {
        if (_snapshotProviders.TryGetValue(topic, out var provider))
        {
            ReadOnlyMemory<byte>? built;
            try
            { built = provider(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[multiplex-hub] snapshot provider for '{topic}' threw: {ex.Message}");
                built = null;
            }
            if (built is { } e)
            {
                envelope = e;
                return true;
            }
        }
        envelope = default;
        return false;
    }

    public bool TopicHasSubscribers(string topic)
    {
        foreach (var (_, client) in _clients)
        {
            if (client.IsSubscribed(topic))
                return true;
        }
        lock (_testSubsLock)
        {
            return _testSubs.TryGetValue(topic, out var n) && n > 0;
        }
    }

    public int TopicSubscriberCount(string topic)
    {
        int count = 0;
        foreach (var (_, client) in _clients)
        {
            if (client.IsSubscribed(topic))
                count++;
        }
        lock (_testSubsLock)
        {
            if (_testSubs.TryGetValue(topic, out var n))
            {
                count += n;
            }
        }
        return count;
    }

    public int ClientCount => _clients.Count;

    public Task HandleClientAsync(WebSocket socket, CancellationToken cancellationToken = default)
        => HandleClientAsync(socket, phoneSessionId: null, ClientTransport.Lan, cancellationToken);

    /// <summary>
    /// Topics only a desktop (loopback, service-token) socket may receive. The
    /// pending pair request frame carries the request id + SAS the host uses to
    /// approve a NEW device; a phone session that could read it would be able to
    /// race the real phone's confirm and take that session, or cancel it.
    /// </summary>
    private static readonly HashSet<string> DesktopOnlyTopics = new(StringComparer.Ordinal)
    {
        PanelTopics.PairCodeRequest,
    };

    public Task HandleClientAsync(WebSocket socket, string? phoneSessionId, CancellationToken cancellationToken = default)
        => HandleClientAsync(socket, phoneSessionId, ClientTransport.Lan, cancellationToken);

    /// <summary>
    /// Accept a multiplexed WebSocket. When <paramref name="phoneSessionId"/>
    /// is non-null, this client is treated as a Pair Remote session and can
    /// be force-closed by <see cref="KickPhoneSessionsAsync"/> /
    /// <see cref="KickAllPhoneAsync"/>. Local desktop / panel-kiosk callers
    /// pass null and stay unkickable. <paramref name="transport"/> records the
    /// wire the client reached us on (LAN /ws vs the relay bridge) so the
    /// sessions list can report it; defaults to LAN.
    /// </summary>
    public async Task HandleClientAsync(WebSocket socket, string? phoneSessionId, ClientTransport transport, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var client = new SubscribedClient(socket, phoneSessionId, transport);
        _clients[id] = client;

        try
        {
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                }
                catch (WebSocketException) { break; }

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    if (msg == "ping")
                        continue;
                    ProcessCommand(id, client, msg);
                }
            }
        }
        finally
        {
            _clients.TryRemove(id, out _);
            var topics = client.GetSubscriptions();
            client.Dispose();

            try
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }

            foreach (var topic in topics)
            {
                if (!TopicHasSubscribers(topic))
                {
                    try
                    { OnTopicLastUnsubscriber?.Invoke(topic); }
                    catch { }
                }
            }
        }
    }

    public async Task BroadcastTopicAsync(string topic, ReadOnlyMemory<byte> envelope)
    {
        foreach (var (_, client) in _clients)
        {
            if (client.IsSubscribed(topic))
            {
                await client.SendAsync(envelope, WebSocketMessageType.Text);
            }
        }
        // Fire the test-only observer if one is attached. Integration tests use
        // this to verify broadcasters actually hit the wire without needing a
        // real WebSocket client. Production code doesn't attach a handler so
        // this event is a no-op at runtime.
        try
        { OnBroadcastForTest?.Invoke(topic, envelope); }
        catch { /* swallow */ }
    }

    /// <summary>
    /// Force-close every connected phone-session WebSocket whose session id
    /// matches one in <paramref name="sessionIds"/>. The socket transitions
    /// to <see cref="WebSocketState.CloseSent"/> which unblocks the receive
    /// loop in <see cref="HandleClientAsync"/>, and the finalizer there
    /// removes the entry from <see cref="_clients"/>. Local desktop / panel
    /// clients (phoneSessionId == null) are never touched. Kicks fan out
    /// in parallel so a single slow socket can't delay the rest.
    /// </summary>
    public Task KickPhoneSessionsAsync(IReadOnlyCollection<string> sessionIds)
    {
        if (sessionIds.Count == 0)
            return Task.CompletedTask;

        var ids = new HashSet<string>(sessionIds, StringComparer.Ordinal);
        var tasks = new List<Task>();
        foreach (var (_, client) in _clients)
        {
            if (client.PhoneSessionId is { } sid && ids.Contains(sid))
                tasks.Add(CloseClientSafeAsync(client));
        }
        return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    /// <summary>
    /// Force-close every connected phone-session WebSocket. Used when the
    /// remote-control killswitch is toggled off and on "Remove all sessions".
    /// Parallel for the same reason as <see cref="KickPhoneSessionsAsync"/>.
    /// </summary>
    public Task KickAllPhoneAsync()
    {
        var tasks = new List<Task>();
        foreach (var (_, client) in _clients)
        {
            if (client.PhoneSessionId is not null)
                tasks.Add(CloseClientSafeAsync(client));
        }
        return tasks.Count == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    /// <summary>
    /// Close one kicked client, isolating any failure so a single client's close
    /// error can never fault the whole revoke (which would surface as an HTTP 500
    /// on the revoke endpoint). <see cref="SubscribedClient.CloseRevokedAsync"/>
    /// already swallows internally; this is a second belt so the killswitch
    /// semantics - "kick still closes every client, just can't throw" - hold even
    /// if a future close path regresses.
    /// </summary>
    private static async Task CloseClientSafeAsync(SubscribedClient client)
    {
        try { await client.CloseRevokedAsync().ConfigureAwait(false); }
        catch { /* one client's close must not fail the revoke */ }
    }

    /// <summary>
    /// Report how the given phone session is currently connected to the hub:
    /// <c>"direct"</c> if bridged over a WebRTC DataChannel P2P connection,
    /// <c>"relay"</c> if bridged through the cloud relay, <c>"lan"</c> if
    /// connected only via a direct LAN WebSocket, or <c>null</c> if no client
    /// for that session is connected. Priority direct &gt; relay &gt; lan when a
    /// session somehow has more than one (a transport being torn down while
    /// another is still up), surfacing the transport the user most wants shown.
    /// </summary>
    public string? GetConnectedTransport(string phoneSessionId)
    {
        if (string.IsNullOrEmpty(phoneSessionId))
            return null;

        var sawRelay = false;
        var sawLan = false;
        foreach (var (_, client) in _clients)
        {
            if (!string.Equals(client.PhoneSessionId, phoneSessionId, StringComparison.Ordinal))
                continue;
            if (client.Transport == ClientTransport.Direct)
                return "direct";
            if (client.Transport == ClientTransport.Relay)
                sawRelay = true;
            else
                sawLan = true;
        }
        if (sawRelay)
            return "relay";
        return sawLan ? "lan" : null;
    }

    /// <summary>
    /// Test-only hook. Fires after every <see cref="BroadcastTopicAsync"/> call,
    /// including topics with zero subscribers. Integration tests subscribe to this
    /// instead of wiring a real WebSocket client.
    /// </summary>
    internal event Action<string, ReadOnlyMemory<byte>>? OnBroadcastForTest;

    /// <summary>
    /// Test-only: register a phantom subscriber for <paramref name="topic"/> so
    /// <see cref="TopicHasSubscribers"/> returns true and the broadcaster runs
    /// its full gather path. Returns a disposable that removes the subscription.
    /// Fires <see cref="OnTopicFirstSubscriber"/> after releasing
    /// <see cref="_testSubsLock"/>, not while holding it - a handler that takes
    /// its own lock (a worker's Tick loop, which then calls back into
    /// <see cref="TopicHasSubscribers"/>) would otherwise invert lock order
    /// against that same tick.
    /// </summary>
    internal IDisposable AddTestSubscription(string topic)
    {
        bool wasFirst;
        lock (_testSubsLock)
        {
            _testSubs.TryGetValue(topic, out var count);
            _testSubs[topic] = count + 1;
            wasFirst = count == 0;
        }
        if (wasFirst)
        {
            try
            { OnTopicFirstSubscriber?.Invoke(topic); }
            catch { }
        }
        return new TestSubscriptionHandle(this, topic);
    }

    private readonly Dictionary<string, int> _testSubs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _testSubsLock = new();

    private void ReleaseTestSubscription(string topic)
    {
        lock (_testSubsLock)
        {
            if (!_testSubs.TryGetValue(topic, out var count) || count <= 0)
            {
                return;
            }
            if (count == 1)
            {
                _testSubs.Remove(topic);
                try
                { OnTopicLastUnsubscriber?.Invoke(topic); }
                catch { }
            }
            else
            {
                _testSubs[topic] = count - 1;
            }
        }
    }

    private sealed class TestSubscriptionHandle : IDisposable
    {
        private readonly MultiplexHub _hub;
        private readonly string _topic;
        private bool _disposed;
        public TestSubscriptionHandle(MultiplexHub hub, string topic) { _hub = hub; _topic = topic; }
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _hub.ReleaseTestSubscription(_topic);
        }
    }

    private void ProcessCommand(Guid clientId, SubscribedClient client, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("sub", out var subArr) && subArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in subArr.EnumerateArray())
                {
                    var topic = item.GetString();
                    if (topic is null)
                        continue;
                    if (client.PhoneSessionId is not null && DesktopOnlyTopics.Contains(topic))
                        continue;

                    bool wasFirst = !TopicHasSubscribers(topic);
                    client.Subscribe(topic);
                    if (wasFirst)
                    {
                        try
                        { OnTopicFirstSubscriber?.Invoke(topic); }
                        catch { }
                    }

                    // Snapshot-on-subscribe: deliver the latest known envelope
                    // to this single client so late subscribers don't wait for
                    // the next slow / event-driven broadcast tick.
                    if (TryGetTopicSnapshot(topic, out var snapshot))
                    {
                        _ = client.SendAsync(snapshot, WebSocketMessageType.Text);
                    }
                }
            }

            if (root.TryGetProperty("unsub", out var unsubArr) && unsubArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in unsubArr.EnumerateArray())
                {
                    var topic = item.GetString();
                    if (topic is null)
                        continue;

                    client.Unsubscribe(topic);
                    if (!TopicHasSubscribers(topic))
                    {
                        try
                        { OnTopicLastUnsubscriber?.Invoke(topic); }
                        catch { }
                    }
                }
            }
        }
        catch
        {
            // Malformed command - ignore.
        }
    }

    private sealed class SubscribedClient : IDisposable
    {
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly HashSet<string> _topics = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _topicLock = new();

        /// <summary>
        /// When non-null, this client is a Pair Remote session and can be
        /// force-disconnected by <see cref="MultiplexHub.KickPhoneSessionsAsync"/>.
        /// Null means a trusted local client (desktop app or panel kiosk).
        /// </summary>
        public string? PhoneSessionId { get; }

        /// <summary>Wire this client reached the hub on (LAN /ws vs relay bridge).</summary>
        public ClientTransport Transport { get; }

        public SubscribedClient(WebSocket socket, string? phoneSessionId = null, ClientTransport transport = ClientTransport.Lan)
        {
            _socket = socket;
            PhoneSessionId = phoneSessionId;
            Transport = transport;
        }

        public async Task CloseRevokedAsync()
        {
            // Kicking a client must never throw out of the revoke path (a thrown
            // close would fault Task.WhenAll in the hub and 500 the revoke
            // endpoint). The await is inside the try because _writeLock can be
            // disposed underneath us: the receive loop in HandleClientAsync
            // disposes this client the instant its socket reports Close, and a
            // relay-bridged client closes that fast - RelayWebSocket.CloseAsync
            // aborts the relay transport, which immediately unblocks that loop.
            // So WaitAsync (or the finally Release) can race an ObjectDisposed.
            bool acquired = false;
            try
            {
                await _writeLock.WaitAsync().ConfigureAwait(false);
                acquired = true;
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "revoked", CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // Socket already closing / disposed, transport faulted, or the
                // write lock was disposed by a concurrent teardown. The session
                // is being removed regardless; swallow so the kick still
                // completes for every other client. Force a transport abort so
                // a relay-bridged client still gets torn down (and the phone
                // still sees the revoked close) even if the orderly close threw.
                try { _socket.Abort(); } catch { /* best effort */ }
            }
            finally
            {
                if (acquired)
                {
                    try { _writeLock.Release(); } catch { /* lock disposed under us */ }
                }
            }
        }

        public bool IsSubscribed(string topic)
        {
            lock (_topicLock)
                return _topics.Contains(topic);
        }

        public void Subscribe(string topic)
        {
            lock (_topicLock)
                _topics.Add(topic);
        }

        public void Unsubscribe(string topic)
        {
            lock (_topicLock)
                _topics.Remove(topic);
        }

        public string[] GetSubscriptions()
        {
            lock (_topicLock)
                return _topics.ToArray();
        }

        public async Task SendAsync(ReadOnlyMemory<byte> payload, WebSocketMessageType type)
        {
            if (_socket.State != WebSocketState.Open)
                return;

            await _writeLock.WaitAsync();
            try
            {
                await _socket.SendAsync(payload, type, endOfMessage: true, CancellationToken.None);
            }
            catch { }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose() => _writeLock.Dispose();
    }
}
