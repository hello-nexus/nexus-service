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
| **Cooling** | Fan curves, pump and AIO control across the first-party and vendor hubs the service drives, with calibration, safety limits and a CPU thermal guard (on by default) that raises fans near the part's own temperature limit and repairs curve configs that can stall them. |
| **Lighting** | RGB for the whole OpenRGB catalog through a bundled headless child process, plus first-party protocols. Effects engine, screen and audio sync, key-press reactions on per-key keyboards, LAN smart lights (Hue, Nanoleaf, Govee). |
| **Devices** | Native USB drivers: HYTE (Y70, Q-series, Keeb, CNVS, hubs), Lian Li (Uni Fan, Galahad II, HydroShift LCD, HydroShift II, Strimer, wireless, TL and SL-INF LCD Flex fans), Corsair iCUE LINK, Xeneon Edge and Galleon K100 SD (screen and dials), NZXT Kraken and RGB & Fan Controller (fans), iBUYPOWER (AW5, keyboards, mice), Tryx Panorama, Elgato Stream Deck (keys, dials, touch strip), Nollie, Asiahorse Umbra V2. Firmware updates, and detection of competing vendor software. |
| **Panels** | Pairs and serves the React panel UIs for the Y70 touch panel, Q-series screens and the phone companion, and streams off-screen rendered panels (H.264 or raw frames) to USB display devices on Windows and macOS. |
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
| `NEXUS_LED_SPACE` | Windows only. `0` closes the LED-space look-ahead window path so every window renders through the canvas path. |
| `NEXUS_D3D_PREVENT_THREADING` | Windows only. `1` creates the ANGLE renderer's D3D11 device with the driver's internal threading off. |
| `NEXUS_DISABLE_DDC` | Linux only. `1` skips DDC/CI display brightness probing over the I2C buses. |
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
  Cooling/             # curve engine, thermal guard, calibration, safety, per-hub cooling providers
  QSeries/             # persisted adb transport to Q-series screens
  Lighting/            # Engine/ effects + canvas, Rgb/ OpenRGB bridge, Zones/, Mappings/, Capture/, Smart/, Scene/ 3D placement, KeyReactive/ key-press reactions
  Devices/             # device manager, USB detection, per-device handlers, firmware
  Peripherals/         # protocol drivers per vendor (Hyte, LianLi*, Corsair*, Nzxt, Ibp, Tryx, StreamDeck, Keeb, Y70, ...)
  Conflicts/           # competing vendor-software detection, device ownership, opt-in shutdown
  Panel/               # panel pairing, kiosk launch, backgrounds; Streams/ = streamed-panel sessions + transports
  Sentry/              # lock alert: armed on a locked PC, the first input pushes to paired phones through the cloud API (reads no input itself)
  Deck/  Rendering/    # deck action model + headless executor; server-rendered tiles and key images
  Hotkeys/             # global hotkeys: bindings, OS registration status, firing; registered with the OS only (Carbon on macOS, the user helper's RegisterHotKey on Windows), never by capturing keystrokes
  Widgets/  Store/     # nexus.app/1 app host and installer; cloud app-store proxy, entitlements
  Gallery/  Media/     # shared image/video sources; media import + library
  Activity/            # screen time, app detection, audio analysis
  Games/  Fps/         # installed-game catalog, FPS capture and per-game sessions
  FocusModes/  Audio/  # focus modes; per-app volume mixer and audio playback
  Steam/ Discord/ Obs/ Twitch/ Klipy/ Integrations/   # third-party integrations (Klipy/ = GIF catalog, Integrations/ = Home Assistant)
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
data/                  # shipped defaults: install defaults, animate templates, OpenRGB device catalog,
                       # built-in ARGB product LED mappings (embedded; the picker works offline)
