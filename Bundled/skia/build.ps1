# Builds Bundled\win-x64\skia\libSkiaSharp.dll. macOS / Linux: build.sh. Provenance: NOTES.md.
#
#   powershell -File Bundled\skia\build.ps1 -Work C:\skia-build
#
# Needs: VS 2022 Build Tools (C++), LLVM 19.1.1 at C:\Program Files\LLVM (the
# clang-cl SkiaSharp's own CI uses), CMake, NASM, Ninja, Python 3, git.
param(
  [Parameter(Mandatory)] [string] $Work,
  [string] $Llvm = "C:\Program Files\LLVM",
  [string] $Vs = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools"
)
# Native tools report on stderr; failures are caught through $LASTEXITCODE instead.
$ErrorActionPreference = "Continue"

$SkiaCommit = "45afab4f1f0921f3feb97f58cf89b136fffa85e6" # mono/skia, SkiaSharp 4.153.1
$JpegTurboTag = "3.2.0"
$Deps = "libpng", "zlib", "libwebp", "wuffs", "harfbuzz"
$Here = $PSScriptRoot
$Out = Join-Path $Here "..\win-x64\skia"
New-Item -ItemType Directory -Force $Work | Out-Null
$Work = (Resolve-Path $Work).Path

function Invoke-Git { git.exe -c advice.detachedHead=false @args 2>&1 | Out-Null; if ($LASTEXITCODE) { throw "git $args failed" } }
function Fetch([string] $dir, [string] $url, [string] $sha) {
  if (Test-Path "$dir\.git") { return }
  Invoke-Git init -q $dir
  Invoke-Git -C $dir fetch -q --depth 1 $url $sha
  Invoke-Git -C $dir checkout -q FETCH_HEAD
}
# Runs a cmd line inside the x64 VS developer environment.
function InVs([string] $cmd) {
  cmd /c "`"$Vs\VC\Auxiliary\Build\vcvars64.bat`" >nul && $cmd"
  if ($LASTEXITCODE) { throw "failed: $cmd" }
}

# --- sources -----------------------------------------------------------------
$Skia = "$Work\skia"
Fetch $Skia "https://github.com/mono/skia.git" $SkiaCommit
foreach ($dep in $Deps) {
  $line = Select-String -Path "$Skia\DEPS" -Pattern "`"third_party/externals/$dep`"" | Select-Object -First 1
  if ($line.Line -notmatch '"(https[^"@]+)@([0-9a-f]+)"') { throw "no DEPS entry for $dep" }
  Fetch "$Skia\third_party\externals\$dep" $Matches[1] $Matches[2]
}
if (-not (Test-Path "$Work\libjpeg-turbo\.git")) {
  Invoke-Git clone -q --depth 1 -b $JpegTurboTag https://github.com/libjpeg-turbo/libjpeg-turbo.git "$Work\libjpeg-turbo"
}
if (-not (Test-Path "$Skia\bin\gn.exe")) { python "$Skia\bin\fetch-gn"; if ($LASTEXITCODE) { throw "fetch-gn failed" } }
foreach ($patch in Get-ChildItem "$Here\patches\*.patch") {
  git -C $Skia apply --reverse --check $patch.FullName 2>$null
  if ($LASTEXITCODE) { Invoke-Git -C $Skia apply $patch.FullName }
}

