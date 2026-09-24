# Installer

Builds `Nexus-Setup.exe`, the single signed executable end users download
to install Nexus. Wraps the AOT publish output in an Inno Setup 6 wizard
that lays files into `C:\Program Files\Nexus\`, downloads and installs the
Microsoft Edge WebView2 Runtime when the PC has none (an extra wizard page
says so; silent installs do it without one), installs the PawnIO kernel
driver, registers and starts the `NexusService` Windows service (LocalSystem,
automatic start), drops a searchable Start Menu shortcut (and, if the
directory-page checkbox is left ticked, a desktop shortcut), and opens the
dashboard. Uninstall offers to keep or remove the per-machine data under
`%ProgramData%\Nexus\`.

This is **not part of the regular AOT publish cycle**. The dev loop stays:

```
dotnet publish ...
sc start NexusService
```

The installer is built explicitly when shipping a release.

## One-time setup (PC only)

```powershell
winget install JRSoftware.InnoSetup
```

Inno Setup 6 lands at `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`.

## Building

The installer wraps an existing AOT publish output. Under the current
deploy flow the service publishes **directly into `C:\Program Files\Nexus\`**
(see the `build-pc` runbook), so point the script there explicitly - the
script's legacy `..\..\aot` default no longer exists:

```powershell
powershell -File installer\build-installer.ps1 -PublishDir "$env:ProgramFiles\Nexus"
```

Output: `installer\output\Nexus-Setup.exe` plus a `SHA256SUMS` next to it,
with copies of both dropped at `%USERPROFILE%\nexus\`.
Releases are published as a semver `vX.Y.Z` tag (matching `VERSION`) on
`hello-nexus/nexus` via `gh release create`, and must carry the
`SHA256SUMS` asset - the OTA updater requires the published hash to auto-stage a
release.

Optional flags:
- `-PublishDir <path>`  the AOT publish dir to wrap (pass `$env:ProgramFiles\Nexus`)
- `-OpenOutput`         open Explorer at the resulting file
- `-Sign`               Authenticode-sign every PE in the payload, the embedded
  uninstaller, and the installer via Azure Artifact Signing (see Code signing
  below); omit for a fast unsigned dev build
- `-SignToolPath` / `-DlibPath`  override the auto-probed signtool / dlib paths
- `-Bootstrap`          build the two Windows **web installers** instead (see
  below); needs no publish dir
- `-BootstrapBaseUrl`   point the web installers at a different site for a
  local end-to-end test (default `https://hellonexus.com`)

### Keep it lean

