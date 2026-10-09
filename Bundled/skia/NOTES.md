# libSkiaSharp (Nexus build)

`Bundled/<rid>/skia/libSkiaSharp.{dll,dylib,so}` is one native library per RID,
built from source by the scripts here. It replaces three stock libraries:
`libSkiaSharp` from the SkiaSharp NuGet packages, `libHarfBuzzSharp` from the
HarfBuzzSharp packages, and the standalone libjpeg-turbo `turbojpeg` library.

| | |
| --- | --- |
| Skia | mono/skia `45afab4f1f0921f3feb97f58cf89b136fffa85e6`, the commit SkiaSharp 4.153.1 ships (its third-party deps pinned by that tree's `DEPS`) |
| HarfBuzz | the same tree's `third_party/externals/harfbuzz`, as the official HarfBuzzSharp 14.2.1.301 |
| libjpeg-turbo | 3.2.0, static, SIMD on (NASM on x64, NEON on arm64) |
| Managed | SkiaSharp 4.153.1 and HarfBuzzSharp 14.2.1.301 from NuGet, unmodified |

## What differs from the NuGet build

- **Raster only.** No Ganesh/Graphite, GL, Vulkan, Direct3D or Metal; no PDF,
  XPS, SVG or Skottie; no RAW/DNG; WebP decodes but does not encode.
- **Only the entry points in `exports.txt` are exported**, and the linker drops
  everything they do not reach. `exports.txt` is the set the AOT service binds
  plus the few the tests call. `CheckSkiaExports` in `Nexus.Service.csproj`
  fails an AOT publish whose binary binds a name missing from it.
- **HarfBuzz is linked in.** `TextShaper` points HarfBuzzSharp's
  `libHarfBuzzSharp` imports at this library. It builds with the official
  defines plus `HB_NO_*` switches for APIs shaping never reaches (draw, paint,
  serialization, name/math/meta tables).
- **libjpeg-turbo is linked in** as Skia's system libjpeg and exports the
  TurboJPEG API `TurboJpeg.cs` binds, so the JPEG decoder and the encoder share
  one SIMD copy. The stock Skia build compiles libjpeg-turbo without x86 SIMD.
- `SK_DISABLE_EFFECT_DESERIALIZATION`: the service never deserializes Skia
  pictures or effects, and the registry pulled every effect into the link.
- `patches/` makes the C API compile without Ganesh, fixes Skia's macOS
  min-version linker flag, and lets the build override `SK_X_API` (Windows
  otherwise dllexports every xamarin entry point past the `.def` list).
- `stubs.cpp` gives bodies to the GPU, path-op and animated-WebP entry points
  that linked C API objects reference and nothing calls (lld-link resolves
  every symbol of a linked object before discarding dead code).

Compiler and Skia flags otherwise match SkiaSharp's own CI
(`native/<os>/build.cake` at the 4.153.1 tag): clang-cl 19.1.1 with `/MT` and
`/guard:cf` on Windows, Apple clang on macOS, clang on Linux, `-O3`/`/O2`,
`SK_AVOID_SLOW_RASTER_PIPELINE_BLURS`, `SK_ENABLE_LEGACY_SHADERCONTEXT`.

## Rebuilding

Edit `exports.txt` (one name per line, sorted) when the service starts calling
a SkiaSharp, HarfBuzzSharp or TurboJPEG API that is not listed, then rebuild all
three RIDs. A first build fetches about 1 GB of sources; a relink after an
`exports.txt` change takes seconds.

| RID | Where | Command |
| --- | --- | --- |
| win-x64 | Windows, VS 2022 Build Tools (C++), LLVM 19.1.1, CMake, NASM, Ninja, Python 3 | `powershell -File Bundled\skia\build.ps1 -Work C:\skia-build` |
| osx-arm64 | macOS, Xcode, CMake, Ninja, Python 3 | `Bundled/skia/build.sh osx-arm64 ~/skia-build` |
| linux-x64 | `debian:12-slim` amd64 container with `clang-16 cmake nasm ninja-build python3 git libfontconfig-dev patchelf`, `clang`/`clang++` linked to the 16 binaries | `Bundled/skia/build.sh linux-x64 /work` |

Each script writes `Bundled/<rid>/skia/`. After rebuilding, run the service
tests on macOS (they load the bundled library) and an AOT publish per RID.

## Runtime dependencies

- win-x64: KERNEL32, USER32. DirectWrite loads at runtime.
- osx-arm64: CoreText, CoreGraphics, CoreFoundation, libc++; macOS 12.0+.
- linux-x64: libfontconfig.so.1 and glibc 2.35+. FreeType, libstdc++ and
  libgcc are linked in, so the library needs no C++ runtime on the system. The
  Nexus binary itself needs glibc 2.38 (v3.0.24-beta.1).

## Licenses

Skia, skcms: BSD-3-Clause. HarfBuzz: Old MIT. libjpeg-turbo: BSD-3-Clause, IJG
and zlib (`../turbojpeg-LICENSE.md`, `../turbojpeg-README.ijg`). libpng: PNG
Reference Library License v2. zlib: zlib. libwebp: BSD-3-Clause. Wuffs:
Apache-2.0. FreeType (Linux only): FreeType License.
