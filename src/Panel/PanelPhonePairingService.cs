using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Nexus.Service.Models.Panel;
using Nexus.Service.Net;
using Nexus.Service.Persistence;
using Nexus.Service.Sockets;

namespace Nexus.Service.Panel;

public sealed class PanelPhonePairingService
{
    public const string PresenceTopic = "panel/phone/presence";
    public const string SessionCookieName = "nexus_phone_token";
    public static readonly TimeSpan SessionIdle = TimeSpan.FromDays(30);
    /// <summary>
    /// Sessions claimed over plain HTTP (browser fallback) get a much
    /// shorter idle window than the SPKI-pinned HTTPS path, so a sniffed
    /// cookie expires in hours rather than weeks.
    /// </summary>
    public static readonly TimeSpan HttpSessionIdle = TimeSpan.FromHours(24);
    private const int PairTtlSeconds = 60;
    private const int MaxSessions = 12;
    /// <summary>
    /// Upper bound on an accepted client-provided <c>deviceId</c>. A UUID is 36
    /// chars; this leaves slack for a vendor-prefixed form while rejecting absurd
    /// values that a misbehaving / hostile client could use to bloat settings.json.
    /// An over-length id is treated as "no dedup id" (empty) rather than truncated.
    /// </summary>
    private const int MaxDeviceIdLength = 128;
    private const long LastSeenRefreshMs = 60_000;
    private const long RecentSessionWindowMs = LastSeenRefreshMs * 2 + 15_000;
    private static readonly long SessionIdleMs = (long)SessionIdle.TotalMilliseconds;
    private static readonly long HttpSessionIdleMs = (long)HttpSessionIdle.TotalMilliseconds;

    // Manual pair-code (BT-SSP-style numeric comparison) tunables. Matches
    // the QR TTL so both pairing paths feel identical from the dashboard.
    public const int PairCodeTtlSeconds = 60;
    private const int PairCodeMaxAttempts = 5;
    private const long PairCodeLockoutMs = 5 * 60 * 1000;
    private static readonly byte[] SasInfoPrefix = Encoding.UTF8.GetBytes("nexus-pair-sas-v1|");

    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _pairTokens = new(StringComparer.Ordinal);

    // Pair-code state. _pairCode holds the one active code (replaced on
    // /start); _pairCodeAttempts tracks per-IP brute-force lockouts.
    private readonly object _pairCodeLock = new();
    private PairCodeState? _pairCode;
    private readonly Dictionary<string, PairCodeAttempt> _pairCodeAttempts = new(StringComparer.Ordinal);

    public int ServicePort { get; set; } = 9400;
    public int HttpsPort { get; set; }

    /// <summary>
    /// SHA-256(SPKI) of the local HTTPS cert, base64url-no-pad. Embedded in
    /// the pair QR's Universal Link as the `fp` query param so the iOS
    /// companion app can cert-pin before its first TLS handshake.
    /// </summary>
    public string SpkiFingerprint { get; set; } = string.Empty;

    /// <summary>
    /// Latency-nearest relay region tag (e.g. "ap"), stamped into the pair QR as
    /// the `r` query param so the phone connects to the SAME regional relay this
    /// host registered on. Empty ⇒ no tag ⇒ the phone uses the legacy default.
    /// Set by <c>RelayConnectionService</c> once the /relays directory resolves.
    /// </summary>
    public string RelayRegionTag { get; set; } = string.Empty;

    /// <summary>
    /// Public Universal Link host. The QR encodes
    /// `https://&lt;PublicLinkHost&gt;/r/pair?host=&lt;lan&gt;&port=&lt;p&gt;&pair=&lt;t&gt;&fp=&lt;spki&gt;`
    /// so iOS Camera + Universal Links + the app's in-app scanner all decode
    /// the same payload. The web fallback at /r/pair handles "no app installed"
    /// by redirecting to the LAN URL or linking to the App Store.
    /// </summary>
    public string PublicLinkHost { get; set; } = "hellonexus.com";

    /// <summary>
    /// Resolves the user-visible host PC name. Reads
    /// NexusSettings.HostDisplayName each call so a settings update is
    /// reflected immediately in the next QR / claim / /ping payload, then
    /// falls back to the OS-reported machine name when the override is empty.
    /// </summary>
    public string MachineName => ResolveMachineName();

    public PanelPhonePairingService(IConfigStore store, MultiplexHub hub)
    {
        _store = store;
        _hub = hub;

        // Retain the currently-pending pair request so a dashboard that
        // connects mid-handshake (e.g. one opened from the tray pairing
        // notification, after the one-shot live broadcast already fired)
        // immediately receives it on subscribe and pops the Allow/Deny
        // modal. Returns null once the request is decided / denied /
        // expired so a late or reconnecting dashboard never resurrects a
        // dead prompt.
        _hub.RegisterSnapshotProvider(PanelTopics.PairCodeRequest, BuildPendingRequestSnapshot);
    }

    /// <summary>
    /// Fired when a phone submits a pair request but no dashboard is
    /// currently subscribed to surface the Allow/Deny modal - i.e. the
    /// Nexus window isn't open. The Windows tray host turns this into a
    /// native notification; clicking it opens the dashboard, which then
    /// receives the retained request via the snapshot provider. No-op on
    /// platforms / modes without a tray host subscribed.
    /// </summary>
    public event Action<PairAttentionNotice>? PairRequestNeedsAttention;

    /// <summary>
    /// Fired when the pending request reaches a terminal state (allowed,
    /// denied, cancelled, or expired) so the tray host can dismiss any
    /// notification it raised for it.
    /// </summary>
    public event Action? PairRequestResolved;

    /// <summary>Minimal payload for <see cref="PairRequestNeedsAttention"/>.</summary>
    public sealed record PairAttentionNotice(string DeviceLabel);

    /// <summary>
    /// Fired whenever the set of outstanding (unconsumed, unexpired) QR pair
    /// tokens changes - a token is minted in <see cref="CreatePairQr"/>, consumed
    /// in <see cref="ClaimCore"/>, or reaped on expiry. <c>RelayConnectionService</c>
    /// listens on this to reconcile its per-token pair rendezvous links the same
    /// way it reconciles session links off <see cref="IConfigStore.OnChanged"/>:
    /// it desires one host link per outstanding pair token (keyed by rid_pair)
    /// when relay + remote control are on, and drops a link as soon as its token
    /// leaves the set. No poll loop; this is the push signal.
    /// </summary>
    public event Action? OutstandingPairTokensChanged;

    /// <summary>
    /// An outstanding QR pair token plus its derived <see cref="PairRoot"/>. The
    /// relay client uses <see cref="PairRoot"/> to derive the pair rendezvous id
    /// (rid_pair) and the per-connection claim AEAD key; the plaintext
    /// <see cref="Token"/> is what <see cref="ClaimCore"/> consumes once a phone
    /// proves possession by completing the sealed handshake.
    /// </summary>
    public readonly record struct OutstandingPairToken(string Token, byte[] PairRoot);

    /// <summary>
    /// Snapshot the live (unconsumed, unexpired) pair tokens, each with its
    /// derived pairRoot, for the relay client to register a pair rendezvous per
    /// token. Prunes expired tokens first so the relay never desires a link for a
    /// token that can no longer be claimed.
    /// </summary>
    public IReadOnlyList<OutstandingPairToken> GetOutstandingPairTokens()
    {
        List<string> tokens;
        int reaped;
        lock (_lock)
        {
            reaped = PruneExpiredPairsLocked();
            tokens = _pairTokens.Keys.ToList();
        }
        // If expiry shrank the set, push the change so any reconcile that raced
        // this read converges (and stale pair links get dropped) - no poll loop.
        if (reaped > 0)
            RaisePairTokensChanged();
        var result = new List<OutstandingPairToken>(tokens.Count);
        foreach (var token in tokens)
            result.Add(new OutstandingPairToken(token, Nexus.Service.Relay.RelayCrypto.DerivePairRoot(token)));
        return result;
    }

    private void RaisePairTokensChanged()
    {
        try { OutstandingPairTokensChanged?.Invoke(); }
        catch { /* listener is best-effort; never let it break a claim/mint */ }
    }

    /// <summary>
    /// Single chokepoint for every pair-code "request" and "cancelled"
    /// frame. Fans the frame out to live dashboard subscribers, and - for a
    /// fresh "request" with no subscriber listening - raises
    /// <see cref="PairRequestNeedsAttention"/> so the desktop surfaces a
    /// notification. "cancelled" frames resolve any raised notification.
    /// </summary>
    private void PublishPairCodeFrame(PanelPhonePairCodeRequestFrame frame)
    {
        PanelTopics.BroadcastPairCodeRequest(_hub, frame);

        if (string.Equals(frame.Kind, "request", StringComparison.Ordinal))
        {
            // Gate on live subscribers: if a dashboard is connected it
            // shows the modal itself, so a notification would be redundant.
            if (!_hub.TopicHasSubscribers(PanelTopics.PairCodeRequest))
            {
                try { PairRequestNeedsAttention?.Invoke(new PairAttentionNotice(frame.DeviceLabel)); }
                catch { /* notification is best-effort */ }
            }
        }
        else if (string.Equals(frame.Kind, "cancelled", StringComparison.Ordinal))
        {
            ResolvePairAttention();
        }
    }