# --- libjpeg-turbo (static, SIMD, /MT) -------------------------------------------
# turbojpeg-static.lib carries both the TurboJPEG API (TurboJpeg.cs) and the
# libjpeg API Skia's codec uses, so it stands in for the system libjpeg.
$Jt = "$Work\jt-win-x64"
$cl = "$Llvm\bin\clang-cl.exe".Replace('\', '/')
InVs ("cmake -S `"$Work\libjpeg-turbo`" -B `"$Jt`" -G Ninja -DCMAKE_BUILD_TYPE=Release " +
  "-DCMAKE_C_COMPILER=`"$cl`" -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded " +
  "-DCMAKE_C_FLAGS=`"/Gy /Gw /guard:cf`" -DENABLE_SHARED=OFF -DENABLE_STATIC=ON " +
  "-DWITH_TURBOJPEG=ON -DWITH_TOOLS=OFF -DWITH_TESTS=OFF -DCMAKE_INSTALL_PREFIX=`"$Jt\install`" >nul")
InVs "cmake --build `"$Jt`" >nul && cmake --install `"$Jt`" >nul"
New-Item -ItemType Directory -Force "$Jt\link" | Out-Null
# GN passes the system lib as a bare "jpeg", which lld-link opens verbatim.
Copy-Item "$Jt\install\lib\turbojpeg-static.lib" "$Jt\link\jpeg" -Force

# --- export list -----------------------------------------------------------------
$Def = "$Work\exports-win-x64.def"
@("LIBRARY libSkiaSharp", "EXPORTS") + (Get-Content "$Here\exports.txt" | Where-Object { $_ } | ForEach-Object { "  $_" }) |
  Set-Content -Encoding ascii $Def

# --- skia ------------------------------------------------------------------------
New-Item -ItemType Directory -Force "$Skia\nexus" | Out-Null
Copy-Item "$Here\BUILD.gn", "$Here\stubs.cpp" "$Skia\nexus\" -Force
# GN only loads build files the root BUILD.gn reaches.
if (-not (Select-String -Quiet -SimpleMatch "//nexus:SkiaSharp" "$Skia\BUILD.gn")) {
  Add-Content -Encoding ascii "$Skia\BUILD.gn" 'group("nexus") { deps = [ "//nexus:SkiaSharp" ] }'
}
$fwd = { param($p) $p.Replace('\', '/') }
$gnArgs = @(
  'target_os="win"', 'target_cpu="x64"',
  "clang_win=`"$(& $fwd $Llvm)`"", "win_vc=`"$(& $fwd "$Vs\VC")`"",
  'is_official_build=true', 'is_debug=false', 'skia_enable_tools=false',
  'skia_enable_ganesh=false', 'skia_enable_graphite=false', 'skia_use_gl=false',
  'skia_use_vulkan=false', 'skia_use_metal=false', 'skia_use_direct3d=false', 'skia_use_dawn=false',
  'skia_enable_pdf=false', 'skia_use_xps=false', 'skia_enable_svg=false', 'skia_enable_skottie=false',
  'skia_use_expat=false', 'skia_use_dng_sdk=false', 'skia_use_piex=false', 'skia_use_icu=false',
  'skia_use_harfbuzz=false', 'skia_use_perfetto=false', 'skia_enable_precompile=false',
  'skia_use_fontations=false', 'skia_use_jpeg_gainmaps=false', 'skia_use_partition_alloc=false',
  'skia_use_libwebp_encode=false', 'skia_use_no_webp_encode=true',
  'skia_enable_fontmgr_win_gdi=false', 'skia_use_freetype=false',
  'skia_use_system_libjpeg_turbo=true', 'skia_use_system_libpng=false',
  'skia_use_system_libwebp=false', 'skia_use_system_zlib=false',
  'skia_use_system_freetype2=false',
  "nexus_exports=`"$(& $fwd $Def)`"",
  ("extra_cflags=[ '-DSK_AVOID_SLOW_RASTER_PIPELINE_BLURS', '-DSK_ENABLE_LEGACY_SHADERCONTEXT', '-DSK_DISABLE_EFFECT_DESERIALIZATION', " +
   "'/MT', '/EHsc', '/guard:cf', '-D_HAS_AUTO_PTR_ETC=1', '/I$(& $fwd "$Jt\install\include")' ]"),
  "extra_ldflags=[ '/guard:cf', '/LIBPATH:$(& $fwd "$Jt\link")' ]"
) -join ' '
Set-Location $Skia
# GN strings take double quotes; args.gn avoids PowerShell's native-argument quoting.
New-Item -ItemType Directory -Force "$Skia\out\win-x64" | Out-Null
Set-Content -Encoding ascii "$Skia\out\win-x64\args.gn" $gnArgs.Replace("'", '"')
# GN would otherwise run python3, which on Windows can be the Store alias stub.
$python = (Get-Command python.exe).Source
& "$Skia\bin\gn.exe" gen out/win-x64 "--script-executable=$python"
if ($LASTEXITCODE) { throw "gn gen failed" }
InVs "ninja -C out\win-x64 nexus:SkiaSharp"

# --- output ---------------------------------------------------------------------
New-Item -ItemType Directory -Force $Out | Out-Null
Copy-Item "$Skia\out\win-x64\libSkiaSharpNexus.dll" "$Out\libSkiaSharp.dll" -Force -ErrorAction Stop
Get-Item "$Out\libSkiaSharp.dll" | Format-Table Name, Length
