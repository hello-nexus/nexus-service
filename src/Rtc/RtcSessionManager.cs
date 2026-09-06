using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nexus.Service.Panel;
using Nexus.Service.Persistence;
using Nexus.Service.Relay;
using Nexus.Service.Sockets;
using SIPSorcery.Net;

namespace Nexus.Service.Rtc;

/// <summary>
/// Owns every live WebRTC DataChannel direct-P2P session for paired phones.
/// Endpoint-driven (constructed lazily on the first <c>POST /rtc/offer</c>),
/// not a <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> - there is
/// no boot-time work, only reactions to offers and to
/// <see cref="IConfigStore.OnChanged"/>.
///
/// One <see cref="RTCPeerConnection"/> per phone session, last-offer-wins: a
/// fresh offer for a session id disposes whatever peer connection preceded it.
/// The phone (offerer) opens two reliable ordered data channels, "runtime" and
/// "http"; this host never creates channels, only answers non-trickle (it
/// waits for its own ICE gathering to finish, capped, before returning the
/// answer SDP - there is no signaling channel left afterward to trickle
/// candidates over).
/// </summary>
public sealed class RtcSessionManager
{
    private const int MaxSdpBytes = 64 * 1024;
    private const int RuntimeChannelGraceMs = 30_000;
    private static readonly TimeSpan IceGatherCap = TimeSpan.FromSeconds(4);

    private readonly ILogger<RtcSessionManager> _log;
    private readonly PanelPhonePairingService _pairing;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;
    private readonly RelayHttpDispatcher _httpDispatcher;

    private readonly object _gate = new();
    private readonly Dictionary<string, RtcSession> _sessions = new(StringComparer.Ordinal);
    // Per-session offer lock: refcounted so a slot is safe to remove the
    // moment nobody holds or awaits it, without a dictionary that only ever
    // grows across the service's uptime (session ids are minted fresh per
    // claim, never reused).
    private readonly Dictionary<string, OfferLock> _offerLocks = new(StringComparer.Ordinal);

    public RtcSessionManager(
        ILogger<RtcSessionManager> log, ILoggerFactory loggerFactory,
        PanelPhonePairingService pairing, IConfigStore store, MultiplexHub hub,
        RelayHttpDispatcher httpDispatcher)
    {
        _log = log;
        _pairing = pairing;
        _store = store;
        _hub = hub;
        _httpDispatcher = httpDispatcher;

        // SIPSorcery.LogFactory is a process-wide singleton; route it through the
        // app's real sinks but floored at Warning so per-packet SIPSorcery debug
        // logging never drowns out the service's own logs.
        SIPSorcery.LogFactory.Set(new WarningOnlyLoggerFactory(loggerFactory));

        _store.OnChanged += OnSettingsChanged;
    }

    public readonly record struct OfferResult(bool Ok, string Sdp, string Error)
    {
        public static OfferResult Fail(string error) => new(false, "", error);
        public static OfferResult Success(string sdp) => new(true, sdp, "");
    }

    public async Task<OfferResult> HandleOfferAsync(string phoneSessionId, RtcOfferRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(phoneSessionId))
            return OfferResult.Fail("no phone session");

