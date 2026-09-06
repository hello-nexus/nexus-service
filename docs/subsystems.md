# Subsystems

Per-subsystem detail for `nexus-service`: routes, invariants, and security
notes that are too specific for the README. The generated route inventory is
`openapi.json`; WebSocket topics and cadence are in `network-transport.md`.
Paths below are relative to `src/`, except `data/` (repo root) and `db/` (the
runtime data directory).

## Monitoring

- Sensors: LibreHardwareMonitor on Windows, IOKit on macOS, sysfs/hwmon on
  Linux. CPU, GPU, RAM, network, disk, fan, temperature, FPS, battery
  (laptops), media sessions.
- Timeline events (app opens, admin elevations, USB attach/detach, user-typed
  notes) are collected by `Monitoring/Events/MonitoringEventCollector` into an
  append-only binary log and served over `GET`/`POST`/`DELETE /monitoring/events`
  for the graph's event lane. Privacy-capability access stays in its own store
  behind `GET /monitoring/privacy`.
- Per-drive SMART read intervals are configurable via
  `GET /monitoring/smart-poll` plus the `monitoring.smartPollSeconds`
  preference, applied through LibreHardwareMonitor's
  `StorageDevice.SmartUpdateCycleCount`. A SMART read is an ATA pass-through
  that reloads a parked head, which is why the interval is per drive.
- Each 1 Hz history sample, appended event, and privacy-session upsert also
  pushes live on the `monitoring/history-tail`, `monitoring/events`, and
  `monitoring/privacy` WebSocket topics, alongside HTTP polling.

## Cooling

Fan curves, pump speed, and AIO control. The curve engine, calibration runner,
and safety limits live in `Cooling/`; each hub or cooler family contributes an
`ICoolingProvider` (Corsair LINK, Galahad II, Kraken, Lian Li, Q-series
coolers, SL wireless, SmartHub, NP50, MiniHub, plus the OS-level Windows, macOS,
Linux hwmon, liquidctl and NVIDIA providers). `QSeries/` holds the transport to
Q-series screens.

## Lighting

