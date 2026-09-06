# Builds the Nexus Microsoft Store MSIX shell: a full-trust launcher
# (NexusStoreLauncher.exe) bundled with an existing Nexus-Setup.exe, packaged
# with makeappx. Architecture: a real launchable app that installs/opens the
# existing Inno-packaged suite, not a bare downloader (see the "Microsoft
# Store (MSIX)" section in installer/README.md).
#
# This is a v1 scaffold: it produces a locally installable/sideloadable .msix.
# The Store submission path (Partner Center upload, .msixupload, Store
# Submission API) is a separate, not-yet-built step.
#
# Usage (from Windows, in any shell). The two modes below use DIFFERENT
# -Publisher values and must not be confused: signtool enforces that
# Identity/@Publisher byte-matches the signing certificate's Subject
# (APPX_E_PUBLISHER_MISMATCH otherwise), so a -Sign build cannot use the
# Partner Center identity, and a Store submission must not use the signing
# cert subject (Microsoft's own signature replaces it on ingestion anyway).
#
#   Store build (stays unsigned; Store re-signs on submission):
#     powershell -File installer\msix\build-msix.ps1 `
#       -IdentityName "HelloNexus.HelloNexus" `
#       -Publisher "CN=62485AE2-77C6-4F70-A924-D5BA99963014" `
#       -PublisherDisplayName "Hello Nexus"
#
#   Local sideload verification (-Sign):
#     powershell -File installer\msix\build-msix.ps1 -Sign `
#       -IdentityName "HelloNexus.HelloNexus" `
#       -Publisher "<Azure Artifact Signing cert Subject, e.g. from
#                    (Get-AuthenticodeSignature .\Nexus-Setup.exe).SignerCertificate.Subject>" `
#       -PublisherDisplayName "Hello Nexus"

param(
    [string]$IdentityName = "Nexus.DEV.CHANGEME",
    [string]$Publisher = "CN=CHANGEME-Not-A-Real-Publisher",
    [string]$PublisherDisplayName = "CHANGE ME - placeholder publisher, not for Store submission",
    [string]$SetupPath = "",
    # Normally derived from the bundled Nexus-Setup.exe. Overriding makes the
    # package claim a version its payload does not carry, so prefer rebuilding
    # the payload; see the README's Identity tokens section.
    [string]$Version = "",
    # Cross-arch AOT publish is not dependable, so this must match the host
    # building it: win-arm64 only for a build running on an ARM64 machine.
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Rid = "win-x64",
    [switch]$Sign,
    [switch]$SkipPublisherModeCheck,
    [string]$SignToolPath = "",
    [string]$DlibPath = ""
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir "..\..")).Path

if ([string]::IsNullOrEmpty($SetupPath)) {
    $SetupPath = Join-Path $scriptDir "..\output\Nexus-Setup.exe"
}

# Whether the caller overrode the placeholder identity, read from
# PSBoundParameters rather than a second copy of the default literals above -
# editing a param default here cannot silently disable this check.
$identityOverridden = $PSBoundParameters.ContainsKey('IdentityName') -and
    $PSBoundParameters.ContainsKey('Publisher') -and
    $PSBoundParameters.ContainsKey('PublisherDisplayName')
if (-not $identityOverridden) {
    Write-Warning "Package identity is still the placeholder default (-IdentityName/-Publisher/-PublisherDisplayName not all overridden). This .msix is NOT valid for a real Store submission or a production sideload install."
}
if ($Sign -and -not $identityOverridden) {
    throw "Refusing a -Sign build with placeholder identity. Pass -Publisher set to the Azure Artifact Signing certificate's Subject (see the header comment), or omit -Sign for an unsigned scaffold build."
}

# Partner Center commonly issues a bare "CN=<GUID>" publisher id for an
# individual/unverified account; signtool would reject that as a signing
# subject, so a -Sign build needs the Azure Artifact Signing cert's Subject
# instead. This heuristic catches the mix-up before makeappx/signtool do.
$looksLikeStorePublisherId = $Publisher -match '^CN=[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
if ($Sign -and $looksLikeStorePublisherId -and -not $SkipPublisherModeCheck) {
    throw "-Publisher looks like a bare Partner Center 'CN=<GUID>' Store identity, not a certificate subject. -Sign needs -Publisher set to the Azure Artifact Signing cert's Subject instead; a Store build uses the Partner Center identity and stays unsigned (omit -Sign). Pass -SkipPublisherModeCheck to override this heuristic."
}

if (-not (Test-Path $SetupPath)) {
    throw "Nexus-Setup.exe not found at $SetupPath. Build it first (installer\build-installer.ps1), or pass -SetupPath."
}

function Resolve-MakeAppx {
    $kitRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    )
    $cands = foreach ($r in $kitRoots) {
        if (Test-Path $r) {
            Get-ChildItem $r -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^10\.\d+\.\d+\.\d+$' } |
                ForEach-Object { Join-Path $_.FullName "x64\makeappx.exe" } |
                Where-Object { Test-Path $_ }
        }
    }
    $best = $cands | Sort-Object {
        [version](Split-Path (Split-Path (Split-Path $_ -Parent) -Parent) -Leaf)
    } -Descending | Select-Object -First 1
    if (-not $best) {
        throw "makeappx.exe not found under Windows Kits 10. Install the Windows SDK."
    }
    return $best
}

