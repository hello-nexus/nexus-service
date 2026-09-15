# Network Transport and Polling Inventory

Current as of 2026-07-16. This document inventories the network traffic that
the app sends today between the desktop dashboard, the panel surfaces, and the
local service, focused on transport semantics: cadence, WebSocket topics,
multiplex behavior, snapshot-on-subscribe, and auth/reconnect. The exhaustive
REST route list (every path, method, request/response shape) lives in the
generated `docs/openapi.json`; this document does not try to enumerate every
route.

## Scope

Terms used below:

- `desktop` means the main React dashboard at `/`.
- `panel` means the kiosk/remote React surfaces at `/panel/{deviceId}`. The
  shorter forms `/panel`, `/touch`, and `/panel/phone` redirect into the
  per-device URL after allocating or recovering a deviceId from the device's
  own cookie + localStorage cache.
- `service` means `nexus-service`, listening on HTTP port `9400` by default
  and optional local HTTPS port `9443` when the local certificate is available.
- `sidecar` means service-owned local helper processes, such as the OpenRGB
  SDK server on `127.0.0.1:6742`.

There is no direct desktop-to-panel network channel. Desktop and panel traffic
both terminate at the service. Shared state moves through service REST routes,
the service multiplex WebSocket, persisted preferences, and local browser
storage/BroadcastChannel.

## Maintenance

Keep this inventory current when features add, remove, or change a WebSocket
topic, polling cadence, upload/download path, external network call, or
desktop/panel/phone transport behavior. Update the relevant counts and audit
hotspots in the same change that changes the traffic.

The exhaustive REST route inventory (every path, method, request/response
schema) is generated into `docs/openapi.json` and is not duplicated here.
When a change adds, removes, or renames a REST route, regenerate that file
instead of adding a row to this document; only touch this document when the
route also changes a polling cadence, introduces or removes a WebSocket
topic, or changes multiplex/auth/reconnect behavior.

## Route Families Added Since 2026-05

These route families landed after this document's prior pass. Their full
endpoint lists are in `docs/openapi.json`; only transport-relevant behavior
is called out here:

- `/panel/streams/*` (streamed panels) - `GET /panel/streams/assignments` is a
  regular poll target; `POST /panel/streams/{sessionId}/ingest` is not
  request/reply, it is one long-lived chunked POST per stream session that
  carries framed H.264 access units for the life of the session (request
  body size and minimum data rate limits are lifted for this route).
- `/diagnostics/*` - health/incident/SMART/GPU/memory snapshots plus
  downloadable support bundle (`/diagnostics/support-bundle/download`) and PDF report
  (`/diagnostics/report.pdf`) endpoints. No WebSocket topic.
- `/tryx/*` - Tryx Panorama panel control, including cloud theme catalog
  proxying (`/tryx/cloud/*`) and local media upload/select. No WebSocket
  topic.
- `/cloud/*` - online account registration/login/sync endpoints
  (`nexus-api`-backed), plus device reporting/management and the
  `/cloud/benchmarks/submit` leaderboard forwarder. The `cloud/accounts`
  topic fires when the active account changes.
- `/home-assistant/*` - Home Assistant entity config and control. Introduces
  the `homeAssistant` multiplex topic (see below).
- `/rtc/offer` - WebRTC DataChannel direct P2P signaling: the phone posts an
  SDP offer (typically over the relay tunnel) and gets an SDP answer back,
  after which media/data can flow peer-to-peer instead of through the relay.
  No WebSocket topic.

## Transport Topology

| Path | Transport | Direction | Purpose |
|---|---:|---|---|
| `/ping` | HTTP GET | desktop/panel -> service | Public health check and platform/version probe. |
| `/pair` | HTTP GET | desktop/panel -> service | Loopback-only token bootstrap for service API calls. |
| All authenticated REST routes | HTTP(S) fetch | desktop/panel -> service | Commands, status snapshots, CRUD, uploads, downloads. |
| Static assets and SPA shell | HTTP(S) GET | desktop/panel -> service | Load the bundled web app, panel app, and media/icons. |
| `/ws?token=...` | WebSocket text JSON | desktop/panel <-> service | One multiplexed topic socket per app surface. |
| `/lighting/output?token=...` | WebSocket binary | desktop -> service | Dedicated live lighting frame stream for the Lighting view. |
| `127.0.0.1:6742` | TCP binary | service -> OpenRGB | RGB device discovery, mode changes, zone resize, and LED frame pushes. |
| OBS endpoint, default `ws://127.0.0.1:4455` | WebSocket JSON | service -> OBS | OBS status and controls when OBS widget/actions are used. |
| Steam Web API | HTTPS | service -> internet | Steam profile, games, friends, achievements when Steam widget/API routes run. |
| `ipwho.is` and `api.open-meteo.com` | HTTPS | service -> internet | Weather location and forecast, cached by the service. |
| `discord://`, `steam://`, app URLs | OS protocol launch | service -> OS | Opens local apps. These are not app-network polling paths. |

## Auth and Reconnect Behavior

- `fetchService()` calls `getToken()` before authenticated requests. If no token
  is cached, it calls `/pair`; if a request gets `401`, it clears the token and
  calls `/pair` again before retrying once.
