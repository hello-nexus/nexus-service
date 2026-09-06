# Builds Nexus-Setup.exe from an existing AOT publish output.
#
# This is NOT part of the regular AOT publish cycle. Run it explicitly when you
# want a fresh installer (e.g. before a release). The dev workflow stays:
#   dotnet publish -c Release -r win-x64 -o <publish-dir>
#   schtasks /Run /TN NexusService
#
# Usage (from the PC, in any shell):
#   powershell -File installer\build-installer.ps1
#
# By default the script looks for the publish output at ..\..\aot relative to
# this script (i.e. <repo>/aot if the layout is <repo>/service/installer).
# Override with -PublishDir for a custom layout.
#
# Optional: -PublishDir <path>  override the AOT publish dir
#           -OpenOutput          reveal the resulting Setup.exe in Explorer
#           -Bootstrap           build bootstrap\Nexus-Bootstrap.iss instead:
#                                the two web installers (stable + beta) that
#                                download Nexus-Setup.exe at install time. Needs
#                                no publish dir. See bootstrap\Nexus-Bootstrap.iss
#                                for why these are rebuilt only when that
#                                script changes.
#           -BootstrapBaseUrl    override the site the web installers fetch
#                                from (local end-to-end testing only)

param(
    [string]$PublishDir = "",
    [switch]$OpenOutput,
    [switch]$Sign,
    [string]$SignToolPath = "",
    [string]$DlibPath = "",
    [switch]$Bootstrap,
    [string]$BootstrapBaseUrl = ""
)

if ([string]::IsNullOrEmpty($PublishDir)) {
    $scriptDirEarly = Split-Path -Parent $MyInvocation.MyCommand.Path
    $PublishDir = (Resolve-Path (Join-Path $scriptDirEarly "..\..\aot") -ErrorAction SilentlyContinue).Path
    if ([string]::IsNullOrEmpty($PublishDir)) {
        $PublishDir = Join-Path $scriptDirEarly "..\..\aot"
    }
}

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$iss = Join-Path $scriptDir "Nexus.iss"

# Artifact Signing (Azure, formerly "Trusted Signing"). Enabled with -Sign.
# signtool + the Artifact Signing dlib sign each PE against the nexus-public
# certificate profile (account/profile/endpoint in signing-metadata.json).
# Auth is ambient DefaultAzureCredential: the service-principal env vars
# AZURE_TENANT_ID/AZURE_CLIENT_ID/AZURE_CLIENT_SECRET (EnvironmentCredential)
# locally, or `az login` (OIDC, AzureCliCredential) in CI. Certs are valid only
# 72h, so /tr timestamping is mandatory: it keeps a signature valid after the
# cert rotates.
$signMetadata = Join-Path $scriptDir "signing-metadata.json"
# Certs rotate daily (72h validity), so timestamping is mandatory; shared by
# the sweep (Invoke-NexusSigning) and the ISCC /S sign-tool definition.
$timestampUrl = "http://timestamp.acs.microsoft.com"

function Resolve-SignTool {
    if ($SignToolPath -and (Test-Path $SignToolPath)) { return $SignToolPath }
    # The dlib does not work with the 10.0.20348 Windows SDK; take the newest
    # other x64 signtool from the installed Windows Kits.
    $kitRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    )
    $cands = foreach ($r in $kitRoots) {
        if (Test-Path $r) {
            Get-ChildItem $r -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^10\.\d+\.\d+\.\d+$' -and $_.Name -notlike '10.0.20348.*' } |
                ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
                Where-Object { Test-Path $_ }
        }
    }
    $best = $cands | Sort-Object {
        [version](Split-Path (Split-Path (Split-Path $_ -Parent) -Parent) -Leaf)
    } -Descending | Select-Object -First 1
    if (-not $best) {
        throw "signtool.exe (Windows SDK >= 10.0.22000, not 20348) not found. Install the Windows SDK or pass -SignToolPath."
    }
    return $best
}

