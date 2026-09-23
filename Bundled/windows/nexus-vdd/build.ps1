# Builds NexusVirtualDisplay.dll with the MSVC toolset and the WDK headers/libs from the
# Microsoft.Windows.WDK.x64 NuGet package, then catalogs and signs the driver package.
# Output lands in Bundled\win-x64\vdd, where Nexus.Service.csproj picks it up.
param(
    # Package root holding bin, Include and Lib; empty downloads the package into build\wdk.
    [string]$Wdk = "",
    [string]$SdkVersion = "10.0.26100.0",
    [string]$Out = (Join-Path $PSScriptRoot "..\..\win-x64\vdd"),
    # Azure Artifact Signing with the installer's identity (installer\signing-metadata.json).
    [switch]$Sign,
    [string]$SignToolPath = "",
    [string]$DlibPath = "",
    # DriverVer version; empty derives it from VERSION: 3.0.17-beta.2 -> 3.0.17.2, 3.0.17 -> 3.0.17.65535.
    [string]$Version = ""
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$metadata = Join-Path $PSScriptRoot "..\..\..\installer\signing-metadata.json"
if ($Sign -and -not ($DlibPath -and (Test-Path $DlibPath))) { throw "-Sign needs -DlibPath (Azure.CodeSigning.Dlib.dll)" }
if (-not $Version) {
    # pnputil ranks same-day packages by version, so each prerelease of one version needs its own.
    $full = (Get-Content (Join-Path $PSScriptRoot "..\..\..\VERSION") -Raw).Trim()
    $build = if ($full -notmatch '-') { 65535 } elseif ($full -match '\.(\d+)$') { $Matches[1] } else { 0 }
    $Version = ($full -split '-')[0] + ".$build"
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=x64 -host_arch=x64" | Out-Null

$obj = Join-Path $PSScriptRoot "build"
New-Item -ItemType Directory -Force $obj, $Out | Out-Null

if (-not $Wdk) {
    $Wdk = Join-Path $obj "wdk\c"
    if (-not (Test-Path "$Wdk\bin")) {
        $zip = Join-Path $obj "wdk.zip"
        Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Windows.WDK.x64/10.0.26100.6584" -OutFile $zip -UseBasicParsing
        $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
        if ($hash -ne "C393D03DFB640B5C92F546B32F6770EF68CD3AAF691956E7D66D8E2C28A1B55E") { throw "WDK package hash mismatch: $hash" }
        # Extract aside and rename, so an interrupted extract is never taken for a complete tree.
        $tmp = Join-Path $obj "wdk.tmp"
        Remove-Item $tmp, (Join-Path $obj "wdk") -Recurse -Force -ErrorAction SilentlyContinue
        Expand-Archive $zip $tmp -Force
        Rename-Item $tmp "wdk"
        Remove-Item $zip
    }
}

$defines = "/DUNICODE", "/D_UNICODE", "/DUMDF_DRIVER", "/DUMDF_USING_NTSTATUS", "/DUMDF_VERSION_MAJOR=2", "/DUMDF_VERSION_MINOR=25",
    "/DIDDCX_VERSION_MAJOR=1", "/DIDDCX_VERSION_MINOR=4", "/DIDDCX_MINIMUM_VERSION_REQUIRED=4", "/D_ATL_NO_WIN_SUPPORT"
$includes = "/I$Wdk\Include\wdf\umdf\2.25", "/I$Wdk\Include\$SdkVersion\um\iddcx\1.4", "/I$Wdk\Include\$SdkVersion\um", "/I$Wdk\Include\$SdkVersion\shared"
& cl.exe /nologo /c /O2 /MT /EHsc /std:c++17 /W3 /Zi $defines $includes "$PSScriptRoot\Driver.cpp" "/Fo$obj\Driver.obj" "/Fd$obj\Driver.pdb"
if ($LASTEXITCODE -ne 0) { throw "cl failed ($LASTEXITCODE)" }

& link.exe /nologo /DLL /DEBUG /OPT:REF /OPT:ICF "/OUT:$Out\NexusVirtualDisplay.dll" "/PDB:$obj\NexusVirtualDisplay.pdb" "/IMPLIB:$obj\NexusVirtualDisplay.lib" "$obj\Driver.obj" `
    "$Wdk\Lib\wdf\umdf\x64\2.25\WdfDriverStubUm.lib" "$Wdk\Lib\$SdkVersion\um\x64\iddcx\1.4\iddcxstub.lib" `
    d3d11.lib dxgi.lib avrt.lib advapi32.lib ole32.lib kernel32.lib user32.lib ntdll.lib
if ($LASTEXITCODE -ne 0) { throw "link failed ($LASTEXITCODE)" }

& cl.exe /nologo /O2 /MT /EHsc /W3 /DUNICODE /D_UNICODE "$PSScriptRoot\Host.cpp" "/Fo$obj\Host.obj" "/Fe$Out\NexusVirtualDisplayHost.exe" /link onecore.lib
if ($LASTEXITCODE -ne 0) { throw "host build failed ($LASTEXITCODE)" }

if ($Sign -and -not $SignToolPath) {
    # The Artifact Signing dlib does not work with the 10.0.20348 SDK signtool.
    $SignToolPath = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.*\x64\signtool.exe" |
        Where-Object { $_.Directory.Parent.Name -notlike "10.0.20348.*" } |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $SignToolPath) { throw "no Windows SDK signtool.exe found; pass -SignToolPath" }
}
function Sign([string]$File) {
    if (-not $Sign) { return }
    # Artifact Signing and the timestamp server fail a small share of requests outright (see
    # installer\build-installer.ps1); a retry succeeds.
    for ($attempt = 1; ; $attempt++) {
        & $SignToolPath sign /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib $DlibPath /dmdf $metadata $File
        if ($LASTEXITCODE -eq 0) { return }
        if ($attempt -ge 4) { throw "signtool failed on $File ($LASTEXITCODE)" }
        Start-Sleep -Seconds (5 * $attempt)
    }
}

# The catalog hashes the signed driver, so the DLL is signed first and the catalog last.
Sign "$Out\NexusVirtualDisplay.dll"
Sign "$Out\NexusVirtualDisplayHost.exe"
Copy-Item "$PSScriptRoot\NexusVirtualDisplay.inf" $Out -Force
& "$Wdk\bin\$SdkVersion\x64\stampinf.exe" -f "$Out\NexusVirtualDisplay.inf" -d * -v $Version
if ($LASTEXITCODE -ne 0) { throw "stampinf failed ($LASTEXITCODE)" }
Remove-Item "$Out\NexusVirtualDisplay.cat" -ErrorAction SilentlyContinue
& "$Wdk\bin\$SdkVersion\x86\Inf2Cat.exe" "/driver:$Out" /os:10_X64 /uselocaltime
if ($LASTEXITCODE -ne 0) { throw "Inf2Cat failed ($LASTEXITCODE)" }
Sign "$Out\NexusVirtualDisplay.cat"
Get-ChildItem $Out | Select-Object Name, Length
