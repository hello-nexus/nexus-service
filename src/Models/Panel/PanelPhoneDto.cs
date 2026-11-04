namespace Nexus.Service.Models.Panel;

public sealed class PanelPhonePairQrResponse
{
    public string Url { get; set; } = "";
    public string QrDataUrl { get; set; } = "";
    public string MachineName { get; set; } = "";
    public int TtlSeconds { get; set; }
    public long ExpiresAt { get; set; }
    /// <summary>
    /// Plain-HTTP port for the browser fallback. The QR's `port` field carries
    /// the HTTPS port for the native iOS app (which pins SPKI); browsers can't
    /// trust the self-signed LAN cert, so the web path lands here instead.
    /// </summary>
    public int HttpPort { get; set; }
}

public sealed class PanelPhoneClaimBody
{
    public string PairToken { get; set; } = "";

    /// <summary>
    /// Stable client-persisted device id (a UUID). Used to dedup authorized
    /// sessions so re-pairing the same device replaces its prior session. May
    /// also be supplied as a <c>?deviceId=</c> query param; the body value wins.
    /// Empty / missing falls back to fingerprint-based dedup (legacy behavior).
    /// </summary>
    public string DeviceId { get; set; } = "";

    /// <summary>
    /// Client-detected device class (e.g. "iPad", "Android tablet"). The browser
    /// resolves cases the server-side User-Agent can't: an iPad reports a desktop
    /// Mac UA, a Samsung tablet reports a phone UA. Non-blank wins over the
    /// UA-derived descriptor; blank/missing falls back to it.
    /// </summary>
    public string DeviceName { get; set; } = "";

    /// <summary>
    /// When true and the device is not already paired, the claim returns
    /// NeedsApproval=true with SAS fields instead of a token; the phone
    /// then polls /pair-code/confirm for host approval.
    /// False (or absent) keeps the legacy direct-mint behavior.
    /// </summary>
    public bool SupportsSasApproval { get; set; }
}

public sealed class PanelPhoneClaimResponse
{
    public bool Paired { get; set; }
    public string Token { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string Error { get; set; } = "";
    /// <summary>True when the claim is gated on host approval via the SAS flow.</summary>
    public bool NeedsApproval { get; set; }
    /// <summary>Pending request id; present when NeedsApproval is true.</summary>
    public string? RequestId { get; set; }
    /// <summary>6-digit SAS for the user to compare on both screens; present when NeedsApproval is true.</summary>
    public string? Sas { get; set; }
    /// <summary>SPKI fingerprint the SAS is bound to; present when NeedsApproval is true.</summary>
    public string? SpkiFingerprint { get; set; }
    /// <summary>Unix-ms expiry of the pending request; present when NeedsApproval is true.</summary>
    public long ExpiresAt { get; set; }
}

public sealed class PanelPhoneServiceInfoResponse
{
    public string MachineName { get; set; } = "";
}

public sealed class PanelPhoneSessionNameBody
{
    public string Name { get; set; } = "";
}

public sealed class PanelHostNameBody
{
    /// <summary>Empty / whitespace clears the override and falls back to Environment.MachineName on the next read.</summary>
    public string Name { get; set; } = "";
}

public sealed class PanelHostNameResponse
{
    public string MachineName { get; set; } = "";
}

public sealed class PanelPhoneSessionsResponse
{
    public int ConnectedCount { get; set; }
    public int AuthorizedCount { get; set; }
    public long SessionIdleMs { get; set; }
    public long Now { get; set; }
    public List<PanelPhoneSessionDto> Sessions { get; set; } = new();
}

public sealed class PanelPhoneSessionDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string DeviceType { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string RemoteAddress { get; set; } = "";
    public long CreatedAt { get; set; }
    public long LastSeenAt { get; set; }
    public long ExpiresAt { get; set; }
    public bool RecentlyActive { get; set; }
    /// <summary>
    /// How this session is connected to the PC right now: <c>"relay"</c> when a
    /// live hub client for it is bridged through the cloud relay, <c>"lan"</c>
    /// when connected via a direct LAN WebSocket, or <c>null</c> when the session
    /// is authorized but not currently connected.
    /// </summary>
    public string? ConnectedVia { get; set; }
}

public sealed class PanelStatusResponse
{
    public string Msg { get; set; } = "";
    public bool KioskRunning { get; set; }
    public bool PhoneConnected { get; set; }
    public int PhoneSubscribers { get; set; }
}

public sealed class RemoteControlStateResponse
{
    public bool Enabled { get; set; }
}

public sealed class RemoteControlToggleRequest
{
    public bool Enabled { get; set; }
}

/// <summary>Cloud-relay transport opt-in state (GET /panel/phone/relay).</summary>
public sealed class RelayStateResponse
{
    public bool Enabled { get; set; }
}

/// <summary>Cloud-relay transport opt-in toggle (POST /panel/phone/relay).</summary>
public sealed class RelayToggleRequest
{
    public bool Enabled { get; set; }
}

/// <summary>
/// First message the host sends on a relay socket:
/// <c>{"v":1,"role":"host","rid":"&lt;rid&gt;"}</c>. The relay maps the rid to a
/// rendezvous slot and forwards opaque binary frames to whichever client
/// presents the same rid. The relay never sees the key the rid is derived from.
/// </summary>
public sealed class RelayHostHello
{
    public int V { get; set; } = 1;
    public string Role { get; set; } = "host";
    public string Rid { get; set; } = "";