- `resolveAuthWs()` puts the token in the WebSocket query string.
- The multiplex WebSocket reconnects after `2000 ms` on close/error and resends
  all active subscriptions on reconnect.
- `/lighting/output` reconnects after `2000 ms` on close/error.
- Phone panel pairing uses `POST /panel/phone/claim` once per QR token and then
  stores a 30-day idle phone session token. The QR URL and claim response also
  include the service machine name so the native app can label paired
  computers while still showing `host:port` as the connection reference. The
  native app also calls `GET /panel/phone/service-info` with its phone session
  token on launch/switch so existing Keychain records can hydrate the same
  machine name after an app update without requiring a reset or re-pair.
- The pair QR encodes both an HTTPS port (for the native iOS app, which pins
  SPKI) and a plain-HTTP port (`httpPort`, default `9400`) for the browser
  fallback at `https://hellonexus.com/r/pair`. Browsers can't pin the
  service's self-signed LAN cert, so the "Continue in browser" button on the
  Universal Link landing page navigates to `http://<lan-ip>:<httpPort>/panel/phone?pair=...`
  instead of HTTPS. The panel page renders a yellow `PanelInsecureBanner`
  whenever loaded over plain HTTP from a non-loopback host.
- HTTP browser sessions still go through the same `/panel/phone/claim` flow
  and receive the same 30-day idle session cookie; only the transport
  differs. Hardening (bind session to claim-time IP/UA, `Sec-Fetch-Site`/
  `Origin` pin on state-changing HTTP requests, shorter idle TTL when
  claimed over HTTP) is a planned follow-up.

## Always-on Desktop Traffic

This is the traffic from the main dashboard while the local service is online,
before counting the currently selected view.

| Request/frame | Cadence | Count | Purpose |
|---|---:|---:|---|
| `GET /ping` | every `5000 ms` | `0.2 req/s` | Service connection state. |
| `GET /cooling/status` | every `500 ms` | `2 req/s` | Sidebar cooling status dot and calibration state. |
| `GET /lighting/status` | every `500 ms` | `2 req/s` | Sidebar lighting status/scanning state. |
| `GET /panel/status` | every `500 ms` | `2 req/s` | Sidebar panel/kiosk and phone subscriber count. |
| `GET /profiles` | once on online mount, then after profile mutations | event | Profile dropdown state. |
| `GET /preferences` | once on `UiSettingsProvider` mount/profile switch | event | Server-backed UI preferences. |
| `POST /preferences` | debounced `250 ms` after setting changes | event | Persist profile-scoped preferences. |
| `WS /ws` subscribe `monitoring`, `screentime` | one socket while online | 1 socket | Global monitoring store and screen-time store. |
| WS server frame `monitoring` | service default `1000 ms` | `1 frame/s/client` | Composite sensors, process, and network snapshot. |
| WS server frame `screentime` | every `10000 ms`, plus snapshot on subscribe | `0.1 frame/s/client` | Current focus app and daily usage. |

There is no dedicated request to seed installed-memory totals for
process/memory charts (the prior `GET /system/memory/total` route is gone).
Total capacity now rides the existing periodic sensor payloads: each memory
sensor carries a `theoreticalMaximum` field (installed RAM in GB), and the
composite `monitoring` frame also carries a formatted `MemoryTotal` string.

Base desktop idle count in the current working tree:

- HTTP polling: `6.2 req/s` (`6 req/s` from service state, `0.2 req/s` ping).
- WebSocket: one persistent `/ws` connection, usually receiving `1.1` JSON
  frames per second (`monitoring` every second and `screentime` every 10
  seconds) while subscribed.

The default selected My Computer view is now Dashboard. It reuses the panel
widget engine in embedded desktop mode and adds one `GET /preferences` on mount
to hydrate `UiSettings.DashboardLayout`; layout edits debounce a
`POST /preferences` carrying the profile-scoped `dashboardLayout` field.

`GET /panel/status` returns this payload every `500 ms` while the desktop
sidebar is mounted and the service is online:

| Field | Meaning |
|---|---|
| `msg` | `"running"` when the Edge kiosk process is tracked as running, otherwise `"stopped"`. |
| `kioskRunning` | Boolean form of the tracked Edge kiosk process state. |
| `phoneConnected` | `true` when at least one phone panel is subscribed to `panel/phone/presence`. |
| `phoneSubscribers` | Count of current `panel/phone/presence` WebSocket subscribers. |

## Multiplex WebSocket Topics

All topics share `/ws`. Client commands are JSON text frames:

```json
{"sub":["monitoring","screentime"]}
```

```json
{"unsub":["monitoring"]}
```

Server frames are JSON envelopes:

```json
{"t":"monitoring","d":{}}
```

### Snapshot on subscribe

Some topics broadcast slowly (`screentime` every `10000 ms`) or only on change
(`volume`). For these, a late subscriber would otherwise wait for the next
broadcast tick to receive any state. `MultiplexHub` exposes a snapshot
registry: a producer registers `Func<ReadOnlyMemory<byte>?>` per topic, and on
every `sub` the hub immediately delivers the cached envelope to that one
client. The provider returns a pre-built `WsEnvelope.Build` envelope or `null`
when no state has been produced yet. The periodic / event-driven broadcast
keeps the cache fresh.