The installer wraps the publish tree **verbatim**, so anything stray in
`C:\Program Files\Nexus\` ships inside it. The installer is lzma2/max
compressed, so an unexpected multi-MB size jump vs the previous release is a
signal worth checking - diff against the last `Nexus-Setup.exe`. A jump can be
intended (a new bundled feature) or junk; verify which.

- **Intended payload:** the firmware flasher binaries (`dfu-util\`,
  `dfu-driver\`) ship in release - end users flash firmware upgrades
  through them, and they account for the step up in installer size when the
  firmware flasher feature landed. They are *not* junk. `DevTools`
  only unlocks the brick-risky cross-variant / downgrade paths in
  `FirmwareFlasher.cs`, not flashing itself, so don't gate the binaries on it.
- **Actual junk to strip:** delete any `test-results\` (a `dotnet test` runner
  artifact the Web SDK content glob pulls into publish) and confirm
  `wwwroot\assets\` holds only the current build's hashed bundles (a skipped
  wwwroot wipe accumulates every prior build's dead `*.js`).

## Web installers (bootstrap)

`hellonexus.com`'s Windows download buttons hand out `Nexus-Installer.exe`
(stable) and `Nexus-Installer-Beta.exe`, not `Nexus-Setup.exe`. Both compile
from `bootstrap\Nexus-Bootstrap.iss` with `-Bootstrap`:

```powershell
powershell -File installer\build-installer.ps1 -Bootstrap [-Sign]
```

They carry no payload. On Install they fetch the site's
`/download/offline/sha256sums?channel=<stable|beta>`, then
`/download/offline/windows?channel=<...>` pinned to the hash from that list
(the site resolves both to the matching GitHub release), and run the
downloaded `Nexus-Setup.exe` (its own UAC prompt, its own wizard). A silent
stub run (`Nexus-Installer.exe /VERYSILENT`) passes `/VERYSILENT` through and
exits non-zero on failure: 1 if the download or the hash check fails, 3 if
the payload fails, is cancelled, or the UAC prompt is declined. The payload is downloaded without Mark-of-the-Web, the same as the
in-app OTA, so it is not subject to a per-release SmartScreen check - which is
the point: SmartScreen reputation is keyed on the downloaded file's hash, and
a release every day or two never lets `Nexus-Setup.exe` accrue any. The web
installers are built and signed **once** by the release CI's `web_installers`
dispatch input and published as release assets on
`github.com/hello-nexus/nexus-installer` (one release per `StubVersion`, so
`releases/latest/download/<name>` is a fixed URL), then re-used unchanged
across releases. **Rebuild them only when the bootstrap
script changes** (bump its `StubVersion`); every rebuild is a new hash that
starts from zero. `-Bootstrap` writes a `<name>.sha256` next to each output
(same `<hash>  <name>` shape as `SHA256SUMS`).

`Nexus-Setup.exe` keeps shipping on every release exactly as before: it is what
the OTA downloads and what system integrators install from a USB stick, and
the site links it as the "offline installer".

## Files

- `Nexus.iss` - Inno Setup script (wizard config, install steps, uninstall)
- `bootstrap\Nexus-Bootstrap.iss` - the web installers (both channels from one
  script via `/DChannel=`), see above
- `logo-small.bmp` - 58x58 logo shown top-right of the directory page; regenerated
  from `..\icon.ico` if you change the brand mark
- `build-installer.ps1` - the build entry point, also strips macOS AppleDouble
  files from the publish dir (they slip in via scp from Mac dev machines)
- `signing-metadata.json` - Artifact Signing account/profile/endpoint (non-secret)
- `output\` - compiler output (gitignored)

## Install scope

Machine-scope only. Files go to `C:\Program Files\Nexus` (`{commonpf64}`) and
`NexusService` runs as **LocalSystem**, shared by every account on the PC, so
there is no per-user option (the previous schtask-era per-user install is gone).
The wizard always requires UAC: both the PawnIO kernel driver and the service
registration are machine-wide.

The service registers with **automatic** start, so it runs from boot for all
users with no per-user autostart entry. The directory page is normally the
only wizard page; it carries a "Create a desktop shortcut" checkbox (ticked by
default). A PC with no WebView2 Runtime gets one more page announcing the
runtime download.

## Code signing

Pass `-Sign` to Authenticode-sign the release via **Azure Artifact Signing**
(formerly "Trusted Signing"), under the **American Future Technology Corp**
publisher identity. The script signs **every `.exe`/`.dll` in the payload**
that is not already validly signed - bundled third-party binaries included -
then hard-fails if any PE remains unsigned. Smart App Control (Microsoft's
SACVT preinstall validation) requires every PE on the image to chain to a
trusted root regardless of who launches it, so "launched only by the service"
binaries like `OpenRGB-headless.exe` are signed too. Files that already carry
a valid publisher signature (`adb.exe` - Google, `diskspd.exe` - Microsoft)
are left untouched, as is `PawnIO.sys` (kernel-mode signed by its author).
Inno then signs the embedded uninstaller (`SignedUninstaller`, the extracted
`unins000.exe` is a PE on the image too) and `Nexus-Setup.exe` itself, before
the `SHA256SUMS` hash, so the published hash (and the OTA integrity check)
covers the signed bytes.

Account/profile/endpoint live in `signing-metadata.json` (non-secret). The signer
authenticates with `DefaultAzureCredential`:

- **CI** (`hello-nexus/nexus` `.github/workflows/build.yml`): `azure/login` via
  OIDC federated credentials (no stored secret), then `build-installer.ps1 -Sign`
  with `-DlibPath` pointing at the `Microsoft.Trusted.Signing.Client` NuGet
  package's `bin/x64` dlib (signtool comes from the runner's Windows SDK).
- **Local / build-pc**: install the client tools once and provide the
  service-principal env vars, then run with `-Sign`:

  ```powershell
  winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
  $env:AZURE_TENANT_ID="..."; $env:AZURE_CLIENT_ID="..."; $env:AZURE_CLIENT_SECRET="..."
  powershell -File installer\build-installer.ps1 -PublishDir "$env:ProgramFiles\Nexus" -Sign
  ```

  Signing rewrites files in place, so a `-Sign` run against the live
  `Program Files` tree needs every process holding a payload file closed
  first - including the detached adb daemon, which outlives `net stop
  NexusService` and keeps `tools\adb\` locked (kill the `adb.exe` whose
  image path is the bundled copy, same as the installer's own
  pre-install stop logic).

The client tools bundle a compatible signtool + `Azure.CodeSigning.Dlib.dll`; the
dlib does **not** work with the 10.0.20348 Windows SDK. Certs are valid only 72h,
so timestamping (`http://timestamp.acs.microsoft.com`, baked into the script) is
mandatory: it keeps a signature valid after the cert rotates daily.