    private void ResolvePairAttention()
    {
        try { PairRequestResolved?.Invoke(); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Snapshot provider for <c>panel/phone/pair-code/request</c>. Returns
    /// the live request envelope only while it is still awaiting the
    /// host's Allow/Deny - a phone has submitted (RequestId set), the host
    /// hasn't decided, and the TTL hasn't lapsed. Any other state returns
    /// null so a connecting dashboard sees nothing stale.
    /// </summary>
    private ReadOnlyMemory<byte>? BuildPendingRequestSnapshot()
    {
        PanelPhonePairCodeRequestFrame frame;
        lock (_pairCodeLock)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_pairCode is not { } state) return null;
            if (string.IsNullOrEmpty(state.RequestId)) return null; // no phone has submitted yet
            if (state.HostApproved || state.HostDenied) return null; // host already decided
            if (state.ExpiresAt <= nowMs) return null;               // expired

            frame = new PanelPhonePairCodeRequestFrame
            {
                Kind = "request",
                RequestId = state.RequestId,
                Sas = state.Sas,
                DeviceLabel = DescribeDevice(state.PhoneUserAgent),
                RemoteAddress = state.PhoneRemoteAddress,
                UserAgent = state.PhoneUserAgent,
                ExpiresAt = state.ExpiresAt,
            };
        }
        return PanelTopics.BuildPairCodeRequestEnvelope(frame);
    }

    private string ResolveMachineName()
    {
        var settings = _store.Load();
        var overrideName = settings.HostDisplayName;
        if (!string.IsNullOrWhiteSpace(overrideName))
            return overrideName;
        return GetDefaultMachineName();
    }