# Mirrors installer\build-installer.ps1 Resolve-SignTool/Resolve-Dlib so the
# same Azure Artifact Signing setup (client tools, env vars, timestamp URL)
# works for both scripts without a second install/config path.
function Resolve-SignTool {
    if ($SignToolPath -and (Test-Path $SignToolPath)) { return $SignToolPath }
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
        (Join-Path $scriptDir "..\signing-tools")
    )
    foreach ($r in $roots) {
        if (Test-Path $r) {
            $hits = @(Get-ChildItem $r -Recurse -Filter "Azure.CodeSigning.Dlib.dll" -ErrorAction SilentlyContinue)
            if ($hits.Count -gt 0) {
                $x64 = $hits | Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
                if ($x64) { return $x64.FullName }
                return $hits[0].FullName
            }
        }
    }
    throw "Azure.CodeSigning.Dlib.dll (x64) not found. Install Microsoft.Azure.ArtifactSigningClientTools (winget) or pass -DlibPath."
}

# Version mapping: MSIX Identity/@Version requires exactly 4 numeric parts, and
# Partner Center rejects a nonzero 4th (revision) part (MSIX has no prerelease
# concept either; a beta channel would be a separate Store flight, not a suffix).
#
# The version comes off the BUNDLED PAYLOAD, not the repo's VERSION file: CI
# stamps VERSION at build time from its workflow input and never commits it back,
# so a checkout routinely sits several patches behind the released version, and a
# package claiming a version its own Nexus-Setup.exe does not carry misreports
# itself in the Store. build-installer.ps1 stamps VersionInfoVersion as
# "<numeric>.0" from VERSION, so the payload's own version resource already
# carries exactly what this needs.
if ($Version) {
    # Same shape check as the payload branch: an unvalidated -Version reaches
    # makeappx and fails there with a schema error instead of here.
    $parts = $Version.Trim() -split '\.'
    if ($parts.Count -lt 3 -or $parts.Count -gt 4 -or
        @($parts[0..2] | Where-Object { $_ -notmatch '^\d+$' }).Count -gt 0) {
        throw "-Version '$Version' is not a numeric 3- or 4-part version (e.g. 3.0.10 or 3.0.10.0)."
    }
    $msixVersion = "$($parts[0]).$($parts[1]).$($parts[2]).0"
    $versionSource = "-Version $Version"
    $payloadLabel = ""
} else {
    # The numeric VS_FIXEDFILEINFO parts, not the .FileVersion string: that
    # string is StringFileInfo text, which matches only while Nexus.iss leaves
    # VersionInfoTextVersion at its VersionInfoVersion default. These are ints
    # by construction, so they need no parsing, and a resource-less exe reports
    # 0.0.0, which the placeholder guard below still catches.
    $vi = (Get-Item $SetupPath).VersionInfo
    $msixVersion = "$($vi.FileMajorPart).$($vi.FileMinorPart).$($vi.FileBuildPart).0"
    $versionSource = $SetupPath
    # ProductVersion is the full semver, prerelease suffix included, which the
    # 4-part MSIX version cannot express: two betas of one patch pack as the
    # same 4-part version, so a filename built from that version alone would
    # let them overwrite each other with no way to tell the payloads apart.
    # The replace also drops the version resource's whitespace and NUL padding
    # (String.Trim does not remove NULs), and caps length since this reaches a
    # path.
    $payloadLabel = $vi.ProductVersion
    if ($payloadLabel) {
        $payloadLabel = ($payloadLabel -replace '[^A-Za-z0-9.\-+]', '')
        if ($payloadLabel.Length -gt 40) { $payloadLabel = $payloadLabel.Substring(0, 40) }
        # Nexus.iss sets neither VersionInfoProductVersion nor
        # VersionInfoProductTextVersion, so ProductVersion currently inherits
        # AppVersion. If anyone sets either, this label could name a different
        # version than the package carries; fall back rather than mislabel.
        $numeric = "$($vi.FileMajorPart).$($vi.FileMinorPart).$($vi.FileBuildPart)"
        if (-not $payloadLabel.StartsWith($numeric)) {
            Write-Warning "Payload ProductVersion '$payloadLabel' disagrees with its file version $numeric; naming the package after the version instead."
            $payloadLabel = ""
        }
    }
}
if ($msixVersion -like '0.0.0.*') {
    throw "$versionSource reports the placeholder version $msixVersion (the Nexus.iss default). Build the installer with installer\build-installer.ps1 from a checkout whose VERSION is real, or pass -Version."
}
Write-Host "MSIX version: $msixVersion (from $versionSource)"

$outputDir = Join-Path $scriptDir "output"
$stagingDir = Join-Path $outputDir "staging"
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stagingDir "Assets") -Force | Out-Null