## Microsoft Store (MSIX)

`msix/` packages Nexus as a full-trust MSIX for Microsoft Store distribution
(the internal Windows Store distribution plan's Option A: a real launchable
app, not a bare downloader, so it clears Store review while still reusing this
installer wholesale). The package is a tiny launcher shell,
`NexusStoreLauncher.exe`: if `Nexus.exe` is already installed it opens the
dashboard; otherwise it silently runs the bundled `Nexus-Setup.exe` (elevating
via `ShellExecute`, since the setup exe carries its own elevation manifest)
and then opens the dashboard itself, because a silent Inno run skips its own
`--open-app` step. It carries no service/driver logic of its own - everything
above in this README still owns that.

It deliberately does not pass `/DESKTOPICON=1`, so a Store install leaves the
desktop alone: MSIX has no desktop-shortcut mechanism, only a Start entry, and
Store-installed apps are expected to behave that way. The web installer passes
the switch because a website download carries the opposite expectation.

Build (Windows only, after building `Nexus-Setup.exe` per "Building" above).
There are two distinct modes, and they take a different `-Publisher`:

```powershell
# Store build - stays unsigned, Store re-signs on submission.
powershell -File installer\msix\build-msix.ps1 `
    -IdentityName "HelloNexus.HelloNexus" `
    -Publisher "CN=62485AE2-77C6-4F70-A924-D5BA99963014" `
    -PublisherDisplayName "Hello Nexus"

# Local sideload verification only.
powershell -File installer\msix\build-msix.ps1 -Sign `
    -IdentityName "HelloNexus.HelloNexus" `
    -Publisher "<Azure Artifact Signing cert Subject>" `
    -PublisherDisplayName "Hello Nexus"
```

Output: `installer\msix\output\Nexus-<payload version>-<arch>.msix`, e.g.
`Nexus-3.0.10-beta.4-x64.msix`. The file name carries the payload's full
version (prerelease suffix included) and the architecture, neither of which the
4-part package version can express; Partner Center never reads it. A build that
passes `-Version`, or whose payload reports a product version disagreeing with
its file version, is named for the package version instead.

### Identity tokens

`AppxManifest.template.xml` carries five placeholders the build script
substitutes: `{{IDENTITY_NAME}}`, `{{PUBLISHER}}`, `{{PUBLISHER_DISPLAY_NAME}}`,
`{{VERSION}}`, `{{ARCH}}`. `{{IDENTITY_NAME}}` and `{{PUBLISHER_DISPLAY_NAME}}`
come from the app identity reserved in Partner Center and are the real values
shown in both build commands above (they are public: any installed package
exposes them). `{{PUBLISHER}}` differs by mode - the Partner Center id for a
Store build, the signing cert subject for `-Sign` - see Signing below. The
script's own parameter defaults are deliberately invalid placeholders so an
un-overridden package is obviously wrong rather than silently plausible, and
`-Sign` refuses to run against them. `{{ARCH}}` derives from `-Rid` (`win-x64`
default, `win-arm64` allowed); cross-arch AOT publish is not dependable, so the
RID must match the machine running the build. These substituted values are
XML-escaped before they reach the manifest, since they are operator-supplied
strings, not build-time constants.
`{{VERSION}}` is read off the **bundled `Nexus-Setup.exe`**, not the repo's
`VERSION` file. CI stamps `VERSION` from its workflow input at build time and
never commits it back, so a checkout routinely sits several patches behind the
released version, and a package claiming a version its own payload does not
carry misreports itself in the Store. `build-installer.ps1` stamps the
installer's `VersionInfoVersion` as `<numeric>.0`, which is already the exact
shape MSIX needs: four numeric parts with a zero revision (Partner Center
rejects a nonzero fourth part, and MSIX has no prerelease-suffix concept, so a
beta channel would be a separate Store flight). `-Version` overrides that, and is
validated the same way, but it makes the package claim a version its payload
does not carry - and since Store package versions must strictly increase, a
resubmission needs a higher one, which guarantees the mismatch. Prefer
rebuilding the payload at the version you want to ship. The build fails rather
than guessing if the payload carries no version, or reports the `0.0.0`
placeholder from an installer built without `build-installer.ps1`.