    /// <summary>
    /// Persists the user-overridden host display name. Whitespace clears
    /// the override so subsequent reads fall back to the OS machine name.
    /// Input is trimmed, internal whitespace runs collapsed, and length
    /// capped at 64 chars (matching the QR/claim normalisation) so a
    /// LAN client can't bloat settings.json with arbitrary bytes.
    /// </summary>
    public string SetHostDisplayName(string? value)
    {
        string normalized;
        if (string.IsNullOrWhiteSpace(value))
        {
            normalized = "";
        }
        else
        {
            var collapsed = string.Join(
                " ",
                value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
            normalized = collapsed.Length <= 64 ? collapsed : collapsed[..64];
        }
        _store.Update(s =>
        {
            s.HostDisplayName = normalized;
        });
        return ResolveMachineName();
    }

    public PanelPhonePairQrResponse CreatePairQr()
    {
        var token = CreateToken(24);
        var expires = DateTime.UtcNow.AddSeconds(PairTtlSeconds);
        lock (_lock)
        {
            PruneExpiredPairsLocked();
            _pairTokens[token] = expires;
        }
        // A fresh outstanding token: the relay registers its pair rendezvous.
        RaisePairTokensChanged();

        var lanHost = LocalNetwork.GetLocalIp();
        var lanPort = HttpsPort > 0 ? HttpsPort : ServicePort;
        var machineName = NormalizeMachineName(MachineName);
        var url = BuildPairUrl(lanHost, lanPort, token, machineName);
        return new PanelPhonePairQrResponse
        {
            Url = url,
            QrDataUrl = LocalNetwork.GenerateQrSvgDataUrl(url),
            MachineName = machineName,
            TtlSeconds = PairTtlSeconds,
            ExpiresAt = new DateTimeOffset(expires).ToUnixTimeMilliseconds(),
            HttpPort = ServicePort,
        };
    }

    private string BuildPairUrl(string lanHost, int lanPort, string pairToken, string machineName)
    {
        // When the public link host is configured, emit the Universal Link
        // form so iOS hands the URL off to the installed app. When unset
        // (testing), fall back to the legacy direct LAN URL so phones on
        // the same network can still open the panel in Safari.
        // hellonexus.com/r/pair is ours, so a build with no credential emits the
        // direct LAN URL instead - the same fallback an unset host already uses.
        if (string.IsNullOrWhiteSpace(PublicLinkHost) || !Common.ClientCredential.IsOfficial)
        {
            var scheme = HttpsPort > 0 ? "https" : "http";
            return $"{scheme}://{lanHost}:{lanPort}/panel/phone?pair={Uri.EscapeDataString(pairToken)}&machineName={Uri.EscapeDataString(machineName)}";
        }

        var qs = new StringBuilder();
        qs.Append("host=").Append(Uri.EscapeDataString(lanHost));
        qs.Append("&port=").Append(lanPort);
        qs.Append("&pair=").Append(Uri.EscapeDataString(pairToken));
        qs.Append("&machineName=").Append(Uri.EscapeDataString(machineName));
        if (!string.IsNullOrEmpty(SpkiFingerprint))
        {
            qs.Append("&fp=").Append(Uri.EscapeDataString(SpkiFingerprint));
        }
        // Regional relay tag: the phone resolves `r` to the same relay this host
        // registered on (absent ⇒ legacy default). Optional + ignored by the LAN
        // path, so old clients are unaffected.
        if (!string.IsNullOrEmpty(RelayRegionTag))
        {
            qs.Append("&r=").Append(Uri.EscapeDataString(RelayRegionTag));
        }
        // Plain-HTTP port for the browser fallback at /r/pair (Continue in
        // browser). Native iOS uses `port` (HTTPS) + SPKI pinning instead.
        qs.Append("&httpPort=").Append(ServicePort);
        return $"https://{PublicLinkHost}/r/pair?{qs}";
    }

    public PanelPhoneClaimResponse Claim(string pairToken, HttpContext context)
        => Claim(pairToken, deviceId: "", deviceName: "", context);

    public PanelPhoneClaimResponse Claim(string pairToken, string? deviceId, HttpContext context)
        => Claim(pairToken, deviceId, deviceName: "", context);

    public PanelPhoneClaimResponse Claim(string pairToken, string? deviceId, string? deviceName, HttpContext context, bool supportsSasApproval = false)
    {
        var userAgent = context.Request.Headers["User-Agent"].ToString();
        var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "";
        // A deviceId on the POST body wins; fall back to the ?deviceId= query
        // param. Both claim paths funnel the same stable id into ClaimCore.
        var effectiveDeviceId = string.IsNullOrWhiteSpace(deviceId)
            ? context.Request.Query["deviceId"].ToString()
            : deviceId;
        var result = ClaimCore(
            pairToken,
            // The client-detected class (iPad / Android tablet) wins; the UA guess
            // can't tell an iPad from a Mac or a Samsung tablet from a phone.
            // Blank falls back to DescribeDevice(UA) inside ClaimCore.
            deviceName: deviceName ?? "",
            userAgent: userAgent,
            remoteAddress: remoteAddress,
            deviceId: effectiveDeviceId,
            overRelay: false,
            claimedOverHttps: context.Request.IsHttps,
            supportsSasApproval: supportsSasApproval);

        if (!result.Ok)
        {
            return new PanelPhoneClaimResponse { Paired = false, Error = result.Error };
        }

        if (result.NeedsApproval)
        {
            return new PanelPhoneClaimResponse
            {
                Paired = false,
                NeedsApproval = true,
                RequestId = result.RequestId,
                Sas = result.Sas,
                SpkiFingerprint = result.Spki,
                MachineName = result.MachineName,
                ExpiresAt = result.ExpiresAt,
            };
        }

        return new PanelPhoneClaimResponse
        {
            Paired = true,
            Token = result.SessionToken,
            MachineName = result.MachineName,
            // The phone's paired-PC store dedups records by this fingerprint;
            // the relay claim path already returns it.
            SpkiFingerprint = string.IsNullOrEmpty(SpkiFingerprint) ? null : SpkiFingerprint,
        };
    }

    /// <summary>
    /// Outcome of <see cref="ClaimCore"/>. On success <see cref="SessionToken"/>
    /// is the plaintext token the caller hands back to the phone (it is gone from
    /// the PC afterward, hash-only), and <see cref="Spki"/> is the leaf SPKI
    /// fingerprint. On failure <see cref="Error"/> mirrors the HTTP claim's
    /// error sentinels.
    /// </summary>
    public readonly record struct ClaimResult(
        bool Ok, string SessionToken, string MachineName, string Spki, string Error,
        bool NeedsApproval, string RequestId, string Sas, long ExpiresAt)
    {
        public static ClaimResult Fail(string error) => new(false, "", "", "", error, false, "", "", 0);
    }

    private bool AlreadyPaired(string deviceId, string fingerprint)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sessions = _store.Load().Auth?.PanelPhoneSessions;
        if (sessions is null)
        {
            return false;
        }
        foreach (var session in sessions)
        {
            var lastSeen = session.LastSeenAt > 0 ? session.LastSeenAt : session.CreatedAt;
            var idleLimit = session.ClaimedOverHttps ? SessionIdleMs : HttpSessionIdleMs;
            if (lastSeen <= 0 || now - lastSeen > idleLimit)
            {
                continue;
            }
            if (!string.IsNullOrEmpty(deviceId) &&
                string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal))
            {
                return true;
            }
            if (string.IsNullOrEmpty(deviceId) &&
                !string.IsNullOrEmpty(fingerprint) &&
                string.Equals(GetSessionFingerprint(session), fingerprint, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Single chokepoint for minting + storing a paired-phone session, shared by
    /// the LAN HTTP <see cref="Claim"/> and the claim-over-relay path. Validates
    /// the pair token (exists, not expired), CONSUMES it single-use (firing the
    /// outstanding-token-changed event so the relay drops its pair rendezvous and
    /// picks up the new session's runtime rendezvous), then mints a session token,
    /// derives + stores its RelayKey, and returns the plaintext token once.
    ///
    /// <paramref name="overRelay"/> records the provenance; a relay-claimed
    /// session is end-to-end authenticated (the AEAD decrypt of the claim request
    /// already proves token possession), so it is stored
    /// <see cref="PanelPhoneSessionToken.ClaimedOverHttps"/>=true to get the long
    /// 30-day idle window, exactly like an SPKI-pinned HTTPS claim. There is no
    /// remote IP / per-request UA bind on a relayed session - the relay obscures
    /// the client address and a hard bind would 401 the app on every reconnect -
    /// so it follows the same skip the HTTPS path uses.
    /// </summary>
    public ClaimResult ClaimCore(
        string pairToken,
        string deviceName,
        string userAgent,
        string remoteAddress,
        string deviceId,
        bool overRelay,
        bool claimedOverHttps,
        bool supportsSasApproval = false)
    {
        if (!GetRemoteControlEnabled())
            return ClaimResult.Fail("remote-disabled");

        if (string.IsNullOrWhiteSpace(pairToken))
            return ClaimResult.Fail("missing pairing token");

        bool consumed;
        lock (_lock)
        {
            PruneExpiredPairsLocked();
            if (!_pairTokens.TryGetValue(pairToken, out var expires) || expires <= DateTime.UtcNow)
            {
                _pairTokens.Remove(pairToken);
                return ClaimResult.Fail("pairing token expired");
            }
            _pairTokens.Remove(pairToken);
            consumed = true;
        }

        // The token is gone from the outstanding set: tell the relay so it tears
        // down the pair rendezvous (and, once the session below is persisted,
        // brings up that session's runtime rendezvous on the next reconcile).
        if (consumed)
        {
            RaisePairTokensChanged();
        }

        // SAS gate: new device on LAN HTTPS with opt-in flag.
        var normalizedDeviceIdForGate = NormalizeDeviceId(deviceId);
        var fingerprintForGate = overRelay ? "" : BuildDeviceFingerprint(userAgent, remoteAddress);
        if (!overRelay &&
            claimedOverHttps &&
            supportsSasApproval &&
            !AlreadyPaired(normalizedDeviceIdForGate, fingerprintForGate))
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var expires = nowMs + PairCodeTtlSeconds * 1000L;
            var nonce = RandomNumberGenerator.GetBytes(16);
            var requestId = CreateToken(16);
            var sas = ComputeSas(string.Empty, nonce, SpkiFingerprint);
            var deviceLabel = string.IsNullOrWhiteSpace(deviceName) ? DescribeDevice(userAgent) : deviceName.Trim();
            if (deviceLabel.Length > 64)
            {
                deviceLabel = deviceLabel[..64];
            }
            var normalizedRemote = NormalizeRemoteAddress(remoteAddress);

            string? supersededRequestId = null;
            lock (_pairCodeLock)
            {
                if (_pairCode is { RequestId: { Length: > 0 } prior } && _pairCode.ExpiresAt > nowMs)
                {
                    supersededRequestId = prior;
                }

                _pairCode = new PairCodeState
                {
                    Code = string.Empty,
                    Nonce = nonce,
                    ExpiresAt = expires,
                    RequestId = requestId,
                    Sas = sas,
                    PhoneRemoteAddress = normalizedRemote,
                    PhoneUserAgent = userAgent,
                    DeviceId = normalizedDeviceIdForGate,
                    ClaimedOverHttps = true,
                };
            }

            if (supersededRequestId is not null)
            {
                PublishPairCodeFrame(new PanelPhonePairCodeRequestFrame
                {
                    Kind = "cancelled",
                    RequestId = supersededRequestId,
                    Reason = "host-started-new-code",
                });
            }

            PublishPairCodeFrame(new PanelPhonePairCodeRequestFrame
            {
                Kind = "request",
                RequestId = requestId,
                Sas = sas,
                DeviceLabel = deviceLabel,
                RemoteAddress = normalizedRemote,
                UserAgent = userAgent,
                ExpiresAt = expires,
            });

            return new ClaimResult(
                Ok: true,
                SessionToken: "",
                MachineName: NormalizeMachineName(MachineName),
                Spki: SpkiFingerprint,
                Error: "",
                NeedsApproval: true,
                RequestId: requestId,
                Sas: sas,
                ExpiresAt: expires);
        }

        var sessionToken = CreateToken(32);
        var hash = HashToken(sessionToken);
        var relayKey = ComputeRelayKey(sessionToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var normalizedName = NormalizeSessionDisplayName(deviceName, userAgent);
        // A relayed claim is E2E-authenticated and carries no usable client IP,
        // so it skips the IP+UA bind - same treatment as an HTTPS-pinned claim.
        var effectiveHttps = overRelay || claimedOverHttps;
        var deviceFingerprint = overRelay ? "" : BuildDeviceFingerprint(userAgent, remoteAddress);
        var normalizedDeviceId = NormalizeDeviceId(deviceId);

        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.PanelPhoneSessions ??= new List<PanelPhoneSessionToken>();
            // Dedup = by deviceId OR by fingerprint. The client-provided
            // deviceId covers the relay path (no usable IP/UA ⇒ empty
            // fingerprint), while the fingerprint still covers LAN where IP/UA
            // are available. Either match drops the prior session for this
            // device so re-pairing replaces rather than accumulates.
            if (!string.IsNullOrEmpty(normalizedDeviceId))
            {
                s.Auth.PanelPhoneSessions.RemoveAll(session =>
                    string.Equals(session.DeviceId, normalizedDeviceId, StringComparison.Ordinal));
            }
            if (!string.IsNullOrEmpty(deviceFingerprint))
            {
                s.Auth.PanelPhoneSessions.RemoveAll(session =>
                    string.Equals(GetSessionFingerprint(session), deviceFingerprint, StringComparison.Ordinal));
            }
            s.Auth.PanelPhoneSessions.Insert(0, new PanelPhoneSessionToken
            {
                Id = CreateToken(9),
                Hash = hash,
                RelayKey = relayKey,
                Name = normalizedName,
                // Device class frozen at claim time. Name can later be renamed by
                // the user; DeviceType stays the original class so the session
                // list's type line survives a rename (and reflects the client's
                // detection, which beats the UA guess).
                DeviceType = normalizedName,
                UserAgent = userAgent,
                RemoteAddress = remoteAddress,
                DeviceFingerprint = deviceFingerprint,
                DeviceId = normalizedDeviceId,
                CreatedAt = now,
                LastSeenAt = now,
                ClaimedOverHttps = effectiveHttps,
            });
            NormalizeSessionList(s.Auth.PanelPhoneSessions, now);
        });

        return new ClaimResult(
            Ok: true,
            SessionToken: sessionToken,
            MachineName: NormalizeMachineName(MachineName),
            Spki: SpkiFingerprint,
            Error: "",
            NeedsApproval: false,
            RequestId: "",
            Sas: "",
            ExpiresAt: 0);
    }

    /// <summary>
    /// Pick a stored session display name: a non-blank explicit device name wins
    /// (the relay claim carries the phone's own label), otherwise fall back to
    /// the UA-derived descriptor the HTTP claim has always used. Trimmed + capped
    /// to match the QR/claim normalisation.
    /// </summary>
    private static string NormalizeSessionDisplayName(string? deviceName, string? userAgent)
    {
        var candidate = string.IsNullOrWhiteSpace(deviceName) ? DescribeDevice(userAgent ?? "") : deviceName.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = DescribeDevice(userAgent ?? "");
        return candidate.Length <= 64 ? candidate : candidate[..64];
    }

    public PanelPhoneServiceInfoResponse GetServiceInfo()
        => new() { MachineName = NormalizeMachineName(MachineName) };

    /// <summary>
    /// Device class + display label for one stored session. Persisted class wins
    /// (client-detected at claim); legacy sessions stored before DeviceType
    /// existed fall back to the UA descriptor. A custom name wins over the class
    /// unless it is the legacy generic "Phone remote" placeholder.
    /// </summary>
    private static (string DeviceType, string DisplayName) DescribeSession(PanelPhoneSessionToken s)
    {
        var deviceType = string.IsNullOrWhiteSpace(s.DeviceType)
            ? DescribeDevice(s.UserAgent)
            : s.DeviceType;
        var displayName = string.IsNullOrWhiteSpace(s.Name) ||
            (string.Equals(s.Name, "Phone remote", StringComparison.Ordinal) && deviceType != "Phone remote")
            ? deviceType
            : s.Name;
        return (deviceType, displayName);
    }

    /// <summary>Display label for a paired session, used to attribute phone→PC transfers. Empty when the id is unknown.</summary>
    public string GetSessionDisplayName(string id)
    {
        var s = _store.Load().Auth?.PanelPhoneSessions?.FirstOrDefault(t => t.Id == id);
        return s is null ? "" : DescribeSession(s).DisplayName;
    }

    public PanelPhoneSessionsResponse GetSessions(int connectedCount)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sessions = GetNormalizedSessionSnapshot(now);
        var items = sessions
            .OrderByDescending(GetSessionActivity)
            .Select(s =>
            {
                var lastSeen = GetSessionActivity(s);
                var (deviceType, displayName) = DescribeSession(s);
                return new PanelPhoneSessionDto
                {
                    Id = s.Id,
                    Name = displayName,
                    DeviceType = deviceType,
                    UserAgent = s.UserAgent,
                    RemoteAddress = s.RemoteAddress,
                    CreatedAt = s.CreatedAt,
                    LastSeenAt = lastSeen,
                    ExpiresAt = lastSeen > 0 ? lastSeen + SessionIdleMs : 0,
                    RecentlyActive = lastSeen > 0 && now - lastSeen <= RecentSessionWindowMs,
                    // Live transport for this session: "relay" if a relay-bridged
                    // hub client is up, "lan" for a direct /ws client, null if not
                    // currently connected.
                    ConnectedVia = _hub.GetConnectedTransport(s.Id ?? ""),
                };
            })
            .ToList();

        return new PanelPhoneSessionsResponse
        {
            ConnectedCount = connectedCount,
            AuthorizedCount = items.Count,
            SessionIdleMs = SessionIdleMs,
            Now = now,
            Sessions = items,
        };
    }

