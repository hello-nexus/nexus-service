using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Relay;

/// <summary>
/// Holds the PC's outbound cloud-relay sockets. When the user has enabled BOTH
/// remote control AND the relay opt-in, the service opens ONE host
/// <see cref="ClientWebSocket"/> per paired phone session to the relay
/// (<see cref="RelayUrl"/>), registers as that session's <c>host</c> via the
/// rid derived from the session's relay root, and bridges relayed frames into
/// the shared <see cref="MultiplexHub"/> tagged with the session id - so the
/// Pair Remote killswitch (KickAll / KickPhoneSessions) closes a relayed
/// session exactly like a LAN one.
///
/// Entirely event-driven: it reconciles its set of host sockets to the desired
/// state on start and on every <see cref="IConfigStore.OnChanged"/> (relay
/// on/off, remote-control on/off, session add/remove all flow through that one
/// signal). There is no poll loop. A dropped relay socket reconnects with
/// capped exponential backoff; waits are on cancellation tokens / channel
/// reads, never a sleep-to-win-a-race.
/// </summary>
public sealed class RelayConnectionService : BackgroundService
{
    /// <summary>
    /// Legacy default relay gateway. Used until <see cref="SelectRelayAsync"/>
    /// upgrades <see cref="Endpoint"/> to the latency-nearest regional relay, and
    /// the fallback if that directory lookup fails. The rid is the only routing key.
    /// </summary>
    public const string RelayUrl = "wss://api.hellonexus.com/relay";

    /// <summary>Control-plane directory: the region map + the caller's nearest tag.</summary>
    private const string DirectoryUrl = "https://api.hellonexus.com/relays";

    private const int ReceiveBufferSize = 8192;
    private const int InitialReconnectDelayMs = 1_000;
    // Relay close code for a host hello it would not accept. Terminal: another
    // attempt cannot produce a credential this build does not carry, and a
    // clean close otherwise resets the backoff to one second.
    private const WebSocketCloseStatus UnauthorizedClose = (WebSocketCloseStatus)4401;
    private const int MaxReconnectDelayMs = 30_000;
    // Relay caps inbound frames at 256 KB; refuse to buffer anything larger so a
    // hostile relay can't push us into unbounded allocation.
    private const int MaxFrameBytes = 256 * 1024;

    private const string EventKey = "e";
    private const string SaltKey = "salt";
    private const string PeerUp = "peer-up";
    private const string PeerDown = "peer-down";

    // Link-key prefixes keep the desired-link namespaces disjoint inside the
    // single _links map: a runtime session link (keyed by phone session id), a
    // REST-over-relay HTTP-tunnel link (also keyed by session id), and a pre-pair
    // rendezvous link (keyed by rid_pair) can never collide.
    private const string SessionLinkPrefix = "sess:";
    private const string HttpLinkPrefix = "http:";
    private const string PairLinkPrefix = "pair:";

    // Concurrency cap on in-flight tunneled requests per HTTP link, so a hostile
    // peer can't fan out unbounded dispatch tasks on one channel.
    private const int MaxConcurrentHttpRequests = 32;

    private readonly ILogger<RelayConnectionService> _log;
    private readonly PanelPhonePairingService _pairing;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;
    private readonly RelayHttpDispatcher _httpDispatcher;
    // Null in tests / a minimal host: relay selection is skipped and Endpoint
    // keeps whatever it was set to (the legacy default, or a test's fake relay).
    private readonly IHttpClientFactory? _httpFactory;

    private readonly object _gate = new();
    private readonly Dictionary<string, HostLink> _links = new(StringComparer.Ordinal);
    private CancellationToken _serviceCt = CancellationToken.None;
    private bool _started;

    /// <summary>
    /// Relay endpoint every host link connects to. Starts at the legacy
    /// <see cref="RelayUrl"/>; <see cref="SelectRelayAsync"/> upgrades it to the
    /// latency-nearest regional relay once, before the first <see cref="Reconcile"/>
    /// brings links up. Tests point it at a local fake relay (and inject no
    /// <see cref="IHttpClientFactory"/>, so selection is skipped and the fake stands).
    /// </summary>
    public Uri Endpoint { get; set; } = new Uri(RelayUrl);

    /// <summary>
    /// Opens the underlying transport to the relay. Default creates a real
    /// <see cref="ClientWebSocket"/>; tests swap in an in-process transport.
    /// </summary>
    public Func<Uri, CancellationToken, Task<WebSocket>> TransportFactory { get; set; } = DefaultTransportFactory;

    public RelayConnectionService(
        ILogger<RelayConnectionService> log,
        PanelPhonePairingService pairing,
        IConfigStore store,
        MultiplexHub hub,
        RelayHttpDispatcher httpDispatcher,
        IHttpClientFactory? httpFactory = null)
    {
        _log = log;
        _pairing = pairing;
        _store = store;
        _hub = hub;
        _httpDispatcher = httpDispatcher;
        _httpFactory = httpFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _serviceCt = stoppingToken;
        _started = true;

        // Tear everything down when the host stops.
        stoppingToken.Register(() =>
        {
            _store.OnChanged -= OnSettingsChanged;
            _pairing.OutstandingPairTokensChanged -= OnSettingsChanged;
            CloseAllLinks();
        });

        // Pick the latency-nearest regional relay (and publish its tag for the QR)
        // BEFORE subscribing to change signals and bringing links up, so the first
        // reconcile already targets the resolved Endpoint - no Reconcile can race
        // on the pre-selection (legacy) Endpoint and strand links there. Best-
        // effort: any failure leaves the legacy default. Runs after StartAsync
        // (BackgroundService), so this directory call never delays boot.
        // (A pair QR minted inside the brief selection window carries no r= and
        // would target the default relay; the token expires + regenerates, so it
        // self-heals - not worth per-token dual-homing for a sub-second window
        // that only opens right after a service restart.)
        await SelectRelayAsync(stoppingToken).ConfigureAwait(false);

        _store.OnChanged += OnSettingsChanged;
        // The pair-token set changes outside the config store (mint on QR create,
        // consume on a successful claim, reap on expiry); listen on its own push
        // so pair rendezvous links reconcile without a poll loop.
        _pairing.OutstandingPairTokensChanged += OnSettingsChanged;

        Reconcile();
    }