# Publish the launcher (native AOT). -p:PublishAot=true is redundant with the
# csproj default but kept explicit: this binary ships inside a Store package,
# so it must never silently fall back to a framework-dependent build.
$launcherProj = Join-Path $scriptDir "NexusStoreLauncher\NexusStoreLauncher.csproj"
$launcherPublishDir = Join-Path $outputDir "launcher-publish"
if (Test-Path $launcherPublishDir) { Remove-Item $launcherPublishDir -Recurse -Force }
& dotnet publish $launcherProj -c Release -r $Rid --self-contained -p:PublishAot=true -o $launcherPublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for NexusStoreLauncher (exit $LASTEXITCODE)" }

Copy-Item (Join-Path $launcherPublishDir "*") $stagingDir -Recurse -Force
if (-not (Test-Path (Join-Path $stagingDir "NexusStoreLauncher.exe"))) {
    throw "NexusStoreLauncher.exe missing from the publish output; AOT publish did not produce the expected binary."
}

Copy-Item $SetupPath (Join-Path $stagingDir "Nexus-Setup.exe") -Force
Copy-Item (Join-Path $scriptDir "Assets\*") (Join-Path $stagingDir "Assets") -Recurse -Force

# MSIX Identity/@ProcessorArchitecture uses the short arch token, not the
# dotnet RID.
$archByRid = @{
    "win-x64"   = "x64"
    "win-arm64" = "arm64"
}
$arch = $archByRid[$Rid]
if (-not $arch) {
    throw "No MSIX architecture mapping for RID '$Rid'; add it to the archByRid table."
}

# Values are XML-escaped before injection (identity/company strings are
# operator-supplied, not compile-time constants) and the substitution uses
# literal String.Replace, not the -replace regex operator, so a $ in a
# publisher name can never be read as a regex backreference.
$manifestTemplate = Get-Content (Join-Path $scriptDir "AppxManifest.template.xml") -Raw
$escapedIdentityName = [System.Security.SecurityElement]::Escape($IdentityName)
$escapedPublisher = [System.Security.SecurityElement]::Escape($Publisher)
$escapedPublisherDisplayName = [System.Security.SecurityElement]::Escape($PublisherDisplayName)
$escapedVersion = [System.Security.SecurityElement]::Escape($msixVersion)
$manifest = $manifestTemplate.Replace('{{IDENTITY_NAME}}', $escapedIdentityName)
$manifest = $manifest.Replace('{{PUBLISHER}}', $escapedPublisher)
$manifest = $manifest.Replace('{{PUBLISHER_DISPLAY_NAME}}', $escapedPublisherDisplayName)
$manifest = $manifest.Replace('{{VERSION}}', $escapedVersion)
$manifest = $manifest.Replace('{{ARCH}}', $arch)
if ($manifest -match '\{\{[A-Z_]+\}\}') {
    throw "Unsubstituted token(s) remain in AppxManifest.xml: $($Matches[0])"
}

# Set-Content's default encoding under Windows PowerShell 5.1 is the system
# ANSI code page, not UTF-8, silently mismatching the file's own <?xml
# encoding="utf-8"?> declaration for any non-ASCII publisher/company name.
$manifestPath = Join-Path $stagingDir "AppxManifest.xml"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($manifestPath, $manifest, $utf8NoBom)

$makeappx = Resolve-MakeAppx
# Partner Center never reads the file name, so it can carry what the package
# version cannot: the payload's prerelease suffix, and the architecture, since
# a Store submission holds one package per arch and they share an output dir.
$msixName = if ($payloadLabel) { "Nexus-$payloadLabel-$arch.msix" } else { "Nexus-$msixVersion-$arch.msix" }
$msixPath = Join-Path $outputDir $msixName
& $makeappx pack /d $stagingDir /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed (exit $LASTEXITCODE)" }

if ($Sign) {
    if (-not (Test-Path (Join-Path $scriptDir "..\signing-metadata.json"))) {
        throw "Missing signing metadata: $(Join-Path $scriptDir '..\signing-metadata.json')"
    }
    $signMetadata = (Resolve-Path (Join-Path $scriptDir "..\signing-metadata.json")).Path
    $timestampUrl = "http://timestamp.acs.microsoft.com"
    $st = Resolve-SignTool
    $dlib = Resolve-Dlib
    Write-Host "Signing $msixPath for local sideload verification (Store re-signs on submission)"
    & $st sign /v /fd SHA256 /tr $timestampUrl /td SHA256 /dlib $dlib /dmdf $signMetadata $msixPath
    if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit $LASTEXITCODE) on $msixPath" }
}

$size = [math]::Round((Get-Item $msixPath).Length / 1MB, 2)
Write-Host ""
Write-Host "Built: $msixPath  ($size MB)"
Write-Host "Identity: Name=$IdentityName Publisher=$Publisher Version=$msixVersion Rid=$Rid Arch=$arch"
if (-not $identityOverridden) {
    Write-Host "WARNING: placeholder identity - scaffold build only, not a real Store package."
}