        // Serialize the whole validate-swap-negotiate span per session id, so
        // a losing offer's pc.Close() (from the last-offer-wins swap) can
        // never race the winning offer's setRemoteDescription/createAnswer on
        // a live pc. Keyed per session: two different sessions still
        // negotiate fully concurrently, each paying its own ICE gather cap.
        var offerLock = AcquireOfferLock(phoneSessionId);
        await offerLock.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await HandleOfferLockedAsync(phoneSessionId, request, ct).ConfigureAwait(false);
        }
        finally
        {
            offerLock.Semaphore.Release();
            ReleaseOfferLock(phoneSessionId, offerLock);
        }
    }

    private OfferLock AcquireOfferLock(string phoneSessionId)
    {
        lock (_gate)
        {
            if (!_offerLocks.TryGetValue(phoneSessionId, out var offerLock))
            {
                offerLock = new OfferLock();
                _offerLocks[phoneSessionId] = offerLock;
            }
            offerLock.RefCount++;
            return offerLock;
        }
    }

    private void ReleaseOfferLock(string phoneSessionId, OfferLock offerLock)
    {
        lock (_gate)
        {
            offerLock.RefCount--;
            if (offerLock.RefCount == 0 &&
                _offerLocks.TryGetValue(phoneSessionId, out var current) &&
                ReferenceEquals(current, offerLock))
            {
                _offerLocks.Remove(phoneSessionId);
            }
        }
    }

    /// <summary>Per-session offer semaphore, refcounted so removal from <see cref="_offerLocks"/> can never race a concurrent acquire of the same slot.</summary>
    private sealed class OfferLock
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }

    private async Task<OfferResult> HandleOfferLockedAsync(string phoneSessionId, RtcOfferRequest request, CancellationToken ct)
    {
        if (!_pairing.GetRemoteControlEnabled() || !_pairing.GetRelayEnabled())
            return OfferResult.Fail("remote access disabled");

        var relayRoot = ResolveRelayRoot(phoneSessionId);
        if (relayRoot is null)
            return OfferResult.Fail("unknown session");

        byte[] runtimeSalt;
        byte[] httpSalt;
        try
        {
            runtimeSalt = RelayCrypto.FromBase64UrlNoPad(request.RuntimeSalt ?? "");
            httpSalt = RelayCrypto.FromBase64UrlNoPad(request.HttpSalt ?? "");
        }
        catch (FormatException)
        {
            return OfferResult.Fail("bad salt");
        }
        if (runtimeSalt.Length != RelayCrypto.ConnSaltLength || httpSalt.Length != RelayCrypto.ConnSaltLength)
            return OfferResult.Fail("bad salt");
        // Equal salts derive the same AEAD key for both channels; each
        // channel's dir=1 counter independently starts at 0, so the first
        // host->client frame on each would reuse the same (key, nonce) pair.
        if (runtimeSalt.AsSpan().SequenceEqual(httpSalt))
            return OfferResult.Fail("bad salt");

        var offerSdp = request.Sdp ?? "";
        if (offerSdp.Length == 0 || Encoding.UTF8.GetByteCount(offerSdp) >= MaxSdpBytes)
            return OfferResult.Fail("bad offer");

        var runtimeKey = RelayCrypto.DeriveAeadKey(relayRoot, runtimeSalt);
        var httpKey = RelayCrypto.DeriveAeadKey(relayRoot, httpSalt);

        var config = new RTCConfiguration
        {
            iceServers = new List<RTCIceServer>
            {
                new() { urls = "stun:stun.cloudflare.com:3478" },
                new() { urls = "stun:stun.l.google.com:19302" },
            },
        };
        var pc = new RTCPeerConnection(config);
        var session = new RtcSession(this, phoneSessionId, pc, runtimeKey, httpKey,
            hn => RelayCrypto.DeriveRekeyedAeadKey(relayRoot, runtimeSalt, hn),
            hn => RelayCrypto.DeriveRekeyedAeadKey(relayRoot, httpSalt, hn));
        session.Wire();
        // Starts here, not after the answer: an escape from the negotiate
        // span below (a request-abort cancellation, an unexpected SIPSorcery
        // exception) still leaves this timer as the only thing guaranteed to
        // clean up the pc, since onconnectionstatechange may never reach
        // failed/disconnected for an offer that never finishes signaling.
        session.StartRuntimeGraceTimer(TimeSpan.FromMilliseconds(RuntimeChannelGraceMs));

        // Last-offer-wins: the per-session offer lock already makes this
        // sequential with any other offer for the same id, so swapping the
        // session in here can never race a concurrent negotiation on the
        // pc it replaces.
        RtcSession? prior;
        lock (_gate)
        {
            _sessions.TryGetValue(phoneSessionId, out prior);
            _sessions[phoneSessionId] = session;
        }
        prior?.Dispose();

        var offerInit = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp };
        SetDescriptionResultEnum setResult;
        try
        {
            setResult = pc.setRemoteDescription(offerInit);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "rtc: setRemoteDescription threw for session {SessionId}", phoneSessionId);
            CloseSession(phoneSessionId, session);
            return OfferResult.Fail("bad offer");
        }
        if (setResult != SetDescriptionResultEnum.OK)
        {
            CloseSession(phoneSessionId, session);
            return OfferResult.Fail("bad offer");
        }

        await WaitForIceGatheringAsync(pc, IceGatherCap, ct).ConfigureAwait(false);

        RTCSessionDescriptionInit answerInit;
        try
        {
            answerInit = pc.createAnswer(null);
            await pc.setLocalDescription(answerInit).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "rtc: failed to build answer for session {SessionId}", phoneSessionId);
            CloseSession(phoneSessionId, session);
            return OfferResult.Fail("failed to create answer");
        }

        return OfferResult.Success(answerInit.sdp);
    }

    private byte[]? ResolveRelayRoot(string phoneSessionId)
    {
        foreach (var s in _pairing.GetActiveRelaySessions())
        {
            if (string.Equals(s.Id, phoneSessionId, StringComparison.Ordinal))
                return s.RelayRoot;
        }
        return null;
    }

    /// <summary>
    /// Non-trickle ICE: wait for gathering to finish (host + STUN srflx
    /// candidates), capped so a slow/unreachable STUN server can never hang the
    /// request. Does not use RTCAnswerOptions.X_WaitForIceGatheringToComplete -
    /// that option's internal wait (RTCPeerConnection.createBaseSdp) blocks on
    /// _iceCompletedGatheringTask.Task.Wait() with no cancellation applied
    /// despite constructing a token for it, so an unreachable STUN server hangs
    /// the calling thread forever. This wait is async and always bounded.
    /// </summary>
    private static async Task WaitForIceGatheringAsync(RTCPeerConnection pc, TimeSpan cap, CancellationToken ct)
    {
        if (pc.iceGatheringState == RTCIceGatheringState.complete)
            return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnState(RTCIceGatheringState state)
        {
            if (state == RTCIceGatheringState.complete)
                tcs.TrySetResult();
        }

        pc.onicegatheringstatechange += OnState;
        try
        {
            if (pc.iceGatheringState == RTCIceGatheringState.complete)
                return;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(cap);
            try { await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Gather cap reached; answer with whatever candidates gathered so far.
            }
        }
        finally
        {
            pc.onicegatheringstatechange -= OnState;
        }
    }

    /// <summary>Removes and disposes a session only if it is still the live one for that id (guards a stale close racing a newer offer).</summary>
    private void CloseSession(string sessionId, RtcSession expected)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, expected))
                _sessions.Remove(sessionId);
        }
        expected.Dispose();
    }

    /// <summary>Closes whichever session currently owns <paramref name="sessionId"/>, if any.</summary>
    public void CloseSession(string sessionId)
    {
        RtcSession? session;
        lock (_gate)
        {
            if (!_sessions.Remove(sessionId, out session))
                return;
        }
        session.Dispose();
    }

    public void CloseAll()
    {
        List<RtcSession> sessions;
        lock (_gate)
        {
            sessions = new List<RtcSession>(_sessions.Values);
            _sessions.Clear();
        }
        foreach (var s in sessions)
            s.Dispose();
    }

    private void OnSettingsChanged() => _ = Task.Run(Reconcile);

    /// <summary>
    /// Closes every session when remote-control or relay turns off, and prunes
    /// any session whose backing phone-session record was individually revoked
    /// (no dedicated revoke event exists on PanelPhonePairingService; a revoke
    /// mutates settings, which fires OnChanged the same as any other change).
    /// </summary>
    private void Reconcile()
    {
        if (!_pairing.GetRemoteControlEnabled() || !_pairing.GetRelayEnabled())
        {
            CloseAll();
            return;
        }

        var liveIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in _pairing.GetActiveRelaySessions())
            liveIds.Add(s.Id);

        List<KeyValuePair<string, RtcSession>> stale;
        lock (_gate)
        {
            stale = new List<KeyValuePair<string, RtcSession>>();
            foreach (var kv in _sessions)
            {
                if (!liveIds.Contains(kv.Key))
                    stale.Add(kv);
            }
        }
        foreach (var kv in stale)
            CloseSession(kv.Key, kv.Value);
    }

    /// <summary>
    /// One phone session's direct-P2P peer connection: the "runtime" channel
    /// bridged into <see cref="MultiplexHub"/> exactly like a relay session, the
    /// "http" channel driving <see cref="RtcHttpChannel"/>, and the teardown
    /// triggers (grace timer, connection-state changes, adapter aborts) that
    /// all converge on <see cref="RtcSessionManager.CloseSession(string, RtcSession)"/>.
    /// </summary>
    private sealed class RtcSession : IDisposable
    {
        private readonly RtcSessionManager _owner;
        private readonly string _sessionId;
        private readonly RTCPeerConnection _pc;
        private readonly byte[] _runtimeKey;
        private readonly byte[] _httpKey;
        private readonly Func<byte[], byte[]> _runtimeRekey;
        private readonly Func<byte[], byte[]> _httpRekey;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _lock = new();

        private RtcHttpChannel? _httpChannel;
        private CancellationTokenSource? _graceCts;
        private volatile bool _runtimeOpened;
        private volatile bool _disposed;

        public RtcSession(RtcSessionManager owner, string sessionId, RTCPeerConnection pc, byte[] runtimeKey, byte[] httpKey,
            Func<byte[], byte[]> runtimeRekey, Func<byte[], byte[]> httpRekey)
        {
            _owner = owner;
            _sessionId = sessionId;
            _pc = pc;
            _runtimeKey = runtimeKey;
            _httpKey = httpKey;
            _runtimeRekey = runtimeRekey;
            _httpRekey = httpRekey;
        }

        public void Wire()
        {
            _pc.ondatachannel += OnDataChannel;
            _pc.onconnectionstatechange += OnConnectionStateChange;
        }

        private void OnDataChannel(RTCDataChannel dc)
        {
            if (string.Equals(dc.label, "runtime", StringComparison.Ordinal))
                WireRuntime(dc);
            else if (string.Equals(dc.label, "http", StringComparison.Ordinal))
                WireHttp(dc);
            else
                try { dc.close(); } catch { /* unexpected label; refuse it */ }
        }

        private void WireRuntime(RTCDataChannel dc)
        {
            lock (_lock)
            {
                if (_runtimeOpened)
                {
                    CloseDuplicateChannel(dc, "runtime");
                    return;
                }
                _runtimeOpened = true;
            }
            CancelGraceTimer();

            var transport = new RtcDataChannelTransport(dc);
            var relayWs = new RelayWebSocket(
                transport, _runtimeKey, RelayCrypto.DirHostToClient, RelayCrypto.DirClientToHost, _runtimeRekey);
            transport.OnAborted += () => _owner.CloseSession(_sessionId, this);
            dc.onmessage += (_, proto, data) =>
            {
                if (proto == DataChannelPayloadProtocols.WebRTC_Binary)
                    relayWs.EnqueueInbound(data);
            };
            _ = _owner._hub.HandleClientAsync(relayWs, _sessionId, MultiplexHub.ClientTransport.Direct, _cts.Token);
        }

        private void WireHttp(RTCDataChannel dc)
        {
            lock (_lock)
            {
                if (_httpChannel is not null)
                {
                    CloseDuplicateChannel(dc, "http");
                    return;
                }
                _httpChannel = new RtcHttpChannel(_owner._httpDispatcher, dc, _httpKey, _sessionId, _owner._log, _cts.Token, _httpRekey);
            }
        }

        /// <summary>A second channel claiming an already-wired label is closed; the first channel keeps serving.</summary>
        private void CloseDuplicateChannel(RTCDataChannel dc, string label)
        {
            _owner._log.LogDebug("rtc: duplicate {Label} channel for session {SessionId}", label, _sessionId);
            try { dc.close(); } catch { /* best effort */ }
        }

        private void OnConnectionStateChange(RTCPeerConnectionState state)
        {
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected or RTCPeerConnectionState.closed)
                _owner.CloseSession(_sessionId, this);
        }

        /// <summary>Disposes the session if the "runtime" channel never opens within <paramref name="grace"/> of session creation.</summary>
        public void StartRuntimeGraceTimer(TimeSpan grace)
        {
            CancellationTokenSource cts;
            lock (_lock)
            {
                if (_runtimeOpened || _disposed)
                    return;
                cts = new CancellationTokenSource();
                _graceCts = cts;
            }
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(grace, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (!_runtimeOpened)
                    _owner.CloseSession(_sessionId, this);
            });
        }

        private void CancelGraceTimer()
        {
            CancellationTokenSource? cts;
            lock (_lock)
            {
                cts = _graceCts;
                _graceCts = null;
            }
            if (cts is null)
                return;
            try { cts.Cancel(); } catch { /* already disposed */ }
            cts.Dispose();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CancelGraceTimer();
            try { _cts.Cancel(); } catch { /* already disposed */ }
            _httpChannel?.Dispose();
            try { _pc.Close("rtc session disposed"); } catch { /* best effort */ }
            _cts.Dispose();
        }
    }

    private sealed class WarningOnlyLoggerFactory : ILoggerFactory
    {
        private readonly ILoggerFactory _inner;
        public WarningOnlyLoggerFactory(ILoggerFactory inner) => _inner = inner;
        public void AddProvider(ILoggerProvider provider) => _inner.AddProvider(provider);
        public ILogger CreateLogger(string categoryName) => new WarningOnlyLogger(_inner.CreateLogger(categoryName));
        public void Dispose() { /* the app owns the wrapped factory's lifetime */ }
    }

    private sealed class WarningOnlyLogger : ILogger
    {
        private readonly ILogger _inner;
        public WarningOnlyLogger(ILogger inner) => _inner = inner;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && _inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                _inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