function Resolve-Dlib {
    if ($DlibPath -and (Test-Path $DlibPath)) { return $DlibPath }
    $roots = @(
        "$env:ProgramFiles\Microsoft Artifact Signing Client Tools",
        "${env:ProgramFiles(x86)}\Microsoft Artifact Signing Client Tools",
        (Join-Path $scriptDir "signing-tools")
    )
    foreach ($r in $roots) {
        if (Test-Path $r) {
            $hits = @(Get-ChildItem $r -Recurse -Filter "Azure.CodeSigning.Dlib.dll" -ErrorAction SilentlyContinue)
            if ($hits.Count -gt 0) {
                # Prefer the x64 dlib (this signtool is x64); fall back to whatever
                # the install layout provides rather than throwing.
                $x64 = $hits | Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
                if ($x64) { return $x64.FullName }
                return $hits[0].FullName
            }
        }
    }
    throw "Azure.CodeSigning.Dlib.dll (x64) not found. Install Microsoft.Azure.ArtifactSigningClientTools (winget) or pass -DlibPath."
}

# Signing retries: the service fails a small fraction of operations outright.
$signMaxAttempts = 4
$signRetryBaseSeconds = 5

# ISCC arguments that make Inno sign what it emits: Setup.exe and, where the
# script enables it, the embedded uninstaller (SignedUninstaller in Nexus.iss -
# the extracted unins000.exe is a PE on the installed image, so Smart App
# Control checks it like everything else). $q is Inno's double-quote escape;
# $f arrives pre-quoted from ISCC, so it must NOT get $q wrapping.
function Get-IsccSignArgs {
    if (-not (Test-Path $signMetadata)) { throw "Missing signing metadata: $signMetadata" }
    $st = Resolve-SignTool
    $dlib = Resolve-Dlib
    return @(
        ('/Snexussign=$q' + $st + '$q sign /fd SHA256 /tr ' + $timestampUrl + ' /td SHA256 /dlib $q' + $dlib + '$q /dmdf $q' + $signMetadata + '$q $f'),
        "/DEnableSigning"
    )
}

