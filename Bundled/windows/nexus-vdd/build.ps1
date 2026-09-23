# Builds NexusVirtualDisplay.dll with the MSVC toolset and the WDK headers/libs from the
# Microsoft.Windows.WDK.x64 NuGet package, then catalogs and signs the driver package.
# Output lands in Bundled\win-x64\vdd, where Nexus.Service.csproj picks it up.
param(
    [string]$Wdk = "$env:USERPROFILE\wdk\pkg\c",
    [string]$SdkVersion = "10.0.26100.0",
    [string]$Out = (Join-Path $PSScriptRoot "..\..\win-x64\vdd"),
    # Certificate thumbprint in LocalMachine\My used to sign the .cat and .dll; empty skips signing.
    [string]$SignThumbprint = "",
    [string]$Version = "1.0.0.1"
)
$ErrorActionPreference = "Stop"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments "-arch=x64 -host_arch=x64" | Out-Null

$obj = Join-Path $PSScriptRoot "build"
New-Item -ItemType Directory -Force $obj, $Out | Out-Null

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

$signtool = "${env:ProgramFiles(x86)}\Windows Kits\10\bin\$SdkVersion\x64\signtool.exe"
function Sign([string]$File) {
    if (-not $SignThumbprint) { return }
    & $signtool sign /sm /sha1 $SignThumbprint /fd SHA256 $File
    if ($LASTEXITCODE -ne 0) { throw "signtool failed on $File ($LASTEXITCODE)" }
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