    /// <summary>
    /// Resolve the latency-nearest regional relay from the <see cref="DirectoryUrl"/>
    /// control plane (which reads the caller's edge geo) and point
    /// <see cref="Endpoint"/> at it, publishing the region tag for the pairing QR
    /// so the phone follows to the same relay. Best-effort and one-shot: skipped
    /// when no <see cref="IHttpClientFactory"/> is available (tests), and any
    /// failure leaves the legacy default in place.
    /// </summary>
    private async Task SelectRelayAsync(CancellationToken ct)
    {
        if (_httpFactory is null)
            return;
        try
        {
            using var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            Common.ClientCredential.Apply(client);
            var json = await client.GetStringAsync(DirectoryUrl, ct).ConfigureAwait(false);
            var dir = JsonSerializer.Deserialize(json, AppJsonContext.Default.RelayDirectoryResponse);
            if (dir is not null
                && !string.IsNullOrEmpty(dir.Nearest)
                && dir.Regions.TryGetValue(dir.Nearest, out var url)
                && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                // Refuse anything but wss:// - a compromised directory must not be
                // able to downgrade the relay transport to plaintext ws:// (the
                // payloads stay E2E-sealed, but rid/traffic metadata would leak).
                && string.Equals(uri.Scheme, Uri.UriSchemeWss, StringComparison.Ordinal))
            {
                Endpoint = uri;
                _pairing.RelayRegionTag = dir.Nearest;
                _log.LogInformation("relay: nearest region {Tag} -> {Url}", dir.Nearest, url);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // host stopping; nothing to do.
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "relay directory fetch failed; using legacy default {Url}", Endpoint);
        }
    }

    private static Task<WebSocket> DefaultTransportFactory(Uri uri, CancellationToken ct)
        => ConnectClientWebSocketAsync(uri, ct);