- RGB via the bundled [headless OpenRGB](https://github.com/hello-nexus/openrgb-headless)
  child process (`Lighting/Rgb/`), plus first-party HYTE and Lian Li protocols.
- Hardware OpenRGB can only find when told it exists (QMK-OpenRGB keyboards,
  E1.31/WLED devices) is registered through `/devices/openrgb/manual-devices/*`,
  which can also adopt an existing OpenRGB install's registrations.
- Effects engine (`Lighting/Engine/`), zones and hub composition
  (`Lighting/Zones/`), LED mappings (`Lighting/Mappings/`), screen sync
  (`Lighting/Capture/`), audio sync, anime mode.
- LAN smart lights (`Lighting/Smart/`): Hue, Nanoleaf, Govee drivers.
- Game sync (`Lighting/GameSync/`): drive your own hardware from a game's
  lighting. Razer Chroma, Alienware LightFX, and Logitech capture via the
  bundled shims from `nexus-gamesync`, plus CS2 Game State Integration.

## Devices and peripherals

- `Devices/DeviceManager` aggregates every `IDeviceHandler` and coordinates
  USB enumeration into the unified device list; `Devices/Firmware/` handles
  updates from the firmware images embedded at build time (vendor files kept
  outside this repository; see the README's Build section).
- First-party drivers under `Peripherals/`: HYTE Keeb, CNVS, hubs, Y70,
  Q-series; iBUYPOWER keyboards and mice; the Lian Li Uni fan family, Galahad
  II AIO, Strimer, SL wireless; Corsair iCUE LINK and Xeneon Edge; NZXT Kraken;
  iBUYPOWER AW5; Tryx; Nollie ARGB channel controllers. The curated
  supported-hardware catalogs sit behind `/peripherals/supported` and
  `/peripherals/all-supported`.
- `Conflicts/` detects competing vendor software, tracks which app owns a
  device, and can stop the competitor when the user opts in.

### Tryx Panorama AIO screen

Drives the Panorama cooler's screen (custom video upload + transcode, presets,
brightness, fan, sensor overlay) over CDC-ACM serial + ADB (`Peripherals/Tryx/`),
exposed as a first-party device through `/tryx/*` (`Routes/TryxRoutes.cs`);
media uploads via `POST /tryx/media`.

### Elgato Stream Deck

- Native gen1 (BMP) and gen2 (JPEG) HID transports across all 14 catalog
  models, from the button-only families to the screenless Pedal, over the
  shared HID stack (`Peripherals/StreamDeck/`). Bench-verified on the Mini;
  the rest transcribed from the MIT python-elgato-streamdeck and
  elgato-streamdeck references.
- Exposed as a first-party device with the `/streamdeck/*` binding, config,
  image and dispatch contract (`Routes/StreamDeckRoutes.cs`) plus
  localhost-only bench/simulator routes (test pattern, `dev/*` sim-press,
  simulate, models).
- Bindings share the `DeckAction` union with the panel's Deck widget (`Deck/`),
  dispatched headlessly by `DeckActionExecutor`.
- A read-only importer (`Peripherals/StreamDeck/ElgatoImport/`,
  `GET`/`POST /streamdeck/elgato/*`) translates a local Elgato Stream Deck
  software profile (ProfilesV3 store) into a Nexus deck preset without
  touching the Elgato install's own files.
- Key icons can be user-supplied images (`DeckIcon` kind `image`), stored
  content-addressed and served through `POST`/`GET /deck/images`
  (`Deck/DeckImageStore.cs`). A key bound to a URL shows that site's icon,
  fetched and cached per origin service-side via `GET /deck/site-icon?url=`
  (`Deck/SiteIconResolver.cs`), since a panel has no route to the internet. A
  key bound to an executable path resolves the file's icon through the same
  `GET /shortcuts/icon` a Start Menu app uses.

### Deck dispatch policy

A panel's own deck keys are dispatched the same way as a Stream Deck's: the
panel names a slot (`POST /panel/deck/dispatch`, `Routes/PanelDeckRoutes.cs`)
and the service executes the action stored in that panel's layout. The keys
that open a file, send a key chord, type text, or play an audio file never take
their file, keys, or text from the panel request. The raw routes
(`/system/open-path`, `/system/input/*`, `/system/audio/play`) are desktop-token
only, and a panel session cannot author such keys into its layout
(`Deck/DeckLayoutPolicy.cs`); it can only trigger the ones set up from the
desktop app.

## Diagnostics

Hardware failure surveillance (`Diagnostics/`, `/diagnostics/*`): SMART/NVMe
drive health, Windows event-log incidents (WHEA, bugchecks, TDRs, disk errors,
app and game crashes), GPU throttle telemetry via the driver's NVML, AIO pump
and fan stall detection, Windows Memory Diagnostic scheduling, and a PnP problem
sweep. Aggregated into per-component health verdicts with tray alerts and a
support-bundle ZIP export.

## Panels

- Pairs and serves the React panel UIs for the HYTE Y70 secondary touch panel,
  the mobile companion (`/panel/phone`), and Q-series on-device screens
  (`Panel/`).
- Streamed panels: panels rendered off-screen by the overlay's stream engine
  and piped as H.264 to USB display devices through swappable transports
  (`Panel/Streams/`, `/panel/streams/*`). The ArtInChip D213 reference
  transport is dev-tools only.

## Apps and the store

- `Widgets/` hosts `nexus.app/1` SDK apps with sensor bindings and a sandboxed
  Web Worker runtime. Legacy `nexus.widget/2` manifests still load.
- `Store/StoreCatalogProxy` fetches the listing from the cloud API
  (`NEXUS_CLOUD_API` overrides the base) and rewrites asset URLs onto
  `/apps-api/store/media/*`: the CSP allows the catalog fetch (`connect-src`
  lists api.hellonexus.com) but `img-src` has no entry for
  assets.hellonexus.com.
- `Store/StoreInstaller` composes the artifact URL from the app id and version
  rather than trusting a URL from the response, verifies the download against
  the catalog's sha256 (a missing hash is refused; the size is checked only
  when the caller supplies a nonzero one), and extracts into a staging dir that
  is moved into place only after the unpacked `manifest.json` agrees on id and
  version.
- `Store/StoreEntitlements` is the account half: an install first asks the
  cloud for a download grant with the active account's token, so an install
  without a linked account is refused here (`sign_in_required`) rather than in
  the UI, and the grant's hash supersedes whatever the caller sent. It also
  serves Manage purchases, joining the account's cloud entitlements with what
  is on disk.
- Routes: `GET /apps-api/store/apps`, `GET /apps-api/store/apps/{appId}`,
  `GET /apps-api/store/media/{**path}`, `POST /apps-api/store/install`,
  `GET /apps-api/store/library` (`Routes/AppRoutes.cs`).

## Activity and integrations

- Screen time, app detection, shortcuts (`Activity/`); installed-game catalog
  for Steam, Epic and Ubisoft with a per-game FPS session recorder (`Games/`,
  `Fps/`, Windows).
- Steam, Discord (Rich Presence), OBS, Twitch (anonymous IRC-over-WS chat
  reader + emote CDN proxy, fanned out on `twitch/chat/{channel}`), and Home
  Assistant (`Integrations/HomeAssistant/`, `GET`/`POST /home-assistant/*`).
- Focus modes (`FocusModes/`, `/api/focus*`, live on the `focus` topic): a
  named mode activates on a trigger (a catalog game's process, OBS
  streaming/recording, or by hand), holds native notifications for release
  afterwards, and defers periodic cloud egress (heartbeat, telemetry flush,
  fleet retries, FPS upload, OTA polling, cloud device reports). An opt-in
  extra puts every Nexus-rendered panel display to sleep. One mode is active
  at a time, by list order.