The two `DisplayName` values in the manifest are **not** placeholders and are
not substituted. They read `Hello Nexus`, the reserved Store name; `Nexus`
belongs to another publisher, and Partner Center rejects any package whose name
is not one of the account's reserved names. The unpackaged suite the shell
installs is unaffected and keeps its own `Nexus` branding from `Nexus.iss`.

### Signing

A Store submission and a local `-Sign` sideload test use two different
publisher identities and must not be confused. `signtool` enforces that the
package's `Identity/@Publisher` byte-matches the signing certificate's
Subject (`APPX_E_PUBLISHER_MISMATCH` otherwise):

- **Store build** (no `-Sign`): `-Publisher` is the Partner Center reserved
  app identity, typically a bare `CN=<GUID>` for an individual/unverified
  publisher account. The package ships unsigned; Store re-signs it on
  submission, so it is never what end users actually install.
- **`-Sign`** (local sideload verification only): reuses the Azure Artifact
  Signing setup from "Code signing" above (same `signing-metadata.json`, same
  signtool/dlib probing), so `-Publisher` must be that certificate's actual
  Subject instead - read it off a previously signed payload with
  `(Get-AuthenticodeSignature .\Nexus-Setup.exe).SignerCertificate.Subject`.

The script refuses `-Sign` when `-Publisher` looks like a bare Partner Center
`CN=<GUID>` id, since that is never a valid signing subject; pass
`-SkipPublisherModeCheck` if this heuristic misfires.

### Write virtualization: opt-out removed 2026-09-03

The manifest used to disable MSIX write virtualization
(`desktop6:FileSystemWriteVirtualization` / `RegistryWriteVirtualization` plus
the `unvirtualizedResources` restricted capability), on the theory that
`Nexus.exe` and `Nexus-Setup.exe` launched by the packaged launcher would
inherit its package identity and have their writes redirected into the
package's private store. All three are gone, because the redirection they
guarded against cannot reach anything this package starts, and the capability
was a standing certification risk: Microsoft scopes `unvirtualizedResources` to
"certain types of desktop PC games ... published by Microsoft and our
partners".

**The argument is about scope, not about identity.** Write virtualization
redirects only writes under `%USERPROFILE%\AppData` and `HKCU`
(learn.microsoft.com/windows/msix/desktop/flexible-virtualization). Neither is
in the launcher's path:

- The only descendant the launcher creates is `Nexus.exe --open-app`
  (`CommandLineEntry`), whose tree writes `%ProgramData%\Nexus\DashboardEdge`
  (`TrayIcon`) - outside the redirected scope.
- `Nexus-Setup.exe` writes Program Files, `%ProgramData%`, and HKLM. Also
  outside it.
- The suite's HKCU writers - the Run key (`WindowsStartupProvider`), the
  `nexus:` protocol handler (`ProtocolHandler`), and the AUMID registration
  (`ToastNotifications`) - all run in the daemon or the tray helper, which the
  service starts through SCM or Task Scheduler. Neither is ever a descendant of
  the launcher.

That holds however package identity propagates, which is what makes it the
load-bearing argument.

**What was and was not measured on T1** (Windows 11 Home, self-signed sideload
of a `runFullTrust`-only package):

- Measured: the capability-free package installs, reports `caps=runFullTrust`,
  and its launcher activates without error; `makeappx` packs the manifest under
  the real Store identity.
- **Not measured: whether a child of a Store-activated packaged app inherits
  package identity.** An attempt using `Invoke-CommandInDesktopPackage` proved
  nothing - that cmdlet creates children *without* context by default, which a
  control run confirmed (default: child `NO_PACKAGE`; `-PreventBreakaway`:
  child carries the package identity). A second attempt polling for the real
  launcher's children caught neither the launcher nor any child, both being far
  too short-lived for a 100ms poll. Catching them needs ETW process-start
  tracing.

**Residual risk, small and named:** if a launcher descendant does inherit
identity *and* someone later adds an `%APPDATA%` or HKCU write to the
`--open-app` path, that write would silently redirect into the package store.
Nothing on that path writes to either location today.

### Assets

`Assets/` holds the required tile/logo PNGs (`Square44x44Logo`,
`Square150x150Logo`, `Wide310x150Logo`, `StoreLogo`, each with a scale-200
variant), generated from the existing
`Bundled\macos\Assets.xcassets\AppIcon.appiconset\icon_512x512@2x.png` master.
The wide logo is the square mark centered on a transparent canvas, not a
designed wordmark lockup - treat it as a placeholder pending a final art pass.
