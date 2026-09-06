# nexus-service

The local Nexus hardware service: one Native-AOT binary per OS (Windows, macOS,
Linux) that talks to the hardware and exposes a REST + WebSocket API on the
user's PC. Everything else in [Nexus](https://hellonexus.com) is a client of
this process: the [`nexus-web`](https://github.com/hello-nexus/nexus-web)
dashboard, the on-device panels, the phone companions, and
[`nexus-overlay`](https://github.com/hello-nexus/nexus-overlay).

## What it does

| Area | Summary |
| --- | --- |
| **Monitoring** | CPU, GPU, RAM, disk, network, fan, temperature, FPS and battery sensors (LibreHardwareMonitor on Windows, IOKit on macOS, sysfs on Linux), 1 Hz history, a timeline of system events, per-drive SMART health. |
| **Cooling** | Fan curves, pump and AIO control across the first-party and vendor hubs the service drives, with calibration and safety limits. |
| **Lighting** | RGB for the whole OpenRGB catalog through a bundled headless child process, plus first-party protocols. Effects engine, screen and audio sync, LAN smart lights (Hue, Nanoleaf, Govee), game sync (Razer Chroma, LightFX and Logitech capture shims, CS2 Game State Integration). |
| **Devices** | Native USB drivers: HYTE (Y70, Q-series, Keeb, CNVS, hubs), Lian Li (Uni Fan, Galahad II, Strimer, wireless), Corsair iCUE LINK and Xeneon Edge, NZXT Kraken, iBUYPOWER (AW5, keyboards, mice), Tryx Panorama, Elgato Stream Deck, Nollie. Firmware updates, and detection of competing vendor software. |
| **Panels** | Pairs and serves the React panel UIs for the Y70 touch panel, Q-series screens and the phone companion, and streams off-screen rendered panels as H.264 to USB display devices. |
| **Apps** | Host for `nexus.app/1` SDK apps (sandboxed Web Worker runtime, sensor bindings) and the cloud app store. |
| **Activity** | Screen time, app detection, installed-game catalog with per-game FPS sessions, focus modes, per-app volume mixer, media sessions, and the Steam, Discord, OBS, Twitch and Home Assistant integrations. |
| **Diagnostics** | SMART/NVMe health, Windows event-log incidents, GPU throttling, pump and fan stall detection, rolled up into per-component health verdicts with a support-bundle export. |
| **Remote** | Pairing (local TLS, SPKI pinning, pair codes), a regional relay for the phone away from the LAN with a WebRTC direct upgrade, and phone-as-webcam. |
| **Cloud** | Optional Nexus account: profile backup and sync, device reporting, fleet telemetry, OTA self-update. Disabled in unofficial builds (see Build). |
| **AI** | In-process MCP (Model Context Protocol) server, off by default, exposing telemetry, diagnostics, history and control behind per-capability consent and an audit log. Dev-tools builds add a local Ollama-backed assistant. |
| **Lifecycle** | Windows service and tray, macOS launchd, Linux systemd daemon. Single instance, self-elevation, migration from Nexus 2 and FanControl. |

Per-subsystem detail (routes, invariants, security notes) is in
[`docs/subsystems.md`](docs/subsystems.md). The generated REST inventory is
[`docs/openapi.json`](docs/openapi.json); WebSocket topics and polling cadence
are in [`docs/network-transport.md`](docs/network-transport.md).

## Ports

| Port | Bind | Purpose |
| --- | --- | --- |
| `9400` | HTTP, loopback | Dashboard and panel transport. The bind address is the first CLI argument. |
| `9443` | HTTPS | Pairing and remote panel surfaces over a locally generated certificate whose SPKI the clients pin. Derived from the HTTP port. |
| `9401` | HTTP, loopback, Windows | Q-series panel tunnel: the host side of the panel's `adb reverse`. Skipped silently if taken. |
| `9420` | HTTP, loopback | MCP server, when AI Integration is enabled. Single endpoint `POST /mcp`, its own bearer token. |
| `6742` | TCP, loopback | OpenRGB SDK server of the headless child process. |
| `11434` | HTTP, loopback | Managed Ollama runtime behind the local assistant (dev-tools builds only). |

## Develop

Prerequisites: .NET 10 SDK. Node.js 22 if you want the embedded dashboard;
the build pulls it in from a sibling `nexus-web` checkout.

```sh
cd ../nexus-web && npm install && cd ../nexus-service   # once, for the web UI

dotnet run              # JIT dev build at http://localhost:9400
dotnet test             # xUnit suite
dotnet build -p:BuildWeb=false   # skip the nexus-web build when iterating on service code
```

- The build runs `npm run build:service` in `../nexus-web` and copies its
  `dist/` into `wwwroot/`.
- OpenRGB binaries under `Bundled/<rid>/openrgb/` are optional in development;
  without them RGB features report as unavailable.
- `dotnet run -- http://localhost:9400` overrides the bind address.

Diagnostic environment variables (on Windows set them machine-scope: the
service runs as LocalSystem and cannot see user-scope variables):

| Variable | Effect |
| --- | --- |
| `NEXUS_DATA_ROOT` | Data directory override on every platform, for pointing a test host at a throwaway store. |
| `NEXUS_OPENRGB_VERBOSITY` | `verbose` or `trace` raises what the OpenRGB child prints into the service log. Applies at launch. |
| `NEXUS_STOP_SETTLE_MS` | How long turning lighting off waits after the final black frame before the OpenRGB child is killed (default 300, max 5000). |
| `NEXUS_DISCORD_PRESENCE_CLIENT_ID` | Discord application used for Rich Presence, to test against a scratch application. |
| `NEXUS_API_BASE`, `NEXUS_CLOUD_API` | Cloud API base override for the account and LED-mapping clients, and for the store catalog proxy respectively. |

## Source layout

```
src/
  Program.cs           # AOT minimal-API host bootstrap
  Routes/              # one file per REST surface
  Sockets/             # multiplexed WebSocket hub + topics
  Auth/  Security/     # pairing tokens, panel/desktop access policy, local HTTPS cert, security headers
  Sensors/             # LibreHardwareMonitor (Windows), IOKit (macOS), sysfs (Linux)
  Monitoring/          # 1 Hz history store, timeline events, broadcaster
  Cooling/             # curve engine, calibration, safety, per-hub cooling providers
  QSeries/             # persisted adb transport to Q-series screens
  Lighting/            # Engine/ effects + canvas, Rgb/ OpenRGB bridge, Zones/, Mappings/, Capture/, Smart/, GameSync/
  Devices/             # device manager, USB detection, per-device handlers, firmware
  Peripherals/         # protocol drivers per vendor (Hyte, LianLi*, Corsair*, Nzxt, Ibp, Tryx, StreamDeck, Keeb, Y70, ...)
  Conflicts/           # competing vendor-software detection, device ownership, opt-in shutdown
  Panel/               # panel pairing, kiosk launch, backgrounds; Streams/ = streamed-panel sessions + transports
  Deck/  Rendering/    # deck action model + headless executor; server-rendered tiles and key images
  Widgets/  Store/     # nexus.app/1 app host and installer; cloud app-store proxy, entitlements
  Gallery/  Media/     # shared image sources; media import + library
  Activity/            # screen time, app detection, audio analysis
  Games/  Fps/         # installed-game catalog, FPS capture and per-game sessions
  FocusModes/  Audio/  # focus modes; per-app volume mixer and audio playback
  Steam/ Discord/ Obs/ Twitch/ Integrations/   # third-party integrations (Integrations/ = Home Assistant)
  Diagnostics/         # event-log monitor, SMART/NVMe, GPU/cooling/memory checks, health model, support bundle
  Relay/  Rtc/         # off-LAN relay client and sealed channels; WebRTC direct transport
  Webcam/  Transfer/   # phone-as-webcam backends; phone-to-PC file transfer inbox
  Discovery/  Net/     # mDNS advertising; local network helpers
  Cloud/  Telemetry/   # account client, profile sync, device reporting; fleet telemetry
  Update/              # OTA self-update engine
  Mcp/                 # MCP server, tool registry, audit store; Assistant/ = local Ollama assistant
  Lifecycle/  Platform/# install/uninstall, service and tray hosting, CLI flags; per-OS shims
  Helper/              # user-session helper process (Windows): audio, displays, input, tray, and other work Session 0 cannot do
  Persistence/         # settings, profiles, atomic JSON files, data paths
  Migration/           # Nexus 2 and FanControl config import
  Benchmarks/          # in-app hardware benchmark runner
  Actions/ Common/ Defaults/ DependencyInjection/ Models/ Notifications/ Plugins/ Serialization/   # shared plumbing
docs/
  subsystems.md        # per-subsystem detail: routes, invariants, security notes
  openapi.json         # generated REST route inventory (see Build)
  network-transport.md # WebSocket topics, polling cadence, reconnect semantics
  ws-topic-rbac.md     # design note on per-topic WebSocket authorization
  shader-benchmark.md  # Q-series shader performance baseline
data/                  # shipped defaults: install defaults, animate templates, OpenRGB device catalog
Bundled/               # per-RID third-party binaries (adb, dfu-util, pawnio, gamesync, bench CLIs), openrgb + ffmpeg added at publish; macos/ linux/ windows/ = first-party helpers, icons, macOS build scripts
installer/             # Windows Inno Setup + web installer, MSIX, Linux tarball packager
tests/
  Nexus.Service.Tests       # xUnit, AOT-safe
  Nexus.Service.Benchmarks  # BenchmarkDotNet hot paths, on-demand
```

## Build

One AOT binary per OS. Windows targets `net10.0-windows10.0.19041.0`; macOS
and Linux target `net10.0`.

```sh
dotnet publish -c Release -r win-x64   -o publish-win     # cross-compiles from macOS too
dotnet publish -c Release -r osx-arm64 -o publish-mac     # AOT is mandatory here; never pass -p:PublishAot=false
dotnet publish -c Release -r linux-x64 -o publish-linux
```

- A publish fails loudly without the OpenRGB binaries under
  `Bundled/<rid>/openrgb/`. Build them from
  [`nexus-rgb`](https://github.com/hello-nexus/nexus-rgb) first; the csproj
  error spells out the commands.
- The bundled ffmpeg is stock upstream ffmpeg with a minimal LGPL-only
  configuration, compiled by `scripts/build-ffmpeg-minimal.sh`. It is optional
  at build time: `bash scripts/fetch-ffmpeg.sh all` (or `mac | win | linux`)
  produces it once per RID.
- Device firmware images are vendor files kept outside this repository. The
  csproj embeds them from `NEXUS_FIRMWARE_DIR` (or `-p:NexusFirmwareDir=`), a
  gitignored `data/firmware/`, or a sibling `firmware/` directory when one
  exists. A public clone builds with an empty firmware catalog; an official
  publish (one carrying the client token) fails without the images.
- `-p:DevTools=true` compiles in internal tooling (the local AI assistant,
  firmware downgrade paths, D213 panel discovery, the simulated Nexus 2
  install). Distribution builds leave it unset.
- `NEXUS_CLIENT_TOKEN` (or `-p:NexusClientToken=`) is the build credential
  minted by release CI, with `~/.nexus-build/client-token` as the local
  fallback. Its presence defines `OFFICIAL_BUILD`, which wires up the cloud
  account, profile sync, relay, fleet telemetry and the OTA updater. A build
  without it, which is every public clone, is local-only and dials none of the
  hosted services. `NEXUS_POSTHOG_KEY` is injected the same way.

### Route inventory

`docs/openapi.json` is generated from the live route registration. Regenerate
it whenever a route is added, removed or renamed:

```sh
dotnet run -- --emit-openapi docs/openapi.json
```

The flag starts the host only far enough to register endpoints. A `DEBUG`
build also serves the document at `/openapi/v1.json`. The committed copy is
the release flavour; dev-tools-only routes appear only when regenerated with
`-p:DevTools=true`.

## Installers

- **Windows**: `powershell -File installer/build-installer.ps1` wraps the
  publish output into `Nexus-Setup.exe`. `-Bootstrap` instead builds the two
  payload-free web installers (`Nexus-Installer.exe`, `Nexus-Installer-Beta.exe`)
  that hellonexus.com hands out; they are built once, not per release. See
  `installer/README.md`.
- **Linux**: `installer/linux/package.sh publish-linux` produces a tarball;
  its `install.sh` installs a root systemd daemon to `/opt/nexus`. See
  `installer/linux/README.md`.
- **macOS**: `Bundled/macos/build-app.sh` wraps the publish output into
  `Nexus.app`; `Bundled/macos/sign-notarize.sh` deep-signs it, packages
  `Nexus.dmg`, notarizes and staples. `NEXUS_SKIP_CAMERA_EXTENSION=1` omits the
  camera system extension (CI cannot provision it headlessly).

## Test

```sh
dotnet test
```

The suite is AOT-safe. Hardware drivers sit behind provider interfaces with
fake implementations; allocation-budget tests keep the per-frame hot paths
zero-alloc. `tests/Nexus.Service.Benchmarks` is a BenchmarkDotNet project for
those hot paths, outside the solution and the publish:

```sh
dotnet run -c Release -p:BuildWeb=false --project tests/Nexus.Service.Benchmarks -- --filter '*'
```

## Releases

Installers are published as GitHub Releases on
[`hello-nexus/nexus`](https://github.com/hello-nexus/nexus) under semver tags:
`Nexus-Setup.exe`, `Nexus.dmg`, `Nexus-Linux-x64.tar.gz`, plus a `SHA256SUMS`
asset the OTA engine verifies against before installing. The version is the
`VERSION` file at the repo root; the build stamps it into `BuildInfo.Version`.

## Third-party

OpenRGB (GPLv2) ships as a separate child process, source at
[`hello-nexus/openrgb-headless`](https://github.com/hello-nexus/openrgb-headless).
Every bundled component (LibreHardwareMonitor, PawnIO, dfu-util, ffmpeg, ...)
is listed with its license in [`THIRD-PARTY.md`](THIRD-PARTY.md).

## License

`nexus-service` is licensed under the GNU Affero General Public License v3.0;
see [`LICENSE`](LICENSE). Bundled third-party components keep their own
licenses.

Copyright (C) 2026 Hello Nexus