    private static async Task<WebSocket> ConnectClientWebSocketAsync(Uri uri, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        try
        {
            await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private void OnSettingsChanged()
    {
        // IConfigStore.OnChanged fires on the persistence thread; hop off so we
        // never block a writer while opening / closing sockets.
        _ = Task.Run(Reconcile);
    }

    /// <summary>
    /// Bring the live set of host links into agreement with the desired set,
    /// when (remote-control AND relay) are on:
    ///   • one RUNTIME link per active relay session (keyed by session id), and
    ///   • one PAIR link per outstanding QR pair token (keyed by rid_pair).
    /// None when the feature is off (so turning either switch off tears every
    /// link down). Adds links newly desired, stops links that vanished. When a
    /// pair token is consumed by a claim, its pair link disappears here and the
    /// brand-new session's runtime link appears on the same pass.
    /// </summary>
    private void Reconcile()
    {
        if (!_started)
            return;

        var enabled = _pairing.GetRemoteControlEnabled() && _pairing.GetRelayEnabled();

        var desired = new Dictionary<string, DesiredLink>(StringComparer.Ordinal);
        if (enabled)
        {
            foreach (var s in _pairing.GetActiveRelaySessions())
            {
                // RUNTIME link (rid) bridges /ws telemetry; HTTP link (rid_http)
                // tunnels the panel's REST calls. Both die with the session on
                // kick / relay-off / remote-off because both fall out of `desired`.
                desired[SessionLinkPrefix + s.Id] = DesiredLink.Session(s.Id, s.RelayRoot);
                desired[HttpLinkPrefix + s.Id] = DesiredLink.Http(s.Id, s.RelayRoot);
            }

            foreach (var p in _pairing.GetOutstandingPairTokens())
            {
                var ridPair = RelayCrypto.DeriveRid(p.PairRoot);
                desired[PairLinkPrefix + ridPair] = DesiredLink.Pair(p.Token, p.PairRoot);
            }
        }

        List<HostLink> toStop = new();
        List<HostLink> toStart = new();
        lock (_gate)
        {
            if (_serviceCt.IsCancellationRequested)
                return;

            // Stop links no longer desired.
            foreach (var (key, link) in _links)
            {
                if (!desired.ContainsKey(key))
                    toStop.Add(link);
            }
            foreach (var link in toStop)
                _links.Remove(link.LinkKey);

            // Start links newly desired.
            foreach (var (key, d) in desired)
            {
                if (_links.ContainsKey(key))
                    continue;
                var link = new HostLink(this, key, d);
                _links[key] = link;
                toStart.Add(link);
            }
        }

        foreach (var link in toStop)
            link.Stop();
        foreach (var link in toStart)
            link.Start(_serviceCt);
    }

    /// <summary>Which leg a <see cref="DesiredLink"/> satisfies.</summary>
    private enum LinkKind
    {
        /// <summary>rid: bridges a paired session's /ws telemetry into the hub.</summary>
        Runtime,
        /// <summary>rid_http: tunnels the paired session's REST calls into the endpoint pipeline.</summary>
        Http,
        /// <summary>rid_pair: one-shot pre-pair claim rendezvous.</summary>
        Pair,
    }

    /// <summary>
    /// One desired host leg. A RUNTIME link bridges a paired session into the hub
    /// for the life of the relay socket; an HTTP link tunnels that session's REST
    /// calls through the endpoint pipeline; a PAIR link is a one-shot pre-pair
    /// rendezvous that completes a single claim handshake and ends.
    /// </summary>
    private readonly record struct DesiredLink(LinkKind Kind, string Tag, string? PairToken, byte[] Root)
    {
        public bool IsPair => Kind == LinkKind.Pair;

        /// <summary>Runtime link: <paramref name="sessionId"/> tags the hub session; root is the session relayRoot.</summary>
        public static DesiredLink Session(string sessionId, byte[] relayRoot)
            => new(LinkKind.Runtime, Tag: sessionId, PairToken: null, Root: relayRoot);

        /// <summary>HTTP-tunnel link: same session id + relayRoot, but registers on rid_http.</summary>
        public static DesiredLink Http(string sessionId, byte[] relayRoot)
            => new(LinkKind.Http, Tag: sessionId, PairToken: null, Root: relayRoot);

        /// <summary>Pair link: root is the pairRoot; <paramref name="pairToken"/> is what ClaimCore consumes.</summary>
        public static DesiredLink Pair(string pairToken, byte[] pairRoot)
            => new(LinkKind.Pair, Tag: "", PairToken: pairToken, Root: pairRoot);
    }

    private void CloseAllLinks()
    {
        List<HostLink> links;
        lock (_gate)
        {
            links = new List<HostLink>(_links.Values);
            _links.Clear();
        }
        foreach (var link in links)
            link.Stop();
    }

    // ───────────────────────── Inbound sealed LAN tunnel ─────────────────────
    // Accept-side of the SAME sealed transport, but over a DIRECT LAN socket with
    // NO cloud relay in the middle. The phone connects to /secure-tunnel and sends
    // a CLIENT hello {role:"client", rid, salt}; this service IS the host, so it
    // matches the rid to a paired session, sends the client a peer-up, and runs the
    // same runtime (multiplex) or HTTP leg the cloud path runs - the frames just
    // arrive on this accepted socket directly. The session token never crosses the
    // wire: the 128-bit HKDF rid proves which session, the per-connection AEAD key
    // proves possession. Same trust model as the relay, minus the broker.

    private const string NoHost = "no-host";
    private static readonly byte[] PeerUpFrame = Encoding.UTF8.GetBytes("{\"e\":\"" + PeerUp + "\"}");
    private static readonly byte[] NoHostFrame = Encoding.UTF8.GetBytes("{\"e\":\"" + NoHost + "\"}");

    /// <summary>
    /// Drive one inbound sealed-tunnel connection accepted on /secure-tunnel: read
    /// the client hello, identify the session + leg by rid, promote the client
    /// (peer-up), and run the runtime or HTTP leg until the socket closes. A
    /// no-match / killswitch-off reply is {"e":"no-host"} so the client backs off.
    /// </summary>
    public async Task HandleInboundSealedTunnelAsync(WebSocket socket, CancellationToken ct)
    {
        var hello = await ReadClientHelloAsync(socket, ct).ConfigureAwait(false);
        if (hello is null)
            return;

        // The Pair Remote killswitch gates phone sessions the same on every transport.
        if (!_pairing.GetRemoteControlEnabled())
        {
            await SendControlAsync(socket, NoHostFrame, ct).ConfigureAwait(false);
            return;
        }

        var match = MatchSessionByRid(hello.Value.Rid);
        if (match is null)
        {
            await SendControlAsync(socket, NoHostFrame, ct).ConfigureAwait(false);
            return;
        }
        var (sessionId, relayRoot, isHttpLeg) = match.Value;

        byte[] connSalt;
        try { connSalt = RelayCrypto.FromBase64UrlNoPad(hello.Value.Salt); }
        catch { return; }
        if (connSalt.Length != RelayCrypto.ConnSaltLength)
            return;

        var aeadKey = RelayCrypto.DeriveAeadKey(relayRoot, connSalt);
        Func<byte[], byte[]> rekeyDerive = hn => RelayCrypto.DeriveRekeyedAeadKey(relayRoot, connSalt, hn);

        // Promote the client to OPEN - mirrors the relay's peer-up. The web client
        // sends no sealed frame until it sees this.
        await SendControlAsync(socket, PeerUpFrame, ct).ConfigureAwait(false);

        if (isHttpLeg)
            await RunInboundHttpAsync(socket, aeadKey, sessionId, rekeyDerive, ct).ConfigureAwait(false);
        else
            await RunInboundRuntimeAsync(socket, aeadKey, sessionId, rekeyDerive, ct).ConfigureAwait(false);
    }

    /// <summary>Runtime leg: wrap the socket in a <see cref="RelayWebSocket"/> and
    /// drive the hub on it, feeding inbound frames as they arrive for decrypt.</summary>
    private async Task RunInboundRuntimeAsync(WebSocket socket, byte[] aeadKey, string sessionId, Func<byte[], byte[]> rekeyDerive, CancellationToken ct)
    {
        var relayWs = new RelayWebSocket(
            socket, aeadKey, RelayCrypto.DirHostToClient, RelayCrypto.DirClientToHost, rekeyDerive);
        var hubTask = _hub.HandleClientAsync(relayWs, sessionId, MultiplexHub.ClientTransport.Lan, ct);
        try
        {
            await ReadBinaryFramesAsync(socket, relayWs.EnqueueInbound, ct).ConfigureAwait(false);
        }
        finally
        {
            relayWs.CompleteInbound();
            try { await hubTask.ConfigureAwait(false); } catch { /* hub loop teardown */ }
        }
    }

    /// <summary>HTTP leg: reuse <see cref="HttpChannel"/> to dispatch sealed REST
    /// requests through the endpoint pipeline as this session's phone session.</summary>
    private async Task RunInboundHttpAsync(WebSocket socket, byte[] aeadKey, string sessionId, Func<byte[], byte[]> rekeyDerive, CancellationToken ct)
    {
        var channel = new HttpChannel(this, socket, aeadKey, sessionId, ct, rekeyDerive);
        try
        {
            await ReadBinaryFramesAsync(socket, channel.OnRequestFrame, ct).ConfigureAwait(false);
        }
        finally
        {
            channel.Dispose();
        }
    }

    /// <summary>Read the first TEXT message as the client hello and validate it.
    /// Returns null on a non-client / malformed / oversized hello.</summary>
    private static async Task<ClientHello?> ReadClientHelloAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];
        using var message = new System.IO.MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (message.Length + result.Count > 8192) // a hello is tiny
                return null;
            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        if (result.MessageType != WebSocketMessageType.Text)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(message.ToArray());
            var root = doc.RootElement;
            var role = root.TryGetProperty("role", out var r) ? r.GetString() : null;
            if (!string.Equals(role, "client", StringComparison.Ordinal))
                return null;
            var rid = root.TryGetProperty("rid", out var ri) ? ri.GetString() : null;
            var salt = root.TryGetProperty(SaltKey, out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(rid) || string.IsNullOrEmpty(salt))
                return null;
            return new ClientHello(rid, salt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Match a hello rid to a paired session + whether it's the HTTP leg
    /// (rid_http) vs the runtime leg (rid). Possession is still proven later by the
    /// AEAD decrypt; the rid only routes.</summary>
    private (string SessionId, byte[] RelayRoot, bool IsHttp)? MatchSessionByRid(string rid)
    {
        foreach (var s in _pairing.GetActiveRelaySessions())
        {
            if (string.Equals(RelayCrypto.DeriveRid(s.RelayRoot), rid, StringComparison.Ordinal))
                return (s.Id, s.RelayRoot, false);
            if (string.Equals(RelayCrypto.DeriveHttpRid(s.RelayRoot), rid, StringComparison.Ordinal))
                return (s.Id, s.RelayRoot, true);
        }
        return null;
    }

    /// <summary>Read BINARY frames off the accepted socket, invoking
    /// <paramref name="onBinary"/> per frame, until the socket closes. TEXT after
    /// the hello is unused on the accept side and ignored.</summary>
    private static async Task ReadBinaryFramesAsync(WebSocket socket, Action<byte[]> onBinary, CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];
        using var message = new System.IO.MemoryStream();
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                if (message.Length + result.Count > MaxFrameBytes)
                    throw new InvalidOperationException("sealed tunnel frame exceeds 256 KB cap");
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Binary)
                onBinary(message.ToArray());
        }
    }

    private static Task SendControlAsync(WebSocket socket, byte[] frame, CancellationToken ct)
        => socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, ct);

    private readonly record struct ClientHello(string Rid, string Salt);

    /// <summary>
    /// One host leg: owns the reconnect loop, the relay socket, and the current
    /// relayed hub session (if a client is presently peered up).
    /// </summary>
    private sealed class HostLink
    {
        private readonly RelayConnectionService _owner;
        private readonly DesiredLink _desired;
        private readonly byte[] _root;
        private readonly string _rid;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        // Pair links are one-shot: once a claim handshake completes, the token is
        // consumed and this link is obsolete (Reconcile removes it). Set so the
        // reconnect loop stops instead of re-registering a dead pair rendezvous.
        private volatile bool _pairClaimDone;
        private volatile bool _unauthorized;

        /// <summary>The reconcile key this link satisfies (sess:&lt;id&gt; or pair:&lt;rid&gt;).</summary>
        public string LinkKey { get; }

        /// <summary>Phone session id for a runtime link (empty for a pair link); the hub session tag.</summary>
        private string SessionTag => _desired.Tag;

        public HostLink(RelayConnectionService owner, string linkKey, DesiredLink desired)
        {
            _owner = owner;
            LinkKey = linkKey;
            _desired = desired;
            _root = desired.Root;
            // The HTTP-tunnel leg registers on rid_http (a distinct rendezvous off
            // the SAME relayRoot); runtime + pair legs register on rid.
            _rid = desired.Kind == LinkKind.Http
                ? RelayCrypto.DeriveHttpRid(desired.Root)
                : RelayCrypto.DeriveRid(desired.Root);
        }

        public void Start(CancellationToken serviceCt)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(serviceCt);
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { /* ignored */ }
            // The loop observes cancellation and disposes the socket; we don't
            // await here so a single stuck socket can't stall reconciliation.
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var delayMs = InitialReconnectDelayMs;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(ct).ConfigureAwait(false);
                    // Clean return (host socket closed by us / the relay): if we
                    // weren't cancelled, reconnect from the base backoff.
                    delayMs = InitialReconnectDelayMs;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _owner._log.LogDebug(ex, "relay host link {LinkKey} dropped; reconnecting", LinkKey);
                }

                if (ct.IsCancellationRequested)
                    break;

                // A completed pair claim consumed the token; don't reconnect a
                // dead pair rendezvous while Reconcile is removing this link.
                if (_pairClaimDone)
                    break;

                // Only terminal for a build that cannot produce a credential.
                // An official build seeing 4401 means something is wrong on the
                // relay side, and stranding the link would need a remote toggle
                // to clear - so it keeps retrying on the capped backoff.
                if (_unauthorized)
                {
                    if (!Common.ClientCredential.IsOfficial)
                    {
                        _owner._log.LogWarning(
                            "relay refused this build's credential on {LinkKey}; not retrying", LinkKey);
                        break;
                    }

                    _unauthorized = false;
                    _owner._log.LogWarning(
                        "relay refused our credential on {LinkKey}; retrying", LinkKey);
                }

                try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                delayMs = Math.Min(delayMs * 2, MaxReconnectDelayMs);
            }
        }

        private Task RunOnceAsync(CancellationToken ct)
            => _desired.Kind switch
            {
                LinkKind.Pair => RunPairOnceAsync(ct),
                LinkKind.Http => RunHttpOnceAsync(ct),
                _ => RunRuntimeOnceAsync(ct),
            };

        private async Task RunRuntimeOnceAsync(CancellationToken ct)
        {
            WebSocket transport = await _owner.TransportFactory(_owner.Endpoint, ct).ConfigureAwait(false);
            await using var _ = new WebSocketDisposer(transport);

            // 1) Host hello.
            await SendHelloAsync(transport, ct).ConfigureAwait(false);

            // 2) Receive loop: peer-up / peer-down (TEXT) + forwarded frames (BINARY).
            RelayWebSocket? session = null;
            Task? sessionTask = null;
            var buffer = new byte[ReceiveBufferSize];
            using var message = new System.IO.MemoryStream();

            try
            {
                while (!ct.IsCancellationRequested && transport.State == WebSocketState.Open)
                {
                    message.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await transport.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            if (transport.CloseStatus == UnauthorizedClose)
                                _unauthorized = true;
                            return; // relay closed the host socket; outer loop reconnects
                        }
                        if (message.Length + result.Count > MaxFrameBytes)
                            throw new InvalidOperationException("relay frame exceeds 256 KB cap");
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        (session, sessionTask) = HandleControl(message.ToArray(), session, sessionTask, transport, ct);
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        // Forwarded encrypted client frame: hand to the current
                        // relayed session for decrypt + delivery to the hub.
                        session?.EnqueueInbound(message.ToArray());
                    }
                }
            }
            finally
            {
                // End any in-flight relayed session so the hub's receive loop exits.
                session?.CompleteInbound();
                if (sessionTask is not null)
                {
                    try { await sessionTask.ConfigureAwait(false); } catch { /* hub loop teardown */ }
                }
            }
        }

        private (RelayWebSocket?, Task?) HandleControl(
            byte[] textBytes, RelayWebSocket? session, Task? sessionTask, WebSocket transport, CancellationToken ct)
        {
            string? evt;
            string? saltB64;
            try
            {
                using var doc = JsonDocument.Parse(textBytes);
                var root = doc.RootElement;
                evt = root.TryGetProperty(EventKey, out var e) ? e.GetString() : null;
                saltB64 = root.TryGetProperty(SaltKey, out var s) ? s.GetString() : null;
            }
            catch
            {
                // Malformed control frame: ignore (TEXT-after-hello is otherwise unused).
                return (session, sessionTask);
            }

            if (string.Equals(evt, PeerUp, StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(saltB64))
                    return (session, sessionTask);

                // A fresh client peered up. End any prior session first
                // (defensive: relay should have sent peer-down).
                session?.CompleteInbound();

                byte[] connSalt;
                try { connSalt = RelayCrypto.FromBase64UrlNoPad(saltB64); }
                catch { return (session, sessionTask); }
                if (connSalt.Length != RelayCrypto.ConnSaltLength)
                    return (session, sessionTask);

                var aeadKey = RelayCrypto.DeriveAeadKey(_root, connSalt);
                var root = _root;
                // Host endpoint: send dir=1 (host→client), expect dir=2 (client→host).
                var relayWs = new RelayWebSocket(
                    transport, aeadKey, RelayCrypto.DirHostToClient, RelayCrypto.DirClientToHost,
                    hn => RelayCrypto.DeriveRekeyedAeadKey(root, connSalt, hn));

                // Drive the hub on this relayed socket, tagged with the phone
                // session id so the killswitch can close it and marked as a
                // relay bridge so the sessions list reports connectedVia="relay".
                var task = _owner._hub.HandleClientAsync(
                    relayWs, SessionTag, MultiplexHub.ClientTransport.Relay, ct);
                return (relayWs, task);
            }

            if (string.Equals(evt, PeerDown, StringComparison.Ordinal))
            {
                // Client went away: end the relayed hub session but KEEP the host
                // socket open so the next client peers up on the same rid.
                session?.CompleteInbound();
                return (null, sessionTask);
            }

            return (session, sessionTask);
        }

        /// <summary>
        /// Pre-pair rendezvous leg for a brand-new phone with no LAN reach. Same
        /// host hello / relay framing as the runtime leg, but a peered-up client
        /// is the phone proving possession of the QR pair token: on peer-up we
        /// derive the claim AEAD key, read exactly ONE sealed BINARY frame (the
        /// claim request), call ClaimCore, and reply with ONE sealed frame
        /// (claim-ok / claim-err). Consuming the token fires the token-changed
        /// event, so Reconcile drops this link and brings up the new session's
        /// runtime link; the link then ends cleanly. A peer-down before the
        /// claim frame just resets and waits for the next peer on the same rid.
        /// Never bridges into the hub.
        /// </summary>
        private async Task RunPairOnceAsync(CancellationToken ct)
        {
            WebSocket transport = await _owner.TransportFactory(_owner.Endpoint, ct).ConfigureAwait(false);
            await using var _ = new WebSocketDisposer(transport);

            await SendHelloAsync(transport, ct).ConfigureAwait(false);

            byte[]? claimKey = null; // non-null once a client has peered up
            var buffer = new byte[ReceiveBufferSize];
            using var message = new System.IO.MemoryStream();

            while (!ct.IsCancellationRequested && transport.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await transport.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (transport.CloseStatus == UnauthorizedClose)
                            _unauthorized = true;
                        return; // relay closed the host socket; outer loop reconnects
                    }
                    if (message.Length + result.Count > MaxFrameBytes)
                        throw new InvalidOperationException("relay frame exceeds 256 KB cap");
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    claimKey = HandlePairControl(message.ToArray());
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (claimKey is null)
                        continue; // a forwarded frame with no peer-up; ignore
                    // The one sealed claim request. Process it, reply, end the link.
                    var consumed = await ProcessClaimFrameAsync(transport, claimKey, message.ToArray())
                        .ConfigureAwait(false);
                    if (consumed)
                        _pairClaimDone = true;
                    return;
                }
            }
        }

        /// <summary>
        /// Handle a TEXT control frame on the pair leg. peer-up (with connSalt)
        /// derives the claim AEAD key for the inbound claim frame; peer-down
        /// clears it so a stale key can't be reused for a different peer. Returns
        /// the derived key (or null) for the caller's "awaiting claim" state.
        /// </summary>
        private byte[]? HandlePairControl(byte[] textBytes)
        {
            string? evt;
            string? saltB64;
            try
            {
                using var doc = JsonDocument.Parse(textBytes);
                var root = doc.RootElement;
                evt = root.TryGetProperty(EventKey, out var e) ? e.GetString() : null;
                saltB64 = root.TryGetProperty(SaltKey, out var s) ? s.GetString() : null;
            }
            catch
            {
                return null;
            }

            if (string.Equals(evt, PeerUp, StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(saltB64))
                    return null;
                byte[] connSalt;
                try { connSalt = RelayCrypto.FromBase64UrlNoPad(saltB64); }
                catch { return null; }
                if (connSalt.Length != RelayCrypto.ConnSaltLength)
                    return null;
                // claimKey = DeriveAeadKey(pairRoot, connSalt) - identical to the
                // runtime AEAD derivation, just keyed off the pairRoot.
                return RelayCrypto.DeriveAeadKey(_root, connSalt);
            }

            // peer-down (or anything else): drop any pending claim key.
            return null;
        }

        /// <summary>
        /// Open the single sealed claim request (dir=2, counter 0), run ClaimCore
        /// for this link's pair token, and send back exactly one sealed reply
        /// (dir=1, counter 0): claim-ok with the new session token on success,
        /// claim-err otherwise. A decrypt failure ends the link with no reply (the
        /// peer doesn't hold the token; possession is what the AEAD decrypt proves)
        /// - neither consumes the token. Returns true when ClaimCore ran (the token
        /// is now spent / decided, so the link is one-shot done); false on a
        /// decrypt / wrong-direction / malformed-request path where the token is
        /// still outstanding and the phone may retry on a fresh peer-up.
        /// </summary>
        private async Task<bool> ProcessClaimFrameAsync(
            WebSocket transport, byte[] claimKey, byte[] frame)
        {
            RelayClaimRequest? request;
            try
            {
                var (dir, _, plaintext) = RelayCrypto.Open(claimKey, frame);
                if (dir != RelayCrypto.DirClientToHost)
                    return false; // wrong direction ⇒ not a client→host claim; drop.
                request = JsonSerializer.Deserialize(plaintext, AppJsonContext.Default.RelayClaimRequest);
            }
            catch (CryptographicException)
            {
                // Tag-verify failed: the peer doesn't hold the pair token. Drop.
                return false;
            }
            catch (JsonException)
            {
                // Decryptable (so the peer holds the token) but malformed JSON.
                await SendSealedAsync(transport, claimKey, ClaimError("bad-request")).ConfigureAwait(false);
                return false;
            }

            if (request is null ||
                !string.Equals(request.Type, RelayClaimMessageTypes.Claim, StringComparison.Ordinal))
            {
                await SendSealedAsync(transport, claimKey, ClaimError("bad-request")).ConfigureAwait(false);
                return false;
            }

            // Possession proven by the successful decrypt; rid_pair already pins
            // which token. Consume it + mint the session (E2E ⇒ long-idle).
            var pairToken = _desired.PairToken ?? "";
            var claim = _owner._pairing.ClaimCore(
                pairToken,
                deviceName: request.DeviceName ?? "",
                userAgent: "",
                remoteAddress: "",
                deviceId: request.DeviceId ?? "",
                overRelay: true,
                claimedOverHttps: false);

            RelayClaimResponse reply = claim.Ok
                ? new RelayClaimResponse
                {
                    Type = RelayClaimMessageTypes.ClaimOk,
                    SessionToken = claim.SessionToken,
                    MachineName = claim.MachineName,
                    Spki = claim.Spki,
                }
                : ClaimError(claim.Error);

            await SendSealedAsync(transport, claimKey, reply).ConfigureAwait(false);
            // ClaimCore ran ⇒ the token is spent or otherwise decided; one-shot done.
            return true;
        }

        private static RelayClaimResponse ClaimError(string error)
            => new() { Type = RelayClaimMessageTypes.ClaimErr, Error = error };

        /// <summary>
        /// Seal one host→client reply (counter 0) and write it as a single BINARY
        /// relay frame. Sent on <see cref="CancellationToken.None"/> on purpose: a
        /// successful claim consumes the pair token, which fires the
        /// token-changed reconcile that CANCELS this link's token - but the phone
        /// is still waiting for its claim-ok, so the terminal reply must complete
        /// regardless. The link ends immediately after either way.
        /// </summary>
        private static async Task SendSealedAsync(
            WebSocket transport, byte[] claimKey, RelayClaimResponse reply)
        {
            var ct = CancellationToken.None;
            var json = JsonSerializer.SerializeToUtf8Bytes(reply, AppJsonContext.Default.RelayClaimResponse);
            var sealed_ = RelayCrypto.Seal(claimKey, RelayCrypto.DirHostToClient, counter: 0, json);
            await transport
                .SendAsync(sealed_, WebSocketMessageType.Binary, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// REST-over-relay tunnel leg for a paired session. Same host hello /
        /// relay framing as the runtime leg, registered on rid_http. On peer-up we
        /// derive the per-connection AEAD key and then loop reading sealed BINARY
        /// request frames (dir=2). Each decrypts to a <see cref="RelayHttpRequest"/>
        /// that we DISPATCH through the service's own endpoint pipeline, authorized
        /// as this session's phone-session id (no phone bearer needed - the relay
        /// session is already authenticated). The sealed <see cref="RelayHttpResponse"/>
        /// (dir=1) echoes the request id so the panel can multiplex concurrent
        /// fetches. A peer-down ends the channel; the host socket stays open for
        /// the next client. The link dies with the session (kick / relay-off /
        /// remote-off) exactly like the runtime leg, because both fall out of the
        /// reconcile `desired` set together. Never touches the hub.
        /// </summary>
        private async Task RunHttpOnceAsync(CancellationToken ct)
        {
            WebSocket transport = await _owner.TransportFactory(_owner.Endpoint, ct).ConfigureAwait(false);
            await using var _ = new WebSocketDisposer(transport);

            await SendHelloAsync(transport, ct).ConfigureAwait(false);

            HttpChannel? channel = null; // non-null once a client has peered up
            var buffer = new byte[ReceiveBufferSize];
            using var message = new System.IO.MemoryStream();

            try
            {
                while (!ct.IsCancellationRequested && transport.State == WebSocketState.Open)
                {
                    message.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await transport.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            if (transport.CloseStatus == UnauthorizedClose)
                                _unauthorized = true;
                            return; // relay closed the host socket; outer loop reconnects
                        }
                        if (message.Length + result.Count > MaxFrameBytes)
                            throw new InvalidOperationException("relay frame exceeds 256 KB cap");
                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        channel = HandleHttpControl(message.ToArray(), transport, channel, ct);
                    }
                    else if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        if (channel is null)
                            continue; // a forwarded frame with no peer-up; ignore
                        channel.OnRequestFrame(message.ToArray());
                    }
                }
            }
            finally
            {
                channel?.Dispose();
            }
        }

        /// <summary>
        /// TEXT control on the HTTP leg. peer-up (with connSalt) opens a fresh
        /// <see cref="HttpChannel"/> (per-connection AEAD key, counter reset to 0);
        /// peer-down (or anything else) tears the current channel down so an
        /// in-flight dispatch's reply isn't sealed under a stale key/counter for a
        /// different peer.
        /// </summary>
        private HttpChannel? HandleHttpControl(
            byte[] textBytes, WebSocket transport, HttpChannel? current, CancellationToken ct)
        {
            string? evt;
            string? saltB64;
            try
            {
                using var doc = JsonDocument.Parse(textBytes);
                var root = doc.RootElement;
                evt = root.TryGetProperty(EventKey, out var e) ? e.GetString() : null;
                saltB64 = root.TryGetProperty(SaltKey, out var s) ? s.GetString() : null;
            }
            catch
            {
                return current;
            }

            if (string.Equals(evt, PeerUp, StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(saltB64))
                    return current;
                byte[] connSalt;
                try { connSalt = RelayCrypto.FromBase64UrlNoPad(saltB64); }
                catch { return current; }
                if (connSalt.Length != RelayCrypto.ConnSaltLength)
                    return current;

                current?.Dispose(); // defensive: relay should have sent peer-down first
                var aeadKey = RelayCrypto.DeriveAeadKey(_root, connSalt);
                var root = _root;
                return new HttpChannel(_owner, transport, aeadKey, SessionTag, ct,
                    hn => RelayCrypto.DeriveRekeyedAeadKey(root, connSalt, hn));
            }

            // peer-down / unknown: end the current channel; keep the host socket open.
            current?.Dispose();
            return null;
        }

        private async Task SendHelloAsync(WebSocket transport, CancellationToken ct)
        {
            var hello = new RelayHostHello
            {
                V = 1,
                Role = "host",
                Rid = _rid,
                Os = OperatingSystem.IsWindows() ? "win"
                    : OperatingSystem.IsMacOS() ? "mac"
                    : OperatingSystem.IsLinux() ? "linux"
                    : "other",
                OsVer = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                App = BuildInfo.Version,
                Dev = "desktop",
                // Null is omitted; the relay refuses a credential-less hello once enforcement is on.
                Cred = Common.ClientCredential.IsOfficial ? Common.ClientCredential.Token : null,
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(hello, AppJsonContext.Default.RelayHostHello);
            await transport
                .SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One peered-up REST-over-relay session on an HTTP leg. Owns the
    /// per-connection AEAD key, the outbound send lock + monotonic counter (so
    /// concurrent replies never reuse a nonce), the inbound replay guard, and a
    /// concurrency limiter. Each inbound sealed request is decrypted, then
    /// dispatched on its own task so a slow handler can't head-of-line block
    /// other ids; the reply is sealed under the send lock and written back. The
    /// request id multiplexes the responses on the single channel.
    /// </summary>
    private sealed class HttpChannel : IDisposable
    {
        private readonly RelayConnectionService _owner;
        private readonly WebSocket _transport;
        private readonly SealedChannelKeys _keys;
        private readonly string _sessionId;
        private readonly CancellationToken _ct;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly SemaphoreSlim _inFlight = new(MaxConcurrentHttpRequests, MaxConcurrentHttpRequests);
        private volatile bool _disposed;

        public HttpChannel(
            RelayConnectionService owner, WebSocket transport, byte[] aeadKey,
            string sessionId, CancellationToken ct, Func<byte[], byte[]>? rekeyDerive = null)
        {
            _owner = owner;
            _transport = transport;
            _keys = new SealedChannelKeys(aeadKey, rekeyDerive);
            _sessionId = sessionId;
            _ct = ct;
        }

        /// <summary>
        /// Decrypt one inbound sealed request frame (dir=2, non-replayed counter)
        /// and dispatch it on a background task. A bad direction / replayed
        /// counter / tamper is dropped silently (the channel keeps serving valid
        /// frames). Invoked serially from the leg's single receive loop, so the
        /// counter check needs no extra lock.
        /// </summary>
        public void OnRequestFrame(byte[] frame)
        {
            if (_disposed)
                return;

            RelayHttpRequest? request;
            try
            {
                var kind = _keys.Open(frame, RelayCrypto.DirClientToHost, out var plaintext);
                if (kind == SealedChannelKeys.InboundKind.Rejected)
                    return; // wrong direction or replay/reorder ⇒ drop.
                if (kind == SealedChannelKeys.InboundKind.RekeyRequest)
                {
                    BeginRekey();
                    return;
                }
                request = JsonSerializer.Deserialize(plaintext, AppJsonContext.Default.RelayHttpRequest);
            }
            catch (CryptographicException)
            {
                return; // tag-verify failed: not our peer's frame; drop.
            }
            catch (JsonException)
            {
                return; // decryptable but malformed; nothing to correlate a reply to.
            }

            if (request is null)
                return;

            // Bound concurrent dispatch; if we're at the cap, reply 503 rather
            // than queue unboundedly. WaitAsync(0) never blocks the receive loop.
            if (!_inFlight.Wait(0))
            {
                _ = SendBusyAsync(request.Id);
                return;
            }

            _ = DispatchAndReplyAsync(request);
        }

        // Holds the send lock from the key switch until the host nonce is on the
        // wire, so no frame sealed under the rekeyed key can precede it.
        private void BeginRekey()
        {
            try
            {
                _sendLock.Wait(_ct);
            }
            catch (Exception)
            {
                return; // disposed or cancelled during teardown
            }
            byte[] frame;
            try
            {
                frame = _keys.SealHostNonceAndRekey(RelayCrypto.DirHostToClient);
            }
            catch (Exception)
            {
                ReleaseSendLock();
                return;
            }
            _ = SendHoldingLockAsync(frame);
        }

        // Dispose can run while a send still holds the lock; releasing a disposed
        // semaphore must not surface as an unobserved exception.
        private void ReleaseSendLock()
        {
            try { _sendLock.Release(); } catch (ObjectDisposedException) { }
        }

        private async Task SendHoldingLockAsync(byte[] frame)
        {
            try
            {
                if (!_disposed && _transport.State == WebSocketState.Open)
                {
                    await _transport
                        .SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, _ct)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _owner._log.LogDebug(ex, "relay http rekey reply failed");
            }
            finally
            {
                ReleaseSendLock();
            }
        }

        private async Task DispatchAndReplyAsync(RelayHttpRequest request)
        {
            try
            {
                var response = await _owner._httpDispatcher
                    .DispatchAsync(request, _sessionId, _ct).ConfigureAwait(false);
                await SendSealedAsync(response).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _owner._log.LogDebug(ex, "relay http dispatch failed for id {Id}", request.Id);
            }
            finally
            {
                _inFlight.Release();
            }
        }

        private async Task SendBusyAsync(int id)
        {
            var response = new RelayHttpResponse
            {
                Id = id,
                Status = StatusCodes.Status503ServiceUnavailable,
                Body = "{\"error\":true,\"msg\":\"too many concurrent relay requests\"}",
                ContentType = "application/json",
            };
            try { await SendSealedAsync(response).ConfigureAwait(false); }
            catch (Exception ex) { _owner._log.LogDebug(ex, "relay http busy reply failed"); }
        }

        private async Task SendSealedAsync(RelayHttpResponse response)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(response, AppJsonContext.Default.RelayHttpResponse);
            await _sendLock.WaitAsync(_ct).ConfigureAwait(false);
            try
            {
                if (_disposed || _transport.State != WebSocketState.Open)
                    return;
                var frame = _keys.Seal(RelayCrypto.DirHostToClient, json);
                await _transport
                    .SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, _ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _sendLock.Dispose();
            _inFlight.Dispose();
        }
    }

    /// <summary>Disposes a WebSocket once the relay loop using it returns / faults.</summary>
    private readonly struct WebSocketDisposer : IAsyncDisposable
    {
        private readonly WebSocket _socket;
        public WebSocketDisposer(WebSocket socket) => _socket = socket;

        public ValueTask DisposeAsync()
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    // Best-effort orderly close; don't block teardown on it.
                    _ = _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "host-link-end", CancellationToken.None);
                }
            }
            catch { /* ignored */ }
            try { _socket.Dispose(); } catch { /* ignored */ }
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// `GET /relays` directory response: the legacy default WSS URL, the region tag
/// → URL map, and the caller's latency-nearest region tag (computed server-side
/// from the Cloudflare edge geo). Deserialized via <see cref="AppJsonContext"/>.
/// </summary>
public sealed class RelayDirectoryResponse
{
    public string Default { get; set; } = "";
    public Dictionary<string, string> Regions { get; set; } = new();
    public string Nearest { get; set; } = "";
}