    // Optional device self-report for operator observability. Null ⇒ omitted
    // (WhenWritingNull), so the wire is unchanged for anything that leaves these
    // unset; an old relay ignores them, a new relay sanitizes them.
    public string? Os { get; set; }
    public string? OsVer { get; set; }
    public string? App { get; set; }
    public string? Dev { get; set; }

    /// <summary>Signed build credential; null is omitted, which the relay rejects once enforcement is on.</summary>
    public string? Cred { get; set; }
}

/// <summary>
/// Wire message-type discriminators for the claim-over-relay handshake. A
/// brand-new phone with no LAN reachability connects to the pair rendezvous
/// (rid_pair), then exchanges exactly these two sealed BINARY frames with the
/// PC. Kept as named constants so neither end sprinkles the raw literal.
/// </summary>
public static class RelayClaimMessageTypes
{
    /// <summary>phone→PC sealed request: <c>{"type":"claim","deviceName":"…","deviceId":"…"}</c>.</summary>
    public const string Claim = "claim";
    /// <summary>PC→phone sealed success: carries the freshly-minted session token.</summary>
    public const string ClaimOk = "claim-ok";
    /// <summary>PC→phone sealed failure: <c>{"type":"claim-err","error":"…"}</c>.</summary>
    public const string ClaimErr = "claim-err";
    /// <summary>PC→phone sealed: the claim awaits the host's Allow; carries <c>requestId</c>, <c>sas</c>, <c>expiresAt</c>.</summary>
    public const string ClaimPending = "claim-pending";
    /// <summary>phone→PC sealed poll (or cancel) for a pending claim: <c>{"type":"claim-confirm","requestId":"…","approved":true}</c>.</summary>
    public const string ClaimConfirm = "claim-confirm";
    /// <summary>PC→phone sealed reply to a poll while the host has not decided yet.</summary>
    public const string ClaimWaiting = "claim-waiting";
}

/// <summary>
/// phone→PC sealed claim request, sent over the pair rendezvous (rid_pair) once
/// the AEAD channel is up. Possession of the QR pair token is proven by the
/// successful AEAD decrypt - the rid_pair already pins which token the PC is
/// claiming, so the token itself is never put on the wire.
/// </summary>
public sealed class RelayClaimRequest
{
    public string Type { get; set; } = RelayClaimMessageTypes.Claim;
    public string DeviceName { get; set; } = "";