Topics currently using snapshot-on-subscribe:

| Topic | Producer | Cache populated when |
|---|---|---|
| `screentime` | `MonitoringBroadcaster` | first broadcast tick after a subscriber appears (≤1 s) |
| `volume` | `MonitoringBroadcaster.BroadcastVolumeIfChangedAsync` | first broadcast tick after a subscriber appears (≤1 s) |
| `conflicts` | `ConflictWatcher` | first poll tick after startup (~2 s), refreshed every 5 s poll |
| `panel/phone/pair-code/request` | `PanelPhonePairingService` | only while a pair request is pending; no snapshot (subscriber gets nothing) once it resolves |

Other slow / event-driven topics (e.g. `prefs`, `lighting`, `cooling`,
`panel/device`) are good future candidates for the same registry.

| Topic | Producer | Cadence | Consumers today | Purpose |
|---|---|---:|---|---|
| `monitoring` | `MonitoringBroadcaster` | fixed `1000 ms` (no runtime-configurable route) | desktop and panel app-level bridge | Composite CPU/GPU/memory/storage/motherboard/process/network frame. |
| `screentime` | `MonitoringBroadcaster` | every `10000 ms`, plus snapshot on subscribe | desktop and panel app-level bridge | Focus app and usage history. |
| `cpu` | `MonitoringBroadcaster` | default `1000 ms` | `useSensors()` | CPU sensor component. |
| `gpu` | `MonitoringBroadcaster` | default `1000 ms` | `useSensors()` | GPU sensor components. |
| `memory` | `MonitoringBroadcaster` | default `1000 ms` | `useSensors()` | Memory sensor component. |
| `storage` | `MonitoringBroadcaster` | default `1000 ms` | `useSensors()` | Storage components. |
| `motherboard` | `MonitoringBroadcaster` | default `1000 ms` | `useSensors()` | Motherboard sensors, including fan sensors. |
| `summary` | `MonitoringBroadcaster` | default `1000 ms` | `useSensors()` | Condensed "Quick" component (a cpu/gpu/memory subset) for compact sensor displays. |
| `fps` | `MonitoringBroadcaster` + `IFpsProvider` | default `1000 ms` while subscribed | Performance widget slots configured to FPS | Foreground-window FPS sensor. Windows ETW capture starts on first `fps` topic subscriber and stops when the last subscriber leaves. Not included in the global `monitoring` frame. |
| `processes` | `MonitoringBroadcaster` | default `1000 ms` | no direct current React subscriber | Process frame, also included in `monitoring`. |
| `gpu-processes` | `MonitoringBroadcaster` + `GpuProcessMonitor` | default `1000 ms` while subscribed, Windows only | `useProcessMonitor()` | Per-process GPU engine/VRAM usage (PDH counters). Same privacy class as `processes`: exposes running app names. |
| `network` | `MonitoringBroadcaster` | default `1000 ms` | no direct current React subscriber | Network frame, also included in `monitoring`. |
| `extras` | `MonitoringBroadcaster` | default `1000 ms` | `useSensorExtras()` | Detailed-tab sensor extras not carried in the composite `monitoring` frame. |
| `volume` | `MonitoringBroadcaster.BroadcastVolumeIfChangedAsync` | event-driven, evaluated each `1000 ms` tick, plus snapshot on subscribe | `useSystemVolume()` | System default-render audio volume + mute (see Cooling view traffic below for the HTTP fallback). |
| `cooling-realtime` | `CurveEngine` | default `1000 ms` when curves exist | Cooling view | Live fan channel speed/RPM. |
| `cooling-curves` | `CurveEngine` | default `1000 ms` when curves exist | Cooling view | Curve calculations and applied outputs. |
| `conflicts` | `ConflictWatcher` | poll every `5000 ms` server-side, broadcasts only on change, plus snapshot on subscribe | `useConflictApps()` | Competing RGB/control app detection (iCUE, NZXT CAM, etc) driving the device-page conflict gate. |
| `devices` | `DeviceBroadcaster` | re-enumerates every `5000 ms` only while subscribed, broadcasts only on change or first subscriber | `useDevices()`, `useUsbDevices()` | Push-driven refetch for the curated device list and raw USB list; see Devices view traffic below. |
| `benchmark/{runId}` | `BenchmarkRunner` | provider progress, documented around `500 ms` | Benchmark view | Benchmark progress and terminal state. |
| `audio` | `IBeatsProvider.OnBeat`, broadcast from `AppBootstrap` | event-driven while audio capture is running | Lighting view audio preview | Audio level/bass/mid/high/beat/spectrum snapshot for shader preview. |
| `panel/phone/presence` | subscription-count topic | no payload today | phone panel subscribes | Lets `/panel/status` count connected phone remotes. |
| `prefs` | event-driven on every `POST /preferences` and `POST /profiles/{id}/switch` | `{revision: long}` | panel `usePanelTheme`, anything that reads global `Ui*` settings | Push-driven refetch. |
| `lighting` | event-driven on every `/lighting/*` mutation | `{revision: long}` | `LightingQuickWidget` and any future cross-device lighting subscriber | Push-driven refetch; replaces the prior 4s `setInterval(hydrate)` poll. |
| `lighting/mapping-applied` | event-driven when a community mapping auto-applies to a first-seen device | mapping payload rides the frame directly | `MappingAppliedToasts` | Toast + one-click undo; fires alongside a regular `lighting` broadcast for state refetch. |
| `cooling` | event-driven on every `/cooling/*` mutation | `{revision: long}` | `CoolingQuickWidget` | Push-driven refetch; replaces the prior 2s `setInterval(refresh)` poll. Distinct from `cooling-realtime` above. |
| `cooling/warnings` | event-driven on an active-warning-set transition (NP50 heartbeat worker; future warning producers) | `{revision: long, deviceId: string}` | no current REST endpoint or React subscriber found | Broadcast infrastructure only; not yet wired to a route or UI. |
| `panel/device` | event-driven on every panel device CRUD (`POST /panel/devices`, `POST /panel/devices/{id}`, `DELETE /panel/devices/{id}`) | `{revision: long, deviceId: string}` | `usePanelLayout` filters by `deviceId === mine` and refetches the device record | Cross-device layout sync; replaces the BroadcastChannel cross-tab path for cross-device updates. |
| `gallery` | event-driven on gallery source add/remove/upload | `{revision: long}` | `GalleryPage`, `useGallery()` | Push-driven refetch of `GET /gallery/items`. |
| `cloud/accounts` | event-driven on account activation or logout (login, switch, or a recovery the service's own poll loop approved) | `{revision: long}` | `useCloudAccounts()` | Push-driven refetch of `GET /cloud/accounts`, so a sign-in that no page requested still shows without a reload. |
| `displays` | event-driven on display topology or monitor-panel assignment change | `{revision: long}` | `useDisplayTopology()` | Push-driven refetch of `GET /displays/topology`. |
| `homeAssistant` | event-driven on Home Assistant entity cache change | `{revision: long}` | `HomeAssistantPage` | Push-driven refetch of `GET /home-assistant/entities`. |
| `mediaLibrary` | event-driven on lighting media library mutation (import/commit/delete) | `{revision: long}` | `useMediaLibrary()` | Push-driven refetch of `GET /media/library`. |
| `panel/phone/pair-code/request` | event-driven, plus snapshot on subscribe while a request is pending | request/cancelled payload | `IncomingPairModal` | Manual pair-code request lifecycle for the dashboard Allow/Deny modal. |
| `panel/phone/pair-qr/refresh` | event-driven on host network address change (VPN toggle, Wi-Fi/wired switch, DHCP renew) | `{revision: long}` | `PairRemoteContent` | Prompts a QR re-fetch after a LAN IP change instead of waiting out the QR's TTL. |
| `system/accent` | event-driven on OS accent color change, Linux only | `{hex: string}` | `SystemAccentSync` | Live OS accent colour sync (watches the XDG portal). |
| `transfer` | event-driven when a phone-to-PC transfer lands | event payload rides the frame directly | `TransferToasts` | No canonical resource to refetch; the payload is the notification. |
| `update` | event-driven when an update becomes available or finishes staging | `{revision: long}` | dashboard `sidebar` | Push-driven refetch of `GET /update/status` instead of waiting out the sidebar's 60s poll. |
| `twitch/chat/{channel}` | `TwitchChatHub` | event-driven, batched at `250 ms`, plus the retained buffer as a snapshot on subscribe | Twitch widget | Live chat for one channel, pre-split into text and emote runs. Demand-driven: the first subscriber makes the service JOIN the channel and the last unsubscriber makes it PART, so no Twitch connection is held for an unwatched widget. Emote images are proxied through `GET /api/twitch/emote/{id}`. |
| `streamdeckTiles` | `StreamDeckConnectionWorker` | event-driven per-tile on a wire-hash change, capped at 4 monitoring and 4 weather tiles per tick round-robin; no snapshot registry - a fresh subscriber clears every tracked hash so the following tick(s) re-broadcast every visible tile | `StreamDeckDevicePage` Customize tab | Live JPEG render of a visible monitoring/weather Stream Deck key, pixel-identical to what the physical key shows; broadcast only while subscribed. |
| `monitoring/history-tail` | `MonitoringHistoryTailBroadcaster`, fed by `MetricsSampler` | event-driven, once per 1 Hz sample, only while subscribed | Monitoring page's live history-tail hook | Same `MetricsHistoryResponse` shape as `GET /monitoring/history`'s tail poll, decimated to one point per series; additive push alongside the existing HTTP poll. |
| `monitoring/events` | `BroadcastingMonitoringEventStore` | event-driven on every appended timeline event (USB attach/detach, app-open, UAC escalation, custom) | Monitoring page's live events hook | One `MonitoringEventDto`, same shape as `GET /monitoring/events`; additive push alongside the existing HTTP poll. |
| `monitoring/privacy` | `BroadcastingPrivacySessionStore` | event-driven on every privacy-session open/close, Windows only | Monitoring page's live privacy hook | One `PrivacySessionWire`, same shape as an entry in `GET /monitoring/privacy`'s `sessions` array; additive push alongside the existing HTTP poll. |

Notes:

- The backend default monitoring interval is `1000 ms`; a frontend comment in
  `App.tsx` says `2 s`, but the active service code initializes
  `MonitoringBroadcaster` to `1000 ms`.
- `useSensors()` subscribes to six individual sensor topics (`summary`,
  `cpu`, `gpu`, `memory`, `storage`, `motherboard`). When a surface is
  already subscribed to `monitoring`, this duplicates sensor payloads on the
  same socket.
- `CurveEngine` returns early when no curves exist, so `cooling-realtime` and
  `cooling-curves` do not broadcast in that case even if subscribed. The
  event-driven `cooling` (revision) topic is unaffected; it fires from route
  mutations regardless of whether any curve exists.

## Dedicated Lighting WebSocket

`GET /lighting/output` upgrades to a binary WebSocket. The Lighting view opens
this socket when mounted.

| Frame source | Cadence | Count | Purpose |
|---|---:|---:|---|
| `LightingEngine.OnFrame` binary v3 frame | active effect frame interval | about `30 fps` by default, `60 fps` in screen mode | Canvas pixels and per-device LED colors for the live lighting canvas. |

The frame interval is controlled by `LightingEngine.FrameIntervalMs`.
`POST /lighting/frame-rate` can set 1-120 fps. Screen mirror mode sets
`FrameIntervalMs = 16` (about 60 fps). When no lighting effect is active, no
continuous frames are sent; `Stop()` sends a final black frame.

## Desktop View Traffic

### Monitoring

No additional network polling beyond the app-level `/ws` `monitoring` and
`screentime` subscriptions. Monitoring hooks read the local `monitoringStore`.

### Lighting

Mounted Lighting view traffic:

| Request/frame | Cadence | Count | Purpose |
|---|---:|---:|---|
| `WS /lighting/output` | one socket, reconnect `2000 ms` | 1 socket | Live canvas/device colors. |
| binary lighting frame | active effect frame interval | about `30-60 frames/s` | Live preview frames. |
| `GET /lighting/current` | every `1000 ms` | `1 req/s` | Current lighting sync mode. |
| `GET /devices/lighting-devices/all` | on mount and profile change, then push-driven refetch on the `lighting` and `devices` WS topics | event + push | Lighting device layout and power state. No longer a `3000 ms` poll. |
| `GET /devices/usb/all` | every `5000 ms` | `0.2 req/s` | VID/PID detection for lighting device support. Not individually re-verified against the `devices` topic migration described in the Devices view section below; may also now be push-driven. |
| `GET /lighting/static/settings` | every `1000 ms` when static mode | `1 req/s` | Reconcile static color. |
| `GET /lighting/animate/settings` | every `1500 ms` when animate mode | `0.67 req/s` | Reconcile active effect/templates. |
| `GET /lighting/animate/settings`, `/lighting/music-reactive`, `/lighting/static/settings`, `/lighting/screen/effect`, `/lighting/media/effect` | one-shot on mode/raw sync changes | event | Hydrate controls for the active mode. |
| `GET /lighting/effects/{key}/thumbnail.bmp` | on effect grid/quick widget thumbnail load | event, one per visible/effect thumbnail | Effect thumbnails. |
| `GET /media/{id}/thumbnail` | on media mode thumbnail load | event | Media thumbnails. |

Lighting user actions call `POST /lighting/static/headless-start`,
`POST /lighting/animate/headless-start`, `POST /lighting/screen/headless-start`,
`POST /lighting/gif/headless-start`, `POST /lighting/stop`,
`POST /lighting/music-reactive`, `POST /lighting/screen/effect`,
`POST /lighting/media/effect`, device layout/power/zone/LED editor routes, and
media-library import/play/delete routes as needed. `DELETE /media/{id}` is
idempotent for valid media IDs so stale UI entries can be cleared after their
folder, thumbnail, or frame data was removed out of band.

### Cooling

Mounted Cooling view traffic:

| Request/frame | Cadence | Count | Purpose |
|---|---:|---:|---|
| `GET /cooling/fans` | one-shot on mount/profile/control refresh | event | Fan channels. |
| `GET /cooling/sources` | one-shot and every `1000 ms` | `1 req/s` | Temperature source labels/values. |
| `GET /cooling/curves` | one-shot on mount/profile/control refresh | event | Curve config. |
| `GET /cooling/profiles` | one-shot and every `1000 ms` | `1 req/s` | Detect active cooling profile changes. |
| WS topic `cooling-realtime` | default `1000 ms` when curves exist | `1 frame/s/client` | Live fan speed/RPM. |
| WS topic `cooling-curves` | default `1000 ms` when curves exist | `1 frame/s/client` | Live curve calculations. |
| WS topics `summary`,`cpu`,`gpu`,`memory`,`storage`,`motherboard` | default `1000 ms` | up to `6 frames/s/client` | Sensor data for the view and curve labels. |
| WS topic `volume` | event-driven (push only on change, evaluated each `1000 ms` broadcaster tick), plus snapshot on subscribe | <=`1 frame/s/client` | System default-render audio volume + mute. Backs the panel media widget slider. `useSystemVolume` HTTP-polls `/system/volume` at `1000 ms` as a connect-time / fallback path; panel-authorized `POST /system/volume` and `POST /system/volume/mute` apply user changes. |

Cooling user actions call `POST /cooling/fan/{id}/speed`,
`POST /cooling/fan/{id}/auto`, `POST /cooling/fan/{id}/name`,
`POST /cooling/curves/set`, `POST /cooling/profile/{name}`, and
`POST /cooling/calibrate`.

`POST /cooling/profile/{name}` accepts the canonical preset keys `off`,
`silent`, `balanced`, `performance`, and `custom` (legacy `auto` is treated
as a synonym for `off`). The preset persists in
`NexusSettings.Cooling.ActivePreset` and is returned as `active` from
`GET /cooling/profiles`. Switching to `silent`/`balanced`/`performance`
ensures a single shared `preset-{name}` curve owns every fan; switching to
`custom` restores the snapshot saved in
`Cooling.CustomFanCurveAssignments`; switching to `off` releases every fan
to hardware control (a Q-series hub goes to its `Cooling.HubControlModes`
entry, firmware curve by default; an NP50 to its EEPROM default mode).

### Devices

Mounted Devices view traffic:

| Request | Cadence | Count | Purpose |
|---|---:|---:|---|
| `GET /devices/all` | one-shot REST seed on mount (Available tab active), refetched on the `devices` WS topic | event + push | Curated connected device list. |
| `GET /devices/usb/all` | one-shot REST seed whenever the Devices view is mounted, refetched on the `devices` WS topic | event + push | VID/PID support detection; one subscription shared by the catalog highlight and the Connected Devices modal. |
| `GET /displays` | every `5000 ms` on Panels tab | `0.2 req/s` | Attached monitor inventory and brightness-control capability summary. |
| `GET /panel/status` | every `5000 ms` on Panels tab | `0.2 req/s` | Panel host running state for directly managed panels. |
| `GET /panel/phone/sessions` | every `5000 ms` on Panels tab | `0.2 req/s` | Paired phone/tablet panel presence metadata. |
| `GET /y70/brightness`, `/y70/rotation`, `/y70/toggle` | one-shot when Y70 popup/widget mounts | event | Y70 controls hydration. |

The curated device list and raw USB list moved from 5s
REST polling to a one-shot fetch plus the `devices` WS topic (see the
Multiplex WebSocket Topics table above); `DeviceBroadcaster` re-enumerates
every 5s server-side only while the topic has a subscriber, and only
broadcasts when the fingerprint changes or a first subscriber arrives. The
Devices view's tab structure has changed since
this table was last fully audited (tab keys are now `available`, `displays`,
`firmware`, `specs`, with the raw USB list reachable from a modal rather
than a separate tab); the `GET /displays`, `/panel/status`, and
`/panel/phone/sessions` rows above have not been individually re-verified
against the current tab layout.

Devices user actions call Y70 `POST` routes, firmware routes, lighting
rescan/identify routes, and supported-device modal routes on demand.

### Benchmark

| Request/frame | Cadence | Count | Purpose |
|---|---:|---:|---|
| `POST /benchmark/start` | user action | event | Start benchmark run. |
| WS topic `benchmark/{runId}` | provider progress, documented around `500 ms` | about `2 frames/s` while running | Progress updates. |
| `GET /benchmark/status/{runId}` | every `2000 ms` while running | `0.5 req/s` | Fallback if WS frame is missed. |
| `GET /benchmark/result/{runId}` | when terminal WS/status arrives | event | Final result. |
| `POST /benchmark/cancel/{runId}` / `POST /benchmark/reset` | user action | event | Cancel/reset. |

### Settings and Tools

| Request | Cadence | Count | Purpose |
|---|---:|---:|---|
| `GET /start` | once when Settings General tab mounts | event | Start-on-login state. |
| `POST /start` | user toggle | event | Persist start-on-login. |
| `GET /pawnio` | once when Tools PawnIO card mounts | event | PawnIO install/open state. |
| Screen-time browse routes under `/api/screentime/...` | user navigation/actions | event | Historical screen-time data and deletes. |
| Profile import/export routes | user action | event | Profile file transfer. |

## Panel Traffic

### Panel Shell

Y70 kiosk panel baseline:

| Request/frame | Cadence | Count | Purpose |
|---|---:|---:|---|
| Static asset GETs | page load | event | Load panel shell. |
| `GET /pair` or token from `/panel?token=...` | first auth only | event | API token. |
| `GET /preferences` | once from `usePanelLayout` | event | Panel layout. |
| `GET /preferences` | once from `usePanelTheme` | event | Panel theme mode, accent, background, and animation config. |
| `POST /preferences` | after layout/theme/background edits | event | Persist panel layout/theme/background config. |
| `GET /lighting/shaders/{effect}` | once per selected shader background effect, cached in-browser | event | Fetch GLSL source; rendering runs locally on the panel device. |
| `WS /ws` subscribe `monitoring`, `screentime` | one socket | 1 socket | Global panel monitoring/screen-time store. |
| `GET /ping` | every `3000 ms` on Y70/kiosk, after an initial interval delay | `0.33 req/s` | Close kiosk after 3 consecutive failures. |
| WS server frame `monitoring` | default `1000 ms` | `1 frame/s/client` | Panel monitoring store. |
| WS server frame `screentime` | every `10000 ms`, plus snapshot on subscribe | `0.1 frame/s/client` | Panel screen-time store. |

Phone panel baseline:

| Request/frame | Cadence | Count | Purpose |
|---|---:|---:|---|
| `POST /panel/phone/claim` | once when opened with `?pair=...` | event | Exchange QR pair token for phone session token and service machine name. |
| `GET /panel/phone/service-info` | once on native app launch or paired-computer switch | event | Refresh the service machine name for stored native pairings. |
| Static asset GETs | page load | event | Load phone panel shell. |
| `GET /preferences` | twice on mount | event | Layout and theme/background config. |
| `POST /preferences` | after layout/theme/background edits | event | Persist phone layout/theme/background config. |
| `GET /lighting/shaders/{effect}` | once per selected shader background effect, cached in-browser | event | Fetch GLSL source; rendering runs locally on the phone. |
| `WS /ws` subscribe `monitoring`, `screentime`, `panel/phone/presence` | one socket | 1 socket | Store data and presence count. |
| WS server frame `monitoring` | default `1000 ms` | `1 frame/s/client` | Phone monitoring store. |
| WS server frame `screentime` | every `10000 ms`, plus snapshot on subscribe | `0.1 frame/s/client` | Phone screen-time store. |

The phone panel does not run the kiosk `/ping` watchdog.

### Panel Widgets

Panel widgets add traffic only when mounted in the current panel layout.

| Widget | Request/frame | Cadence | Purpose |
|---|---|---:|---|
| Monitoring widget | WS topics `summary`,`cpu`,`gpu`,`memory`,`storage`,`motherboard` via `useSensors()` | default `1000 ms` | Sensor gauges. |
| Monitoring widget network slots | no extra network | local store | Network In/Out/Total gauges read totals from the app-level `monitoring` store. |
| Monitoring widget | WS topic `fps` via `useFpsSensors()` | default `1000 ms` only while an FPS slot is active | FPS gauge. This topic is not subscribed for non-FPS widget configs, so the service does not start ETW capture for ordinary monitoring widgets. |
| Screen-time widget | no extra network | local store | Reads app-level `screentime` store. |
| Media widget | `GET /api/media` | default `2000 ms` | Active media sessions. |
| Media widget | `GET /api/media/{source}/album-art` | on active song/source change | Album art blob. |
| Media widget | `POST /api/media/{source}/control` | user action | Play/pause/next/previous. |
| Weather widget | `GET /api/weather` | every `15 min` | Weather snapshot. |
| Lighting Quick | `GET /lighting/current`, `/lighting/animate/settings`, `/lighting/static/settings` | every `4000 ms` | Hydrate mode/effect/color. |
| Lighting Quick | `GET /lighting/effects/{key}/thumbnail.bmp` | one load per effect when not compact | Effect thumbnails. |
| Lighting Quick | lighting `POST` routes | user action | Apply mode/effect/color/music reactive. |
| Cooling Quick | WS topics `summary`,`cpu`,`gpu`,`memory`,`storage`,`motherboard` via `useSensors()` | default `1000 ms` | Temperature/RPM gauges. |
| Cooling Quick | `GET /cooling/profiles` | every `2000 ms` | Active/profile list. |
| Cooling Quick | `POST /cooling/profile/{name}` | user action | Apply cooling profile. |
| OBS widget | `GET /api/obs/status` | every `2500 ms` | OBS connection/status. |
| OBS widget | OBS `POST` routes | user action | Connect, launch, record, stream, scene. |
| Discord widget | `GET /api/discord/status` | every `5000 ms` | Discord provider status. |
| Discord widget | Discord `POST` routes | user action | Launch/open/mute/deafen/disconnect. |
| Steam widget | `GET /api/steam/status`, `/profile`, `/recent-games`, `/owned-games`, `/friends` | every `15000 ms` when ready | Steam status and lists. |
| Steam widget | `GET /api/steam/achievements/{appId}` | on current game change | Current-game achievements. |
| Steam widget | `POST /api/steam/launch` | user action | Launch Steam. |
| Y70 Controls | `GET /y70/brightness`, `/y70/rotation`, `/y70/toggle` | once on mount | Hydrate controls. |
| Y70 Controls | Y70 `POST` routes | user action | Brightness/orientation/screen toggle. |
| Displays widget | `GET /displays` | on mount and every `5000 ms` | Enumerate monitors, current brightness, capabilities, and provider write policy. |
| Displays widget | `POST /displays/{id}/brightness` | slider target changes | Sends brightness target values; service coalesces and paces hardware writes per display. |
| Macros | `GET /shortcuts/icon?targetId=...` | on selected app icon load | Macro icon. |
| Macros | `POST /panel/macros/open-url`, `/panel/macros/shortcut`, `/shortcuts/launch` | user action | Execute macro. |
| App picker settings | `GET /shortcuts` | once when picker opens | App list. |
| App picker settings | `GET /shortcuts/icon?targetId=...` | lazy as rows enter viewport | App icons. |

Panel-local games, clocks, timers, gallery rotation, stopwatch/countdown, and
touch/drag timers use browser timers only and do not send service network
requests.

## Desktop and Panel Coordination

Because there is no direct desktop-to-panel transport, these are the effective
message paths:

| User-visible flow | Actual network path | Count |
|---|---|---:|
| Desktop sees panel/kiosk/phone status | desktop `GET /panel/status` -> service; response is `msg`, `kioskRunning`, `phoneConnected`, `phoneSubscribers` | every `500 ms` while desktop online |
| Desktop opens phone pairing modal | desktop `GET /panel/phone/pair-qr`, `GET /panel/phone/sessions`, and `GET /panel/phone/remote-control` -> service | immediate |
| Desktop keeps phone sessions fresh while modal is open | desktop `GET /panel/phone/sessions` -> service | every `3000 ms` |
| Desktop refreshes phone QR | desktop `GET /panel/phone/pair-qr` -> service | about every `85 s` before the `90 s` QR expiry |
| Desktop toggles the Pair Remote killswitch | desktop `POST /panel/phone/remote-control {enabled}` -> service. On `false` the service force-closes every phone-session WebSocket with close code `1008` reason `"revoked"` and persists the new state in `AuthSettings.RemoteControlEnabled`. Subsequent phone-authenticated REST and `/ws` upgrade attempts return `403 RemoteDisabled` until re-enabled. | event |
| Phone claims pairing | phone `POST /panel/phone/claim` -> service. Refused with `{paired:false, error:"remote-disabled"}` while killswitch is OFF. | once per QR claim |
| Native app refreshes computer label | phone `GET /panel/phone/service-info?token=...` -> service | once on native app launch or pair switch |
| Phone appears connected to desktop | phone subscribes `panel/phone/presence` on `/ws`; desktop reads count through `/panel/status` | one WS subscribe plus desktop's existing poll |
| Panel layout/theme sync between dashboard, kiosk, and phone | each panel surface fetches/posts `/preferences`; BroadcastChannel only syncs tabs in the same browser profile | network only on fetch/post |
| Widget/app actions launched from panel | panel widget REST `POST` -> service -> OS/provider | one request per user action |

## Service-to-Sidecar and External Network

These messages do not go from desktop or panel directly, but they are network
traffic generated by the service in response to app requests or background
features.

| Feature | Service transport | Cadence/count | Purpose |
|---|---|---:|---|
| OpenRGB connect | TCP `127.0.0.1:6742` | on first lighting effect and reconnect attempts at most every `5 s` | Connect to bundled OpenRGB SDK server. |
| OpenRGB device refresh | TCP request/reply | every `3000 ms` while RGB bridge active | Refresh/drain device list and apply direct mode. |
| OpenRGB USB topology check | OS USB enumeration | every other refresh tick, about `6000 ms` | Bounce OpenRGB when USB count changes. |
| OpenRGB LED frame push | TCP binary | one per active lighting frame per physical device/zone | Push RGB LED colors. |
| OBS provider | WebSocket to configured OBS endpoint, default `ws://127.0.0.1:4455` | on OBS status/control routes; socket reused while open | OBS status and controls. |
| Steam provider | HTTPS `api.steampowered.com` | when Steam widget/API routes run; widget polls every `15 s` | Steam profile, games, friends, achievements. |
| Weather provider | HTTPS `ipwho.is` | cached `24 h` | Approximate location. |
| Weather provider | HTTPS `api.open-meteo.com` | cached `15 min`, stale served up to `2 h` | Forecast/current weather. |

## Current Hotspots for Audit

- The desktop sidebar service-state poll is the largest always-on REST source:
  three endpoints every `500 ms`, or `6 req/s`.
- Desktop and panel subscribe to the composite `monitoring` topic globally.
  Any `useSensors()` consumer adds six more sensor topic frames per second,
  duplicating data already present in `monitoring`.
- Devices view's curated/USB lists are push-driven via the
  `devices` topic rather than polled (see Devices view traffic above);
  `DeviceBroadcaster` re-enumerates the OS device/USB lists every `5000 ms`
  server-side only while the topic has a subscriber, and only broadcasts
  when the enumerated fingerprint actually changed.
- Lighting view has multiple reconciliation loops at once: `/lighting/current`
  every second, USB every five seconds, plus mode-specific settings polling.
  The device list itself moved from a three-second poll to push-driven
  refetch on the `lighting`/`devices` WS topics.
- Phone pairing presence uses a WebSocket subscription count, but the desktop
  still observes it through `/panel/status` polling every `500 ms`.
- Panel layout and theme/background each fetch `/preferences` separately on
  mount, so a panel surface normally does two preference GETs before widget
  traffic. Shader background source fetches are effect-scoped and browser
  cached after the first compile.
- Several comments mention older cadences: the active backend monitoring
  broadcaster default is `1000 ms`, and the active lighting engine defaults to
  about `30 fps` unless screen mode or `/lighting/frame-rate` changes it.