    /// <summary>
    /// A paired phone session with the bytes the relay client needs to bring
    /// up an end-to-end-encrypted host socket: the opaque <paramref name="Id"/>
    /// (passed into the hub so the killswitch can close the relayed session)
    /// and the decoded <paramref name="RelayRoot"/> (used to derive the rid and
    /// per-connection AEAD key).
    /// </summary>
    public readonly record struct ActiveRelaySession(string Id, byte[] RelayRoot);

    /// <summary>
    /// Enumerate live sessions that can be relayed: every non-expired session
    /// that carries a <see cref="PanelPhoneSessionToken.RelayKey"/> (sessions
    /// paired before the relay feature shipped have none and are skipped - they
    /// must re-pair). Returns each session's id plus the decoded relay root.
    /// The plaintext token is never involved; the relay never sees the root.
    /// </summary>
    public IReadOnlyList<ActiveRelaySession> GetActiveRelaySessions()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sessions = GetNormalizedSessionSnapshot(now);
        var result = new List<ActiveRelaySession>(sessions.Count);
        foreach (var session in sessions)
        {
            if (string.IsNullOrEmpty(session.Id) || string.IsNullOrEmpty(session.RelayKey))
                continue;
            byte[] relayRoot;
            try { relayRoot = Convert.FromBase64String(session.RelayKey); }
            catch (FormatException) { continue; }
            if (relayRoot.Length != Nexus.Service.Relay.RelayCrypto.RelayRootLength)
                continue;
            result.Add(new ActiveRelaySession(session.Id, relayRoot));
        }
        return result;
    }

    public async Task<bool> RevokeSessionAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var removed = false;
        _store.Update(s =>
        {
            var sessions = s.Auth?.PanelPhoneSessions;
            if (sessions is null)
                return;
            removed = sessions.RemoveAll(session =>
                string.Equals(session.Id, id, StringComparison.Ordinal)) > 0;
        });
        // The session is already gone from the store; kicking the live client is
        // best-effort cleanup. Never let a close failure (e.g. a relay-bridged
        // client whose transport already faulted) turn a successful revoke into
        // an HTTP 500 - the device list would show it removed yet the request
        // would report failure.
        if (removed)
        {
            try { await _hub.KickPhoneSessionsAsync(new[] { id }).ConfigureAwait(false); }
            catch { /* removal succeeded; kick is best-effort */ }
        }
        return removed;
    }

    public bool RenameSession(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return false;

        var nextName = name.Trim();
        if (nextName.Length > 40)
            nextName = nextName[..40];

        var updated = false;
        _store.Update(s =>
        {
            var sessions = s.Auth?.PanelPhoneSessions;
            if (sessions is null)
                return;

            foreach (var session in sessions)
            {
                if (!string.Equals(session.Id, id, StringComparison.Ordinal))
                    continue;

                session.Name = nextName;
                updated = true;
                break;
            }
        });
        return updated;
    }

    public async Task<int> RevokeAllSessionsAsync()
    {
        var removed = 0;
        _store.Update(s =>
        {
            var sessions = s.Auth?.PanelPhoneSessions;
            if (sessions is null)
                return;

            removed = sessions.Count;
            sessions.Clear();
        });
        if (removed > 0)
        {
            try { await _hub.KickAllPhoneAsync().ConfigureAwait(false); }
            catch { /* removal succeeded; kick is best-effort */ }
        }
        return removed;
    }

    /// <summary>
    /// Returns true if Pair Remote requests are accepted. When false, the
    /// auth middleware rejects any phone-session-authenticated request with
    /// 403 RemoteDisabled and the claim endpoint refuses new pairings.
    /// </summary>
    public bool GetRemoteControlEnabled()
    {
        var auth = _store.Load().Auth;
        return auth?.RemoteControlEnabled ?? true;
    }

    /// <summary>
    /// Persists the killswitch state. On a true -> false transition every
    /// active phone-session WebSocket is closed immediately so the user's
    /// "OFF means OFF" expectation holds without waiting for cookie expiry
    /// or the next HTTP request.
    /// </summary>
    public async Task SetRemoteControlEnabledAsync(bool enabled)
    {
        var changed = false;
        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            if (s.Auth.RemoteControlEnabled == enabled)
                return;
            s.Auth.RemoteControlEnabled = enabled;
            changed = true;
        });
        if (changed && !enabled)
            await _hub.KickAllPhoneAsync();
    }

    /// <summary>
    /// Returns whether the opt-in cloud-relay transport is enabled. The relay
    /// connection is only held when this AND <see cref="GetRemoteControlEnabled"/>
    /// are both true. Default false.
    /// </summary>
    public bool GetRelayEnabled()
    {
        return _store.Load().Auth?.RelayEnabled ?? false;
    }

    /// <summary>
    /// Persists the relay opt-in. The write goes through
    /// <see cref="IConfigStore.Update"/>, which fires
    /// <see cref="IConfigStore.OnChanged"/>; <c>RelayConnectionService</c>
    /// listens on that signal and opens / tears down its host sockets - no
    /// poll loop. Turning the relay off therefore closes every relayed session
    /// as a side effect of the service reconciling to the new state.
    /// </summary>
    public void SetRelayEnabled(bool enabled)
    {
        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            if (s.Auth.RelayEnabled == enabled)
                return;
            s.Auth.RelayEnabled = enabled;
        });
    }

    /// <summary>
    /// Returns the current Wi-Fi discoverability preference. Reading
    /// passes through the persisted document and collapses an expired
    /// "until <ts>" window to "never" so callers see a single source of
    /// truth without having to check the timestamp themselves. Does not
    /// mutate the persisted state.
    /// </summary>
    public (string Mode, long UntilUnixSeconds) GetPairBroadcast()
    {
        var raw = _store.Load().Auth?.PairBroadcast ?? new PairBroadcastSettings();
        if (raw.Mode == "until" && raw.UntilUnixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return ("never", 0);
        return (raw.Mode, raw.UntilUnixSeconds);
    }

    /// <summary>
    /// Persists a Wi-Fi discoverability preference. <paramref name="mode"/>
    /// must be "never", "always", or "until"; for "until" the caller passes
    /// a future Unix-seconds expiry (clamped to a maximum of 24h ahead so a
    /// stale write can't pin broadcast on forever).
    /// </summary>
    public void SetPairBroadcast(string mode, long untilUnixSeconds)
    {
        // Default unknown modes to "never" rather than "always": a misbehaving
        // client sending an unrecognised string should land in the safer state,
        // not silently enable LAN discoverability.
        var normalized = mode switch
        {
            "never" => ("never", 0L),
            "always" => ("always", 0L),
            "until" => ("until", Math.Min(
                untilUnixSeconds,
                DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds())),
            _ => ("never", 0L)
        };
        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.PairBroadcast ??= new PairBroadcastSettings();
            s.Auth.PairBroadcast.Mode = normalized.Item1;
            s.Auth.PairBroadcast.UntilUnixSeconds = normalized.Item2;
        });
    }

    public bool ValidateSessionToken(string? token)
        => TryValidateSessionToken(token, context: null, out _);

    public bool ValidateSessionToken(string? token, HttpContext? context)
        => TryValidateSessionToken(token, context, out _);

    /// <summary>
    /// Same as <see cref="ValidateSessionToken(string?, HttpContext?)"/> but
    /// returns the matched session id on success. The auth middleware uses
    /// this to stash the id on <see cref="HttpContext.Items"/> so the WS
    /// upgrade can tag the connection for the Pair Remote killswitch.
    /// </summary>
    public bool TryValidateSessionToken(string? token, HttpContext? context, out string sessionId)
    {
        sessionId = "";
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var hash = HashToken(token);
        var provided = Encoding.UTF8.GetBytes(hash);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sessions = _store.Load().Auth?.PanelPhoneSessions;
        if (sessions is null)
            return false;

        foreach (var session in sessions)
        {
            if (string.IsNullOrEmpty(session.Hash))
                continue;
            var expected = Encoding.UTF8.GetBytes(session.Hash);
            if (provided.Length == expected.Length &&
                CryptographicOperations.FixedTimeEquals(provided, expected))
            {
                var lastSeen = session.LastSeenAt > 0 ? session.LastSeenAt : session.CreatedAt;
                var idleLimit = session.ClaimedOverHttps ? SessionIdleMs : HttpSessionIdleMs;
                if (lastSeen <= 0 || now - lastSeen > idleLimit)
                {
                    PruneExpiredSessions(now);
                    return false;
                }

                // HTTP-claimed sessions get a hard IP+UA bind on every
                // request: a leaked cookie can't ride from a different
                // device. HTTPS-claimed sessions skip the check because
                // SPKI pinning already covers the threat and bind drift
                // (DHCP renewal, Wi-Fi roam, iOS UA bump) would 401 the
                // native app for no security gain.
                if (!session.ClaimedOverHttps && context is not null)
                {
                    var requestUserAgent = context.Request.Headers["User-Agent"].ToString();
                    var requestRemote = context.Connection.RemoteIpAddress?.ToString() ?? "";
                    var requestFingerprint = BuildDeviceFingerprint(requestUserAgent, requestRemote);
                    var sessionFingerprint = GetSessionFingerprint(session);
                    if (string.IsNullOrEmpty(requestFingerprint) ||
                        string.IsNullOrEmpty(sessionFingerprint) ||
                        !string.Equals(requestFingerprint, sessionFingerprint, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                if (now - lastSeen >= LastSeenRefreshMs)
                    TouchSession(hash, now);
                sessionId = session.Id ?? "";
                return true;
            }
        }

        return false;
    }

    /// <summary>Drop every lapsed pair token. Returns how many were removed so
    /// the caller can fire <see cref="OutstandingPairTokensChanged"/> after
    /// releasing <see cref="_lock"/> when the outstanding set actually shrank.</summary>
    private int PruneExpiredPairsLocked()
    {
        var now = DateTime.UtcNow;
        var expired = _pairTokens.Where(kvp => kvp.Value <= now).Select(kvp => kvp.Key).ToList();
        foreach (var token in expired)
            _pairTokens.Remove(token);
        return expired.Count;
    }

    private void TouchSession(string hash, long now)
    {
        _store.Update(s =>
        {
            var sessions = s.Auth?.PanelPhoneSessions;
            if (sessions is null)
                return;
            foreach (var session in sessions)
            {
                if (session.Hash != hash)
                    continue;
                session.LastSeenAt = now;
                break;
            }
        });
    }

    private void PruneExpiredSessions(long now)
    {
        _store.Update(s =>
        {
            var sessions = s.Auth?.PanelPhoneSessions;
            if (sessions is null)
                return;
            sessions.RemoveAll(session =>
            {
                var lastSeen = GetSessionActivity(session);
                return lastSeen <= 0 || now - lastSeen > SessionIdleMs;
            });
        });
    }

    private List<PanelPhoneSessionToken> GetNormalizedSessionSnapshot(long now)
    {
        var sessions = _store.Load().Auth?.PanelPhoneSessions;
        if (sessions is null)
            return new List<PanelPhoneSessionToken>();

        var needsUpdate = NeedsSessionNormalization(sessions, now);

        if (!needsUpdate)
            return sessions.Select(CloneSession).ToList();

        var snapshot = new List<PanelPhoneSessionToken>();
        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.PanelPhoneSessions ??= new List<PanelPhoneSessionToken>();
            NormalizeSessionList(s.Auth.PanelPhoneSessions, now);
            snapshot = s.Auth.PanelPhoneSessions.Select(CloneSession).ToList();
        });

        return snapshot;
    }

    private static bool ShouldPruneSession(PanelPhoneSessionToken session, long now)
    {
        var lastSeen = GetSessionActivity(session);
        return lastSeen <= 0 || now - lastSeen > SessionIdleMs;
    }

    private static bool NeedsSessionNormalization(List<PanelPhoneSessionToken> sessions, long now)
    {
        if (sessions.Count > MaxSessions)
            return true;

        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            var fingerprint = GetSessionFingerprint(session);
            if (ShouldPruneSession(session, now) ||
                string.IsNullOrWhiteSpace(session.Id) ||
                session.CreatedAt <= 0 ||
                session.LastSeenAt <= 0 ||
                string.IsNullOrWhiteSpace(session.Name) ||
                (string.IsNullOrWhiteSpace(session.DeviceFingerprint) && !string.IsNullOrEmpty(fingerprint)))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(fingerprint) && !fingerprints.Add(fingerprint))
                return true;
        }

        return false;
    }

    private static void NormalizeSessionList(List<PanelPhoneSessionToken> sessions, long now)
    {
        sessions.RemoveAll(session => ShouldPruneSession(session, now));
        foreach (var session in sessions)
        {
            if (string.IsNullOrWhiteSpace(session.Id))
                session.Id = CreateToken(9);
            if (session.CreatedAt <= 0)
                session.CreatedAt = GetSessionActivity(session);
            if (session.CreatedAt <= 0)
                session.CreatedAt = now;
            if (session.LastSeenAt <= 0)
                session.LastSeenAt = session.CreatedAt;
            if (string.IsNullOrWhiteSpace(session.Name))
                session.Name = DescribeDevice(session.UserAgent);
            if (string.IsNullOrWhiteSpace(session.DeviceType))
                session.DeviceType = DescribeDevice(session.UserAgent);
            if (string.IsNullOrWhiteSpace(session.DeviceFingerprint))
                session.DeviceFingerprint = BuildDeviceFingerprint(session.UserAgent, session.RemoteAddress);
        }

        sessions.Sort((a, b) => GetSessionActivity(b).CompareTo(GetSessionActivity(a)));

        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < sessions.Count;)
        {
            var fingerprint = GetSessionFingerprint(sessions[i]);
            if (!string.IsNullOrEmpty(fingerprint) && !fingerprints.Add(fingerprint))
            {
                sessions.RemoveAt(i);
                continue;
            }
            i++;
        }

        if (sessions.Count > MaxSessions)
            sessions.RemoveRange(MaxSessions, sessions.Count - MaxSessions);
    }

    private static long GetSessionActivity(PanelPhoneSessionToken session)
    {
        return session.LastSeenAt > 0 ? session.LastSeenAt : session.CreatedAt;
    }

    private static string GetSessionFingerprint(PanelPhoneSessionToken session)
    {
        return string.IsNullOrWhiteSpace(session.DeviceFingerprint)
            ? BuildDeviceFingerprint(session.UserAgent, session.RemoteAddress)
            : session.DeviceFingerprint;
    }

    private static PanelPhoneSessionToken CloneSession(PanelPhoneSessionToken session)
    {
        return new PanelPhoneSessionToken
        {
            Id = session.Id,
            Hash = session.Hash,
            RelayKey = session.RelayKey,
            Name = session.Name,
            DeviceType = session.DeviceType,
            UserAgent = session.UserAgent,
            RemoteAddress = session.RemoteAddress,
            DeviceFingerprint = session.DeviceFingerprint,
            DeviceId = session.DeviceId,
            CreatedAt = session.CreatedAt,
            LastSeenAt = session.LastSeenAt,
            ClaimedOverHttps = session.ClaimedOverHttps,
        };
    }

    private static string DescribeDevice(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return "Phone remote";

        if (userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase))
            return "iPad";
        if (userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase))
            return "iPhone";
        if (userAgent.Contains("Nexus/", StringComparison.OrdinalIgnoreCase) &&
            userAgent.Contains("CFNetwork", StringComparison.OrdinalIgnoreCase) &&
            userAgent.Contains("Darwin", StringComparison.OrdinalIgnoreCase))
        {
            return "iPhone";
        }
        if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            return userAgent.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
                ? "Android phone"
                : "Android tablet";
        }
        if (userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase))
            return "Mac";
        if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase))
            return "Windows PC";

        return "Phone remote";
    }

    private static string BuildDeviceFingerprint(string? userAgent, string? remoteAddress)
    {
        var normalizedUserAgent = NormalizeFingerprintPart(userAgent);
        var normalizedRemoteAddress = NormalizeRemoteAddress(remoteAddress);
        if (string.IsNullOrEmpty(normalizedUserAgent) && string.IsNullOrEmpty(normalizedRemoteAddress))
            return "";

        return HashValue($"panel-phone-v1|{normalizedRemoteAddress}|{normalizedUserAgent}");
    }

    private static string NormalizeRemoteAddress(string? remoteAddress)
    {
        var value = (remoteAddress ?? "").Trim();
        if (value.Length == 0)
            return "";

        if (IPAddress.TryParse(value, out var address))
        {
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            return address.ToString();
        }

        return NormalizeFingerprintPart(value);
    }

    private static string NormalizeFingerprintPart(string? value)
    {
        return string.Join(
            " ",
            (value ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Validate + normalize a client-provided device id for dedup. Trims
    /// surrounding whitespace; treats empty / whitespace-only as "no dedup id"
    /// (returns ""), and rejects (also returns "") absurdly long values rather
    /// than truncating - a truncated id could collide with a different device's
    /// id. Kept case-sensitive: the contract is an opaque persisted UUID, and
    /// the dedup compare is Ordinal.
    /// </summary>
    private static string NormalizeDeviceId(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxDeviceIdLength)
            return "";
        return trimmed;
    }

    private static string NormalizeMachineName(string? value)
    {
        var normalized = string.Join(
            " ",
            (value ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
            return "Nexus PC";
        return normalized.Length <= 64 ? normalized : normalized[..64];
    }

    private static string GetDefaultMachineName()
    {
        if (OperatingSystem.IsMacOS())
        {
            var computerName = ReadFirstOutputLine("/usr/sbin/scutil", "--get", "ComputerName");
            if (!string.IsNullOrWhiteSpace(computerName))
                return computerName;
        }

        if (!string.IsNullOrWhiteSpace(Environment.MachineName))
            return Environment.MachineName;

        try
        {
            var hostName = Dns.GetHostName();
            if (!string.IsNullOrWhiteSpace(hostName))
                return hostName;
        }
        catch
        {
            // Fall through to the product fallback below.
        }

        return "Nexus PC";
    }

    private static string? ReadFirstOutputLine(string fileName, params string[] arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = fileName;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);

            if (!process.Start())
                return null;

            if (!process.WaitForExit(750))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort cleanup for a one-time platform name probe.
                }
                return null;
            }

            return process.ExitCode == 0
                ? process.StandardOutput.ReadLine()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string CreateToken(int byteCount)
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static string HashToken(string token)
    {
        return HashValue(token);
    }

    /// <summary>
    /// Derive the per-session relay root from the transient plaintext token and
    /// encode it as standard (padded) base64 for storage in
    /// <see cref="PanelPhoneSessionToken.RelayKey"/>. Called only at claim time,
    /// while the plaintext token is still in hand; afterward the token is gone
    /// (hash-only) and this value is the sole way the relay client recovers the
    /// E2E key for the session.
    /// </summary>
    private static string ComputeRelayKey(string sessionToken)
    {
        var relayRoot = Nexus.Service.Relay.RelayCrypto.DeriveRelayRoot(sessionToken);
        return Convert.ToBase64String(relayRoot);
    }

    private static string HashValue(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToBase64String(hash)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    // -- Manual pair-code (BT-SSP-style Numeric Comparison) --------------

    /// <summary>
    /// Dashboard-side: mint a fresh 6-digit code, supersede any prior
    /// in-flight code (publishes a "cancelled" frame so an open dashboard
    /// shows the old code as expired). Returns a "remote-disabled" sentinel
    /// when the killswitch is off, so a code that would be rejected on
    /// submit is never minted.
    /// </summary>
    public PanelPhonePairCodeStartResponse StartPairCode()
    {
        if (!GetRemoteControlEnabled())
            return new PanelPhonePairCodeStartResponse { Code = "", TtlSeconds = 0 };
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expires = now + PairCodeTtlSeconds * 1000L;
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var nonce = RandomNumberGenerator.GetBytes(16);

        string? supersededRequestId = null;
        lock (_pairCodeLock)
        {
            if (_pairCode is { RequestId: { Length: > 0 } prior })
                supersededRequestId = prior;

            _pairCode = new PairCodeState
            {
                Code = code,
                Nonce = nonce,
                ExpiresAt = expires,
            };
        }

        if (supersededRequestId is not null)
        {
            PublishPairCodeFrame(new PanelPhonePairCodeRequestFrame
            {
                Kind = "cancelled",
                RequestId = supersededRequestId,
                Reason = "host-started-new-code",
            });
        }

        var lanHost = LocalNetwork.GetLocalIp();
        var lanPort = HttpsPort > 0 ? HttpsPort : ServicePort;
        return new PanelPhonePairCodeStartResponse
        {
            Host = lanHost,
            Port = lanPort,
            Code = code,
            TtlSeconds = PairCodeTtlSeconds,
            ExpiresAt = expires,
        };
    }

    /// <summary>
    /// Phone-side: submit the typed code. On match the server stashes a
    /// pending request keyed by a one-shot id and broadcasts the SAS to
    /// the dashboard so the user can compare. Rate-limited per remote IP
    /// (5 attempts / 5 min lockout). Single-use: while a request is in
    /// flight (RequestId already set), subsequent submits from a different
    /// remote are rejected so an attacker who guesses the code mid-flow
    /// can't steal the handshake.
    /// REQUIRES HTTPS: the SAS binds to the captured SPKI; over plain
    /// HTTP that binding is meaningless. Reject HTTP early.
    /// </summary>
    public PanelPhonePairCodeSubmitResponse SubmitPairCode(string code, HttpContext context)
    {
        if (!GetRemoteControlEnabled())
            return new PanelPhonePairCodeSubmitResponse { Error = "remote-disabled" };

        if (!context.Request.IsHttps)
            return new PanelPhonePairCodeSubmitResponse { Error = "https-required" };

        var remoteAddress = NormalizeRemoteAddress(context.Connection.RemoteIpAddress?.ToString());
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        lock (_pairCodeLock)
        {
            PruneAttemptsLocked(nowMs);

            if (TryGetLockoutLocked(remoteAddress, nowMs) is { } lockoutSecondsRemaining)
            {
                return new PanelPhonePairCodeSubmitResponse
                {
                    Error = "rate-limited",
                    RetryAfterSeconds = lockoutSecondsRemaining,
                };
            }

            if (_pairCode is not { } state || state.ExpiresAt <= nowMs)
            {
                if (_pairCode is { RequestId: { Length: > 0 } prior })
                {
                    PublishPairCodeFrame(new PanelPhonePairCodeRequestFrame
                    {
                        Kind = "cancelled",
                        RequestId = prior,
                        Reason = "expired",
                    });
                }
                _pairCode = null;
                return new PanelPhonePairCodeSubmitResponse { Error = "no-active-code" };
            }

            // Single-use lock: a successful submit set RequestId; further
            // submits from any IP (including a second guesser) must fail
            // until /start mints a fresh code. Without this, a race
            // window between first-submit and dual-confirm lets a second
            // submitter overwrite RequestId/Sas/PhoneRemoteAddress and
            // hijack the in-flight handshake.
            if (!string.IsNullOrEmpty(state.RequestId))
            {
                RegisterFailedAttemptLocked(remoteAddress, nowMs);
                return new PanelPhonePairCodeSubmitResponse { Error = "code-in-use" };
            }

            if (string.IsNullOrEmpty(code) || code.Length != 6)
            {
                RegisterFailedAttemptLocked(remoteAddress, nowMs);
                return new PanelPhonePairCodeSubmitResponse { Error = "invalid-code" };
            }

            var providedBytes = Encoding.UTF8.GetBytes(code);
            var expectedBytes = Encoding.UTF8.GetBytes(state.Code);
            if (providedBytes.Length != expectedBytes.Length ||
                !CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
            {
                RegisterFailedAttemptLocked(remoteAddress, nowMs);
                return new PanelPhonePairCodeSubmitResponse { Error = "invalid-code" };
            }

            // Code matched. Clear lockout counter for this IP so a single
            // typo on the next manual pair from the same device doesn't
            // accumulate against a prior session's attempts.
            _pairCodeAttempts.Remove(remoteAddress);

            var requestId = CreateToken(16);
            var sas = ComputeSas(state.Code, state.Nonce, SpkiFingerprint);
            var userAgent = context.Request.Headers["User-Agent"].ToString();
            var deviceLabel = DescribeDevice(userAgent);

            state.RequestId = requestId;
            state.Sas = sas;
            state.PhoneRemoteAddress = remoteAddress;
            state.PhoneUserAgent = userAgent;
            state.ClaimedOverHttps = context.Request.IsHttps;
            state.HostApproved = false;
            state.PhoneApproved = false;

            PublishPairCodeFrame(new PanelPhonePairCodeRequestFrame
            {
                Kind = "request",
                RequestId = requestId,
                Sas = sas,
                DeviceLabel = deviceLabel,
                RemoteAddress = remoteAddress,
                UserAgent = userAgent,
                ExpiresAt = state.ExpiresAt,
            });

            return new PanelPhonePairCodeSubmitResponse
            {
                Accepted = true,
                RequestId = requestId,
                Sas = sas,
                SpkiFingerprint = SpkiFingerprint,
                MachineName = NormalizeMachineName(MachineName),
                ExpiresAt = state.ExpiresAt,
            };
        }
    }

    /// <summary>
    /// Phone-side: Wi-Fi-discovered pair handshake. Same SAS-comparison
    /// model as <see cref="SubmitPairCode"/> but with no out-of-band code
    /// - the phone found us via Bonjour, the user taps the discovered
    /// device, and the OOB authentication is the user's physical Allow
    /// click on the desktop (matching Bluetooth-style numeric comparison).
    ///
    /// Rate-limited per remote IP (same lockout as the code flow) so a
    /// malicious LAN host can't pop the pair modal in a tight loop. If a
    /// pair request is already in flight, returns "in-use"; user must
    /// resolve the existing one first.
    /// REQUIRES HTTPS for the same reason as SubmitPairCode: the SAS
    /// binds to the captured SPKI; over plain HTTP that binding is
    /// meaningless.
    /// </summary>
    public PanelPhonePairCodeSubmitResponse InitiatePairWifi(string deviceName, HttpContext context)
    {
        if (!GetRemoteControlEnabled())
            return new PanelPhonePairCodeSubmitResponse { Error = "remote-disabled" };

        if (!context.Request.IsHttps)
            return new PanelPhonePairCodeSubmitResponse { Error = "https-required" };

        var remoteAddress = NormalizeRemoteAddress(context.Connection.RemoteIpAddress?.ToString());
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expires = nowMs + PairCodeTtlSeconds * 1000L;

        lock (_pairCodeLock)
        {
            PruneAttemptsLocked(nowMs);

            if (TryGetLockoutLocked(remoteAddress, nowMs) is { } lockoutSecondsRemaining)
            {
                return new PanelPhonePairCodeSubmitResponse
                {
                    Error = "rate-limited",
                    RetryAfterSeconds = lockoutSecondsRemaining,
                };
            }

            // Single-flight: if a code-mode or wifi-mode request is in
            // flight, refuse the new initiate. Resolves the same race
            // SubmitPairCode guards against (in-flight handshake hijack).
            // Also count this as a failed attempt so a LAN scanner can't
            // spam /pair-wifi/initiate to probe whether a pair handshake
            // is in progress without hitting the rate-limit threshold.
            if (_pairCode is { ExpiresAt: var ttl } && ttl > nowMs && !string.IsNullOrEmpty(_pairCode.RequestId))
            {
                RegisterFailedAttemptLocked(remoteAddress, nowMs);
                return new PanelPhonePairCodeSubmitResponse { Error = "code-in-use" };
            }

            // Clear any leftover expired state.
            if (_pairCode is { ExpiresAt: var oldTtl } && oldTtl <= nowMs)
            {
                _pairCode = null;
            }

            // Mint a fresh SAS bound to the captured SPKI. The "code" in
            // PairCodeState is empty for wifi-initiated requests; ComputeSas
            // still derives a stable 6-digit SAS from (code || nonce || spki),
            // which the dashboard renders for the user to compare against
            // the value displayed on the phone.
            var nonce = RandomNumberGenerator.GetBytes(16);
            var requestId = CreateToken(16);
            var sas = ComputeSas(string.Empty, nonce, SpkiFingerprint);
            var userAgent = context.Request.Headers["User-Agent"].ToString();
            var deviceLabel = string.IsNullOrWhiteSpace(deviceName) ? DescribeDevice(userAgent) : deviceName.Trim();
            if (deviceLabel.Length > 64) deviceLabel = deviceLabel[..64];

            _pairCode = new PairCodeState
            {
                Code = string.Empty,
                Nonce = nonce,
                ExpiresAt = expires,
                RequestId = requestId,
                Sas = sas,
                PhoneRemoteAddress = remoteAddress,
                PhoneUserAgent = userAgent,
                ClaimedOverHttps = context.Request.IsHttps,
            };

            PublishPairCodeFrame(new PanelPhonePairCodeRequestFrame
            {
                Kind = "request",
                RequestId = requestId,
                Sas = sas,
                DeviceLabel = deviceLabel,
                RemoteAddress = remoteAddress,
                UserAgent = userAgent,
                ExpiresAt = expires,
            });

            return new PanelPhonePairCodeSubmitResponse
            {
                Accepted = true,
                RequestId = requestId,
                Sas = sas,
                SpkiFingerprint = SpkiFingerprint,
                MachineName = NormalizeMachineName(MachineName),
                ExpiresAt = expires,
            };
        }
    }

    /// <summary>
    /// Phone-side poll. The phone calls this repeatedly while the user is
    /// waiting for the host to click Allow on the dashboard. When the host
    /// has approved, the response carries the freshly-minted session token
    /// and the canonical SPKI fingerprint (so the phone can verify what it
    /// captured during the TLS handshake matches what the server says it
    /// presented). Pass <paramref name="approved"/> false to cancel a
    /// waiting request (the user closed the sheet).
    /// Requires HTTPS - the session token issued here is treated as
    /// ClaimedOverHttps so the long 30-day idle applies, and that's only
    /// safe if the channel was actually pinned.
    /// </summary>
    public PanelPhonePairCodeConfirmResponse ConfirmPairCode(string requestId, bool approved, HttpContext context)
    {
        if (!context.Request.IsHttps)
            return new PanelPhonePairCodeConfirmResponse { Status = "https-required" };

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        PairCodeState? sessionToIssue = null;

        // Outcome decided inside the lock; any disk-I/O (token issuance via
        // _store.Update) happens after we drop the lock to match the
        // discipline of the QR-side Claim() flow.
        PanelPhonePairCodeConfirmResponse result;
        lock (_pairCodeLock)
        {
            if (_pairCode is not { } state || !string.Equals(state.RequestId, requestId, StringComparison.Ordinal))
                return new PanelPhonePairCodeConfirmResponse { Status = "unknown" };

            // Only the device that submitted the code may confirm or cancel it.
            // The request id also rides the dashboard's WebSocket frame, so on
            // its own it does not prove the caller is that phone.
            // A relayed request has no remote address; pairing a new device is a
            // LAN flow, so an empty address fails closed instead of matching another
            // tunneled session's empty address.
            var confirmRemote = NormalizeRemoteAddress(context.Connection.RemoteIpAddress?.ToString());
            if (confirmRemote.Length == 0 || !string.Equals(state.PhoneRemoteAddress, confirmRemote, StringComparison.Ordinal))
                return new PanelPhonePairCodeConfirmResponse { Status = "unknown" };

            if (state.ExpiresAt <= nowMs)
            {
                _pairCode = null;
                PublishPairCodeFrame(new PanelPhonePairCodeRequestFrame
                {
                    Kind = "cancelled",
                    RequestId = requestId,
                    Reason = "expired",
                });
                return new PanelPhonePairCodeConfirmResponse { Status = "expired" };
            }

            // Host already denied (state retained so the phone learns the
            // canonical "denied" instead of "unknown"). Drop the state on
            // the phone's next touch and report the host's decision.
            if (state.HostDenied)
            {
                _pairCode = null;
                return new PanelPhonePairCodeConfirmResponse { Status = "denied" };
            }

            // Phone user-cancel (closed the sheet or hit Cancel).
            if (!approved)
            {
                _pairCode = null;
                PublishPairCodeFrame(new PanelPhonePairCodeRequestFrame
                {
                    Kind = "cancelled",
                    RequestId = requestId,
                    Reason = "phone-denied",
                });
                return new PanelPhonePairCodeConfirmResponse { Status = "denied" };
            }

            // The phone's presence on this endpoint IS the confirmation -
            // the user typed the code, sees the SAS, and is alive on the
            // device. Only the host's Allow gates token issuance; this
            // matches the user-visible model "compare SAS, then click
            // Allow on the system."
            if (!state.HostApproved)
                return new PanelPhonePairCodeConfirmResponse { Status = "waiting-host" };

            sessionToIssue = state;
            _pairCode = null;
            result = new PanelPhonePairCodeConfirmResponse { Status = "approved" };
        }

        if (sessionToIssue is null)
            return result;

        var (token, machineName) = IssuePairCodeSession(sessionToIssue, nowMs);
        result.Token = token;
        result.MachineName = machineName;
        result.SpkiFingerprint = SpkiFingerprint;
        return result;
    }

    /// <summary>
    /// Dashboard-side approval. If the phone has already confirmed, this
    /// triggers the token issuance on the phone's next poll. The dashboard
    /// receives the SAS via the websocket frame; the session it's authed
    /// against is the desktop session, so the dashboard already proved
    /// physical presence at the PC.
    /// </summary>
    public PanelPhonePairCodeHostDecisionResponse HostDecisionPairCode(string requestId, bool approved)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_pairCodeLock)
        {
            if (_pairCode is not { } state || !string.Equals(state.RequestId, requestId, StringComparison.Ordinal))
                return new PanelPhonePairCodeHostDecisionResponse { Status = "unknown" };

            if (state.ExpiresAt <= nowMs)
            {
                _pairCode = null;
                PublishPairCodeFrame(new PanelPhonePairCodeRequestFrame
                {
                    Kind = "cancelled",
                    RequestId = requestId,
                    Reason = "expired",
                });
                return new PanelPhonePairCodeHostDecisionResponse { Status = "expired" };
            }

            if (!approved)
            {
                // Keep state alive so the phone's next /confirm poll returns
                // the canonical "denied" instead of "unknown". TTL still
                // reaps it; ConfirmPairCode also clears it on the phone-side
                // touch.
                state.HostDenied = true;
                PublishPairCodeFrame(new PanelPhonePairCodeRequestFrame
                {
                    Kind = "cancelled",
                    RequestId = requestId,
                    Reason = "host-denied",
                });
                return new PanelPhonePairCodeHostDecisionResponse { Status = "denied" };
            }

            state.HostApproved = true;
            // Token issuance happens on the phone's next /confirm poll so
            // the cookie/token lives in that response - keeps the host
            // endpoint side-effect-free for the network stack. The phone
            // is already polling at 1 Hz, so the token arrives within a
            // second of the host pressing Allow.
            return new PanelPhonePairCodeHostDecisionResponse { Status = "approved" };
        }
    }

    private (string Token, string MachineName) IssuePairCodeSession(PairCodeState state, long nowMs)
    {
        var sessionToken = CreateToken(32);
        var hash = HashToken(sessionToken);
        var relayKey = ComputeRelayKey(sessionToken);
        var userAgent = state.PhoneUserAgent;
        var remoteAddress = state.PhoneRemoteAddress;
        var deviceFingerprint = BuildDeviceFingerprint(userAgent, remoteAddress);
        var claimedOverHttps = state.ClaimedOverHttps;
        var deviceId = state.DeviceId;

        _store.Update(s =>
        {
            s.Auth ??= new AuthSettings();
            s.Auth.PanelPhoneSessions ??= new List<PanelPhoneSessionToken>();
            if (!string.IsNullOrEmpty(deviceId))
            {
                s.Auth.PanelPhoneSessions.RemoveAll(session =>
                    string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal));
            }
            if (!string.IsNullOrEmpty(deviceFingerprint))
            {
                s.Auth.PanelPhoneSessions.RemoveAll(session =>
                    string.Equals(GetSessionFingerprint(session), deviceFingerprint, StringComparison.Ordinal));
            }
            s.Auth.PanelPhoneSessions.Insert(0, new PanelPhoneSessionToken
            {
                Id = CreateToken(9),
                Hash = hash,
                RelayKey = relayKey,
                Name = DescribeDevice(userAgent),
                // Pair-code path carries no client-detected label, so the class
                // is the UA descriptor (matches the ClaimCore insert pattern
                // rather than relying on the NormalizeSessionList backfill).
                DeviceType = DescribeDevice(userAgent),
                UserAgent = userAgent,
                RemoteAddress = remoteAddress,
                DeviceFingerprint = deviceFingerprint,
                DeviceId = deviceId,
                CreatedAt = nowMs,
                LastSeenAt = nowMs,
                ClaimedOverHttps = claimedOverHttps,
            });
            NormalizeSessionList(s.Auth.PanelPhoneSessions, nowMs);
        });

        return (sessionToken, NormalizeMachineName(MachineName));
    }

    /// <summary>
    /// HKDF-SHA256(ikm = code, salt = nonce, info = "nexus-pair-sas-v1|" + spki)
    /// truncated to a 6-digit SAS via RFC-4226-style dynamic truncation
    /// (mask the high bit before mod 1e6 to remove the sign-bias). The
    /// security property is "binds the displayed digits to the SPKI the
    /// phone's TLS handshake actually saw" - an active MITM, who terminates
    /// TLS with a different cert, computes a different SAS, and the
    /// human-eyeballing-the-two-screens step catches the mismatch.
    /// Internal so tests can pin all three inputs and verify SPKI flip
    /// produces a different SAS.
    /// </summary>
    internal static string ComputeSas(string code, byte[] nonce, string spkiFingerprint)
    {
        var ikm = Encoding.UTF8.GetBytes(code);
        var spkiBytes = string.IsNullOrEmpty(spkiFingerprint)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(spkiFingerprint);
        var info = new byte[SasInfoPrefix.Length + spkiBytes.Length];
        Buffer.BlockCopy(SasInfoPrefix, 0, info, 0, SasInfoPrefix.Length);
        if (spkiBytes.Length > 0)
            Buffer.BlockCopy(spkiBytes, 0, info, SasInfoPrefix.Length, spkiBytes.Length);

        Span<byte> output = stackalloc byte[4];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output, nonce, info);
        var truncated = BinaryPrimitives.ReadUInt32BigEndian(output) & 0x7FFFFFFF;
        return (truncated % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private int? TryGetLockoutLocked(string remoteAddress, long nowMs)
    {
        if (!_pairCodeAttempts.TryGetValue(remoteAddress, out var attempt))
            return null;
        if (attempt.LockoutUntilMs <= 0)
            return null; // attempts accumulating but no lockout yet
        if (attempt.LockoutUntilMs <= nowMs)
        {
            _pairCodeAttempts.Remove(remoteAddress);
            return null;
        }
        var remainingMs = attempt.LockoutUntilMs - nowMs;
        return (int)Math.Max(1, (remainingMs + 999) / 1000);
    }

    private void RegisterFailedAttemptLocked(string remoteAddress, long nowMs)
    {
        if (_pairCodeAttempts.TryGetValue(remoteAddress, out var attempt))
        {
            attempt.Count += 1;
            attempt.LastAttemptMs = nowMs;
        }
        else
        {
            attempt = new PairCodeAttempt { Count = 1, LastAttemptMs = nowMs };
            _pairCodeAttempts[remoteAddress] = attempt;
        }

        if (attempt.Count >= PairCodeMaxAttempts)
            attempt.LockoutUntilMs = nowMs + PairCodeLockoutMs;
    }

    private void PruneAttemptsLocked(long nowMs)
    {
        if (_pairCodeAttempts.Count == 0)
            return;
        var stale = _pairCodeAttempts
            .Where(kvp =>
                // Expired lockouts.
                (kvp.Value.LockoutUntilMs > 0 && kvp.Value.LockoutUntilMs <= nowMs)
                // Accumulating attempts that never crossed the lockout
                // threshold but have gone quiet for at least one lockout
                // window. Without this the dictionary grows for the life
                // of the process under sustained scan-then-give-up probing.
                || (kvp.Value.LockoutUntilMs <= 0 && nowMs - kvp.Value.LastAttemptMs > PairCodeLockoutMs))
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in stale)
            _pairCodeAttempts.Remove(key);
    }

    private sealed class PairCodeState
    {
        public string Code { get; set; } = "";
        public byte[] Nonce { get; set; } = Array.Empty<byte>();
        public long ExpiresAt { get; set; }
        public string RequestId { get; set; } = "";
        public string Sas { get; set; } = "";
        public string PhoneRemoteAddress { get; set; } = "";
        public string PhoneUserAgent { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public bool ClaimedOverHttps { get; set; }
        public bool PhoneApproved { get; set; }
        public bool HostApproved { get; set; }
        public bool HostDenied { get; set; }
    }

    private sealed class PairCodeAttempt
    {
        public int Count { get; set; }
        public long LastAttemptMs { get; set; }
        public long LockoutUntilMs { get; set; }
    }
}