- Volume mixer (`Audio/AudioMixerService.cs`, `/system/audio/mixer/*`, live on
  `audio/mixer`): per-application levels on the default output, one strip per
  process. Windows only: the Core Audio session walk runs in the user-session
  helper, since Session 0 sees none of the interactive session's audio.
  Levels are remembered by process name and re-applied when the app next
  plays; named presets apply a whole set at once.

## Remote access and pairing

- Local TLS on `:9443` with SPKI-pinned client sessions (the mobile apps and
  the dashboard), 6-digit pair codes with SAS verification, host-side approval
  (the pending request is visible to desktop sockets only, and only the phone
  that submitted the code can confirm it).
- `/pair` and the desktop token are honored only from loopback AND under a
  Host header naming this machine (localhost, an IP literal, or the machine
  name), so a browser page whose domain was DNS-rebound to 127.0.0.1 cannot
  fetch the token.
- A relayed phone session reaches exactly the `.AllowPanel()` routes a LAN
  phone session reaches.
- Relay client (`Relay/`) so the phone panel keeps working away from the LAN;
  the nearest regional relay is picked via the cloud API. An opportunistic
  WebRTC DataChannel direct P2P upgrade (`Rtc/`, STUN only, signaled over the
  relay tunnel via `POST /rtc/offer`) drops the relay hop once a direct path
  exists.
- Every sealed session channel (relay, LAN `/secure-tunnel`, direct; the
  one-shot pair-claim frame stays v1) runs the protocol-v2 in-band rekey: the
  client's first sealed frame is `{"c":"hello2"}`, the host answers with a
  sealed 16-byte nonce, and both switch to a key derived from the session root,
  the client salt and that nonce (`Relay/SealedChannelKeys.cs`), so a recorded
  connection cannot be replayed against a later one. A v1 client is still
  served on the connection key until `SealedChannelKeys.RequireRekey` is
  turned on.
- Webcam (`Webcam/`): the mobile companion streams its camera into an OS
  virtual camera device, with per-OS backends.

## Cloud accounts

Optional Nexus account (email/password). The service holds the tokens and
backs this machine's profile library up to the account (debounced push,
revision-based conflict resolution) and reports device specs, all through
local `/cloud/*` routes (desktop-bearer only, never exposed to a paired
phone). Profiles are keyed per machine, so a sync pass only touches rows this
machine owns; copying config between machines is an explicit import
(`/cloud/profiles/library` to browse, `/cloud/profiles/import` to apply,
`DELETE /cloud/profiles/{installId}/{profileId}` to remove) that overwrites
only the categories the user picks.
Sync payloads carry the shareable categories only: no integration credential
and no machine-scoped state leaves the box. `NEXUS_API_BASE` overrides the API
base URL.

## AI Integration

- An in-process MCP (Model Context Protocol) server, off by default (`Mcp/`,
  tools in `Mcp/Tools/`), lets an AI client read this PC's telemetry,
  diagnostics (health verdict, SMART, GPU throttling, crash incidents, driver
  problems, conflicts, updates) and history (sensors, temperatures, apps,
  screen time, game FPS sessions), and control cooling, lighting and profiles,
  over a dedicated loopback listener speaking Streamable HTTP.
- A bearer token separate from the dashboard's pairing token; per-capability
  consent (telemetry, cooling, lighting, profiles, history) checked live on
  every call; every non-read-only call audited to an append-only binary log
  (`db/ai-history/events.log`). Configured via `/ai/status`, `/ai/config`,
  `/ai/token/rotate` (`Routes/AiRoutes.cs`). User guide:
  https://hellonexus.com/docs/guides/ai/mcp-server.
- Local assistant (dev-tools builds only; a release build compiles out its DI
  registration and routes): a natural-language query bar backed by a
  self-managed [Ollama](https://ollama.com) runtime and a small local model,
  off until the user installs it (`Mcp/Assistant/`). The service downloads the
  official portable Ollama archive over HTTPS, verifies its SHA-256 against
  the release's published `sha256sum.txt`, and supervises `ollama serve` as a
  loopback child. Reusing an Ollama already on the PC is an explicit opt-in
  (`POST /ai/assistant/runtime/use-system`, `AiIntegration.UseSystemOllama`);
  off, the service never probes or adopts a foreign listener, since the
  adopted runtime receives every prompt and drives every tool call. Runtime
  and model data live under the data dir's `assistant/` subfolder; on Windows
  the runtime dir is ACL-locked to SYSTEM + Administrators before anything is
  extracted or launched. A query builds tool definitions from the same
  `McpToolRegistry` the MCP listener uses and runs every tool call through it,
  so the same consent and audit gate applies. Surfaced at `/ai/assistant/*`
  (`Routes/AiAssistantRoutes.cs`) with progress on the `aiAssistant` topic.

## Lifecycle

Windows service install, scheduled-task launcher and system tray; macOS
launchd; a Linux root systemd daemon that adopts the login session for tray
and media. Single instance, self-elevation when needed (`Lifecycle/`). The
user-session helper (`Helper/`) runs the work Session 0 cannot do on Windows:
audio sessions, screen capture, process actions and shortcuts. `Migration/`
imports Nexus 2 personalization and FanControl configurations.
