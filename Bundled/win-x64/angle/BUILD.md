# ANGLE (Direct3D 11 only) - Windows build

`libEGL.dll` and `libGLESv2.dll` are a slim build of ANGLE (OpenGL ES 3 on
Direct3D 11) for the Windows lighting renderer. Only the Direct3D 11 backend
is compiled in. WARP is a Direct3D 11 device type, so the software adapter is
unchanged. The Vulkan, Direct3D 9, desktop GL, null, SwiftShader, WebGPU and
Metal backends, the GLSL output, frame capture and the tests are not built.
The HLSL translator and ESSL input are kept.

## Source

- ANGLE `c5bf49e78850a5335a8fa2a7bfb53b559fbfc927` (Chromium 150.0.7871.250
  `angle_revision`, the `[M150]` branch).
- The 46 `cherry-pick-*.patch` files listed in the `patches/angle/.patches` of
  Electron v43.7.7, applied in order with `git am`. They are upstream ANGLE
  fixes, so the code matches the ANGLE inside that Electron release (the
  reported git hash differs because `git am` creates new commits).
- Third-party code statically linked: Abseil, Arm ASTC Encoder, zlib, ceval,
  and Chromium-authored sources (anglebase, zlib wrapper); licenses in
  `ANGLE-LICENSE.txt`. The C and C++ runtime is linked
  statically, so no Visual C++ redistributable is needed.

## Toolchain

Windows 11, Visual Studio 2022 Build Tools 17.14 (MSVC 14.44.35207, supplies
the libraries), Windows SDK 10.0.26100.0, the clang/lld that `gclient sync`
fetches for ANGLE, depot_tools, Python 3.12, x64.

## gn args (`out/off/args.gn`)

    is_debug = false
    is_component_build = false
    target_cpu = "x64"
    is_official_build = true
    use_thin_lto = true
    chrome_pgo_phase = 0
    symbol_level = 0
    angle_enable_d3d11 = true
    angle_enable_d3d9 = false
    angle_enable_gl = false
    angle_enable_vulkan = false
    angle_enable_null = false
    angle_enable_swiftshader = false
    angle_enable_wgpu = false
    angle_enable_metal = false
    angle_enable_glsl = false
    angle_build_tests = false
    angle_has_frame_capture = false

`is_official_build` gives ThinLTO and the release optimization flags (-O2
class, not size-optimized). No PGO profile is used.

## Reproduce

    git clone https://chromium.googlesource.com/chromium/tools/depot_tools.git
    git clone https://chromium.googlesource.com/angle/angle
    set PATH=<depot_tools>;%PATH%
    set DEPOT_TOOLS_WIN_TOOLCHAIN=0
    set GYP_MSVS_VERSION=2022
    set GYP_MSVS_OVERRIDE_PATH=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools
    set vs2022_install=%GYP_MSVS_OVERRIDE_PATH%
    set WINDOWSSDKDIR=C:\Program Files (x86)\Windows Kits\10
    cd angle
    git checkout c5bf49e78850a5335a8fa2a7bfb53b559fbfc927
    python scripts/bootstrap.py
    gclient sync --no-history
    git clone --depth 1 --branch v43.7.7 https://github.com/electron/electron
    git am --3way <each patch named in electron/patches/angle/.patches, in order>
    (create chrome\VERSION with MAJOR=150 MINOR=0 BUILD=7871 PATCH=250;
     the official build's timestamp step reads it)
    gn gen out\off      (with the args above in out\off\args.gn)
    autoninja -C out\off libEGL libGLESv2

Copy `out\off\libEGL.dll` and `out\off\libGLESv2.dll` here.

## Differences from the Electron build

- Exports: the 38 `Cr_z_*` zlib symbols that the Electron build re-exports
  from `libGLESv2.dll` are gone. Every `egl*` (115) and `gl*` entry point is
  unchanged.
- Imports: no `d3d9.dll` (the old build's delay import), no `opengl32.dll`
  reference, no `GDI32`; `libGLESv2.dll` imports `USER32`, `KERNEL32`, `dxgi` and
  `api-ms-win-core-synch-l1-2-0`. The `d3dcompiler_*` DLLs are loaded at
  runtime in both builds.
- Size: `libGLESv2.dll` 8,056,832 to 5,412,352 bytes, `libEGL.dll` 467,968 to
  508,416 bytes.