Bundled/               # per-RID third-party binaries (adb, dfu-util, pawnio, bench CLIs, skia), openrgb + ffmpeg added at publish; macos/ linux/ windows/ = first-party helpers, icons, macOS build scripts
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
- `Bundled/win-x64/angle/` holds the ANGLE `libEGL.dll` and `libGLESv2.dll`
  (a Direct3D 11 only build, recipe in `BUILD.md`; licenses in
  `ANGLE-LICENSE.txt`) and publishes to `<publish>/angle/`. They are the default
  Windows lighting renderer (`Lighting.RenderBackend` = `angle`, OpenGL ES 3 on
  Direct3D 11); `wgl` remains as a fallback and win-arm64 uses `wgl`.
  Setting `Lighting.RenderGpu` to `software` (ANGLE only) renders shaders on the
  CPU through ANGLE WARP, with no hardware probe; it applies after a restart.
- `Bundled/<rid>/skia/libSkiaSharp` is a raster-only Skia build with HarfBuzz
  and libjpeg-turbo linked in, committed per RID and rebuilt with
  `Bundled/skia/build.sh` / `build.ps1` (see `Bundled/skia/NOTES.md`). It
  exports only `Bundled/skia/exports.txt`, and an AOT publish fails if the
  service starts calling a SkiaSharp or HarfBuzzSharp API outside that list.
- The Windows virtual display driver behind secondary-monitor mode is optional
  at build time: `Bundled/windows/nexus-vdd/build.ps1` builds it into
  `Bundled/win-x64/vdd/` with the VS Build Tools (it downloads the WDK package
  itself). Without it the setting is not offered.
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
  hosted services. `NEXUS_POSTHOG_KEY` and `NEXUS_KLIPY_KEY` (the GIF picker's
  api.klipy.com app key) are injected the same way.

### Route inventory

`docs/openapi.json` is generated from the live route registration. Regenerate
it whenever a route is added, removed or renamed:

```sh
dotnet run -- --emit-openapi docs/openapi.json
```

The flag starts the host only far enough to register endpoints. The
generator exists only in `Debug` builds (the default for `dotnet run`); a
release build neither accepts `--emit-openapi` nor ships the OpenAPI packages.
A `DEBUG` build also serves the document at `/openapi/v1.json`. The committed copy is
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
  camera system extension and its helper and activator (CI cannot provision
  the extension headlessly; the other two do nothing without it). For a local
  build, `Bundled/macos/sign-local.sh <Nexus.app> <identity>` seals the bundle
  with a stable identity so macOS privacy grants (Input Monitoring for touch
  routing, Screen Recording) survive rebuilds.

## Test

```sh
dotnet test
```

The suite is AOT-safe. Hardware drivers sit behind provider interfaces with
fake implementations; allocation-budget tests keep the per-frame hot paths
zero-alloc. Every pull request runs this build and suite in CI
(`.github/workflows/ci.yml`); see `CONTRIBUTING.md`. `tests/Nexus.Service.Benchmarks` is a BenchmarkDotNet project for
those hot paths, outside the solution and the publish:

```sh
dotnet run -c Release -p:BuildWeb=false --project tests/Nexus.Service.Benchmarks -- --filter '*'
```

On Windows, `Nexus.exe --lighting-bench <out.json> [--backends angle,wgl]
[--adapters integrated,dedicated,warp] [--seconds N] [--effects a,b]` renders every built-in
shader effect at the 30 Hz cadence in one child process per backend and
adapter, and writes timing, CPU, memory and output-fingerprint results as JSON
plus a summary table, with timestamped `.json` and `.txt` copies in the logs
directory. `--section ledspace|gate|d3dflag` replaces the standard run with a
probe of the look-ahead window path (LED-space probe, exactness gate, D3D11
threading flag).

Look-ahead windows render through one LED-space draw per window once an
exactness gate has proven a shader byte-identical to the canvas render on the
running GPU; any other case uses the canvas path. The window path is
Windows-only, and `NEXUS_LED_SPACE=0` closes it. `NEXUS_D3D_PREVENT_THREADING=1`
(Windows-only) creates the ANGLE D3D11 device with the driver's internal
threading off (off by default).

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