    /// <summary>
    /// Stable client-persisted device id (a UUID). When present, the PC dedups
    /// authorized sessions on it so re-pairing the same phone over the relay
    /// replaces its prior session instead of accumulating duplicates - the relay
    /// obscures the client IP/UA, so fingerprint-based dedup can't see it. Empty
    /// / missing falls back to no relay-side dedup (legacy behavior).
    /// </summary>
    public string DeviceId { get; set; } = "";

    /// <summary>
    /// True when the app can show the SAS and poll claim-confirm. A claim without
    /// it is answered claim-err update_required
    /// and never raises a prompt on the PC.
    /// </summary>
    public bool SupportsSasApproval { get; set; }

    /// <summary>claim-confirm only: the pending request being polled.</summary>
    public string RequestId { get; set; } = "";

    /// <summary>claim-confirm only: true polls for the decision, false cancels the request.</summary>
    public bool Approved { get; set; } = true;
}

/// <summary>
/// PC→phone sealed claim reply. On success <see cref="Type"/> is
/// <c>claim-ok</c> with the new <see cref="SessionToken"/> (the phone then
/// derives the SESSION relayRoot/rid from it and opens a runtime relay channel),
/// <see cref="MachineName"/>, and the PC's leaf <see cref="Spki"/> fingerprint.
/// On failure <see cref="Type"/> is <c>claim-err</c> and <see cref="Error"/>
/// carries the reason (mirrors the HTTP claim's error sentinels).
/// </summary>
public sealed class RelayClaimResponse
{
    public string Type { get; set; } = RelayClaimMessageTypes.ClaimOk;
    public string SessionToken { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string Spki { get; set; } = "";
    public string Error { get; set; } = "";
    /// <summary>claim-pending only: id the phone echoes on every claim-confirm poll.</summary>
    public string RequestId { get; set; } = "";
    /// <summary>claim-pending only: 6-digit code the phone shows for comparison with the PC.</summary>
    public string Sas { get; set; } = "";
    /// <summary>claim-pending only: Unix-ms expiry of the request.</summary>
    public long ExpiresAt { get; set; }
}

/// <summary>
/// One REST-over-relay tunnel request, sent client→PC (dir=2) as a sealed BINARY
/// frame on the session's <c>rid_http</c> rendezvous. The off-LAN panel's normal
/// fetch() calls (device list, layout, controls) are serialized into these so
/// they reach the PC's own HTTP handlers through the relay. <see cref="Id"/>
/// multiplexes concurrent in-flight requests on the one channel; the PC echoes
/// it back on the matching <see cref="RelayHttpResponse"/>.
/// </summary>
public sealed class RelayHttpRequest
{
    /// <summary>Caller-assigned correlation id; echoed on the response.</summary>
    public int Id { get; set; }
    /// <summary>HTTP method (GET/POST/PUT/DELETE/PATCH).</summary>
    public string Method { get; set; } = "GET";
    /// <summary>Request path + optional query (e.g. <c>/panel/status</c>); must clear the allowlist.</summary>
    public string Path { get; set; } = "";
    /// <summary>UTF-8 request body, or null for bodyless methods.</summary>
    public string? Body { get; set; }
    /// <summary>Content-Type for <see cref="Body"/>, or null.</summary>
    public string? ContentType { get; set; }
}

/// <summary>
/// The PC's sealed reply to a <see cref="RelayHttpRequest"/>, sent PC→client
/// (dir=1). <see cref="Id"/> matches the request so the panel resolves the right
/// pending fetch. <see cref="Status"/> is the real HTTP status the in-process
/// dispatch produced (or 403 for an off-allowlist path / 413 for an oversized
/// body). <see cref="Body"/> carries the response bytes base64-encoded when
/// <see cref="Base64"/> is true (binary-safe - thumbnails, icons), else the raw
/// UTF-8 text (error frames). The tunnel is a UTF-8 JSON string channel, so
/// binary MUST be base64 or its bytes corrupt on the round-trip.
/// </summary>
public sealed class RelayHttpResponse
{
    public int Id { get; set; }
    public int Status { get; set; }
    public string Body { get; set; } = "";
    public string? ContentType { get; set; }
    /// <summary>True when <see cref="Body"/> is base64-encoded bytes (vs raw UTF-8 text).</summary>
    public bool Base64 { get; set; }
}

/// <summary>Wi-Fi discoverability (mDNS) preference; AirDrop-style three-state.</summary>
public sealed class PairBroadcastStateResponse
{
    /// <summary>"never" | "always" | "until"</summary>
    public string Mode { get; set; } = "always";
    /// <summary>Unix-seconds expiry when Mode == "until"; 0 otherwise.</summary>
    public long UntilUnixSeconds { get; set; }
}

public sealed class PairBroadcastSetRequest
{
    public string Mode { get; set; } = "always";
    public long UntilUnixSeconds { get; set; }
}

/// <summary>
/// iOS-side Wi-Fi pair initiate. Sent the moment a discovered Nexus service is
/// tapped: server runs the same SAS-comparison handshake as /pair-code/submit
/// but without an out-of-band 6-digit code (the user's Allow click on the
/// desktop is the OOB). Same response shape as the code-submit path so the
/// phone-side state machine can be shared between the two flows.
/// </summary>
public sealed class PairWifiInitiateRequest
{
    /// <summary>Human-readable phone label, e.g. "Nicola's iPhone". Optional, trimmed to 64 chars on the wire.</summary>
    public string DeviceName { get; set; } = "";
}

// Manual pair-code flow (BT-SSP-style numeric comparison). Additive to the
// QR flow for camera-less devices. The phone POSTs the typed code, server
// returns a SAS bound to the leaf SPKI; user visually compares SAS on both
// screens and dual-approves before a session token is issued.

public sealed class PanelPhonePairCodeStartResponse
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Code { get; set; } = "";
    public int TtlSeconds { get; set; }
    public long ExpiresAt { get; set; }
}

public sealed class PanelPhonePairCodeSubmitBody
{
    public string Code { get; set; } = "";
}

public sealed class PanelPhonePairCodeSubmitResponse
{
    public bool Accepted { get; set; }
    public string RequestId { get; set; } = "";
    public string Sas { get; set; } = "";
    public string SpkiFingerprint { get; set; } = "";
    public string MachineName { get; set; } = "";
    public long ExpiresAt { get; set; }
    public string Error { get; set; } = "";
    public int RetryAfterSeconds { get; set; }
}

public sealed class PanelPhonePairCodeConfirmBody
{
    public string RequestId { get; set; } = "";
    public bool Approved { get; set; }
}

public sealed class PanelPhonePairCodeConfirmResponse
{
    /// <summary>One of: waiting-host, approved, denied, expired, unknown.</summary>
    public string Status { get; set; } = "";
    public string Token { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string SpkiFingerprint { get; set; } = "";
}

public sealed class PanelPhonePairCodeHostDecisionBody
{
    public string RequestId { get; set; } = "";
    public bool Approved { get; set; }
}

public sealed class PanelPhonePairCodeHostDecisionResponse
{
    public string Status { get; set; } = "";
}

/// <summary>
/// Multiplex frame for the dashboard. <c>Kind</c> = "request" means a phone
/// just submitted the active code and is awaiting host confirmation.
/// "cancelled" carries a <c>Reason</c> (expired, phone-denied, host-denied,
/// host-started-new-code) so the dashboard can clear its prompt.
/// </summary>
public sealed class PanelPhonePairCodeRequestFrame
{
    public string Kind { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string Sas { get; set; } = "";
    public string DeviceLabel { get; set; } = "";
    public string RemoteAddress { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public long ExpiresAt { get; set; }
    public string Reason { get; set; } = "";
}