function Invoke-NexusSigning {
    param([string[]]$Files)
    $targets = @($Files | Where-Object { $_ -and (Test-Path $_) })
    if ($targets.Count -eq 0) { return }
    if (-not (Test-Path $signMetadata)) { throw "Missing signing metadata: $signMetadata" }
    $st = Resolve-SignTool
    $dlib = Resolve-Dlib
    Write-Host "Signing $($targets.Count) file(s): $st"
    $targets | ForEach-Object { Write-Host "  sign: $_" }
    # Azure Artifact Signing and the RFC3161 timestamp server are both network
    # services: either can return a per-operation failure (HTTP 200 with
    # status "Failed", surfacing as SignerSign() 0x80004005) that succeeds on a
    # retry. One flaked file used to fail the whole release build.
    foreach ($f in $targets) {
        for ($attempt = 1; ; $attempt++) {
            & $st sign /v /fd SHA256 /tr $timestampUrl /td SHA256 `
                /dlib $dlib /dmdf $signMetadata $f
            if ($LASTEXITCODE -eq 0) { break }
            if ($attempt -ge $signMaxAttempts) {
                throw "signtool failed (exit $LASTEXITCODE) on $f after $attempt attempt(s)"
            }
            $delay = $signRetryBaseSeconds * $attempt
            Write-Host "  sign attempt $attempt failed on $f; retrying in ${delay}s"
            Start-Sleep -Seconds $delay
        }
    }
}

# Probe standard Inno Setup 6 install locations: per-user (winget default),
# then both Program Files variants (machine-wide / Chocolatey on CI).
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) { throw "Inno Setup 6 not installed. Run: winget install JRSoftware.InnoSetup (or 'choco install innosetup')" }

# Web installers: no payload, so none of the publish-dir work below applies.
# Both channels compile from one script; the output names come from the
# script's Channel define (Nexus-Installer.exe / Nexus-Installer-Beta.exe).
if ($Bootstrap) {
    $bootDir = Join-Path $scriptDir "bootstrap"
    $bootIss = Join-Path $bootDir "Nexus-Bootstrap.iss"
    if (-not (Test-Path $bootIss)) { throw "Missing $bootIss" }
    $baseArgs = @()
    if ($Sign) { $baseArgs += Get-IsccSignArgs }
    if ($BootstrapBaseUrl) { $baseArgs += "/DBaseUrl=$BootstrapBaseUrl" }
    $built = @{ stable = "Nexus-Installer.exe"; beta = "Nexus-Installer-Beta.exe" }
    foreach ($channel in @("stable", "beta")) {
        Push-Location $bootDir
        try {
            & $iscc @baseArgs "/DChannel=$channel" Nexus-Bootstrap.iss
            if ($LASTEXITCODE -ne 0) { throw "ISCC compile failed for the $channel web installer (exit $LASTEXITCODE)" }
        } finally {
            Pop-Location
        }
    }
    Write-Host ""
    foreach ($channel in @("stable", "beta")) {
        $exe = Join-Path $scriptDir ("output\" + $built[$channel])
        if (-not (Test-Path $exe)) { throw "ISCC produced no $exe" }
        if ($Sign -and (Get-AuthenticodeSignature $exe).Status -ne 'Valid') {
            throw "$($built[$channel]) is not validly signed after ISCC"
        }
        $hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
        # Same "<hash>  <name>" shape as SHA256SUMS, one file per installer.
        Set-Content -Path "$exe.sha256" -Value "$hash  $($built[$channel])" -NoNewline
        $size = [math]::Round((Get-Item $exe).Length / 1MB, 2)
        Write-Host "Built: $exe  ($size MB)"
        Write-Host "SHA256: $hash"
    }
    return
}

if (-not (Test-Path $iss))  { throw "Missing Nexus.iss next to this script: $iss" }
if (-not (Test-Path (Join-Path $PublishDir "Nexus.exe"))) {
    throw "AOT publish not found at $PublishDir. Run dotnet publish first."
}

# Game Sync shim DLLs (produced by the nexus-gamesync component) ship under
# tools\gamesync\, where GameSyncShimInstaller stages them at enable-time. They
# are optional: when absent, the release builds fine and Game Sync stays inactive
# (the runtime tolerates a missing bundle). Warn rather than fail, so cutting a
# release does not require the MSVC shim build.
$shimX64 = @("RzChromaSDK64.dll", "RzChromatic64.dll", "LightFX.dll", "LogitechLedEnginesWrapper.dll", "LogitechLed.dll")
$shimX86 = @("RzChromaSDK.dll", "RzChromatic.dll", "LightFX.dll", "LogitechLedEnginesWrapper.dll", "LogitechLed.dll")
$shimX64Dir = Join-Path $PublishDir "tools\gamesync\x64"
$shimX86Dir = Join-Path $PublishDir "tools\gamesync\x86"
$missingShims = @()
foreach ($dll in $shimX64) {
    if (-not (Test-Path (Join-Path $shimX64Dir $dll))) { $missingShims += "tools\gamesync\x64\$dll" }
}
foreach ($dll in $shimX86) {
    if (-not (Test-Path (Join-Path $shimX86Dir $dll))) { $missingShims += "tools\gamesync\x86\$dll" }
}
if ($missingShims.Count -gt 0) {
    Write-Warning "Game Sync shim DLLs not bundled; the release ships with Game Sync inactive. Missing:`n  $($missingShims -join "`n  ")`nTo include them, build nexus-gamesync (build.bat + build32.bat) and re-publish."
}

# Strip any leftover macOS AppleDouble files from the publish dir (they slip in
# through scp/tar from a Mac dev machine and break Inno's compressor).
[System.IO.Directory]::EnumerateFiles($PublishDir, "._*", "AllDirectories") |
    ForEach-Object { [System.IO.File]::Delete("\\?\" + $_) }

# Fail if wwwroot/assets holds a bundle unreachable from index.html. The
# BuildWeb=false publish path skips the wwwroot wipe in Nexus.Service.csproj, so
# bundles from earlier builds accumulate; this asserts every bundle belongs to
# the current build. Closure: seed from index.html, expand through inter-chunk
# references (Vite hashed basenames), flag the rest.
$assetsDir = Join-Path $PublishDir "wwwroot\assets"
$indexHtml = Join-Path $PublishDir "wwwroot\index.html"
if ((Test-Path $assetsDir) -and (Test-Path $indexHtml)) {
    $all = @(Get-ChildItem $assetsDir -File |
        Where-Object { $_.Extension -eq '.js' -or $_.Extension -eq '.css' } |
        ForEach-Object { $_.Name })
    $reach = [System.Collections.Generic.HashSet[string]]::new()
    $indexText = Get-Content $indexHtml -Raw
    foreach ($f in $all) { if ($indexText.Contains($f)) { [void]$reach.Add($f) } }
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($f in @($reach)) {
            if (-not $f.EndsWith('.js')) { continue }
            $text = Get-Content (Join-Path $assetsDir $f) -Raw
            foreach ($g in $all) {
                if (-not $reach.Contains($g) -and $text.Contains($g)) {
                    [void]$reach.Add($g); $changed = $true
                }
            }
        }
    }
    $orphans = @($all | Where-Object { -not $reach.Contains($_) })
    if ($orphans.Count -gt 0) {
        Write-Host "Orphaned bundles in $assetsDir (stale, not reachable from index.html):"
        $orphans | ForEach-Object { Write-Host "  $_" }
        throw "wwwroot is not clean: $($orphans.Count) orphaned bundle(s). Rebuild nexus-web (which wipes wwwroot) before packaging."
    }
    Write-Host "wwwroot clean: $($all.Count) bundles, 0 orphaned."
}

# Sign every PE in the payload before Inno packages it. Smart App Control
# (Microsoft's SACVT preinstall validation) requires each .exe/.dll on the
# image to chain to a trusted root regardless of who launches it, so bundled
# third-party binaries are signed too. Files already validly signed keep
# their original publisher signature (adb.exe - Google, diskspd.exe -
# Microsoft). .sys is never re-signed: PawnIO.sys carries the kernel-mode
# signature it needs to load, which a plain Authenticode sign would replace.
if ($Sign) {
    if (-not (Test-Path $signMetadata)) { throw "Missing signing metadata: $signMetadata" }
    $unsigned = @(Get-ChildItem $PublishDir -Recurse -File -Force -Include *.exe, *.dll |
        Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -ne 'Valid' } |
        ForEach-Object { $_.FullName })
    Invoke-NexusSigning $unsigned

    # Gate: nothing unsigned may reach the installer payload.
    $stillUnsigned = @(Get-ChildItem $PublishDir -Recurse -File -Force -Include *.exe, *.dll, *.sys |
        Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -ne 'Valid' })
    if ($stillUnsigned.Count -gt 0) {
        $stillUnsigned | ForEach-Object { Write-Host "UNSIGNED: $($_.FullName)" }
        throw "$($stillUnsigned.Count) PE file(s) unsigned after the signing sweep."
    }
    Write-Host "Signing gate: every PE under $PublishDir is validly signed."
}

# Drive the installer's displayed version from the VERSION file so Nexus-Setup.exe
# reports the real product version (e.g. 3.0.0-beta.1) instead of a stale literal.
# VersionInfoVersion (the Details-tab File version) must be 4-part numeric, so the
# -beta.N suffix rides only on AppVersion.
$verFull = (Get-Content (Join-Path $scriptDir "..\VERSION") -Raw).Trim()
$verNumeric = ($verFull -split '-')[0]
$verInfo = "$verNumeric.0"

$isccArgs = @("/DPublishDir=$PublishDir", "/DMyAppVersion=$verFull", "/DMyAppVersionInfo=$verInfo")
if ($Sign) { $isccArgs += Get-IsccSignArgs }

Push-Location $scriptDir
try {
    & $iscc @isccArgs Nexus.iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC compile failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}

$out = Join-Path $scriptDir "output\Nexus-Setup.exe"

# ISCC already signed Setup.exe via the nexussign tool; assert it before the
# drop copy and the hash, so SHA256SUMS (and the OTA integrity check that
# reads it) covers the signed bytes.
if ($Sign -and (Get-AuthenticodeSignature $out).Status -ne 'Valid') {
    throw "Nexus-Setup.exe is not validly signed after ISCC"
}

$dropDir = (Resolve-Path (Join-Path $scriptDir "..\..")).Path
$drop    = Join-Path $dropDir "Nexus-Setup.exe"
Copy-Item $out $drop -Force

$sha256 = (Get-FileHash $out -Algorithm SHA256).Hash.ToLower()
$sumsLine = "$sha256  Nexus-Setup.exe"
Set-Content -Path (Join-Path $scriptDir "output\SHA256SUMS") -Value $sumsLine -NoNewline
Set-Content -Path (Join-Path $dropDir "SHA256SUMS") -Value $sumsLine -NoNewline

$size = [math]::Round((Get-Item $out).Length / 1MB, 2)
Write-Host ""
Write-Host "Built: $out  ($size MB)"
Write-Host "Drop:  $drop"
Write-Host "SHA256: $sha256"
if ($OpenOutput) { explorer.exe "/select,$drop" }
