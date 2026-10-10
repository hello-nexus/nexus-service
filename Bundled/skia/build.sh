#!/usr/bin/env bash
# Builds Bundled/<rid>/skia/libSkiaSharp.{dylib,so} for osx-arm64 or linux-x64.
# Windows: build.ps1. Provenance and the why: NOTES.md.
#
#   Bundled/skia/build.sh osx-arm64 <work-dir>     (on macOS, Xcode clang, cmake)
#   Bundled/skia/build.sh linux-x64 <work-dir>     (in Docker, see NOTES.md)
set -euo pipefail

RID="$1"
WORK="$(mkdir -p "$2" && cd "$2" && pwd)"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/../$RID/skia"

SKIA_COMMIT=45afab4f1f0921f3feb97f58cf89b136fffa85e6 # mono/skia, SkiaSharp 4.153.1
JPEG_TURBO_TAG=3.2.0
DEPS=(libpng zlib libwebp wuffs harfbuzz)
[[ "$RID" == linux-* ]] && DEPS+=(freetype)

JOBS="$(getconf _NPROCESSORS_ONLN)"

# --- sources -----------------------------------------------------------------
if [ ! -d "$WORK/skia/.git" ]; then
  git init -q "$WORK/skia"
  git -C "$WORK/skia" fetch -q --depth 1 https://github.com/mono/skia.git "$SKIA_COMMIT"
  git -C "$WORK/skia" checkout -q FETCH_HEAD
fi
for dep in "${DEPS[@]}"; do
  dir="$WORK/skia/third_party/externals/$dep"
  [ -d "$dir/.git" ] && continue
  line="$(grep "\"third_party/externals/$dep\"" "$WORK/skia/DEPS")"
  url="$(sed -E 's/.*"(https[^"@]+)@([0-9a-f]+)".*/\1/' <<<"$line")"
  sha="$(sed -E 's/.*@([0-9a-f]+)".*/\1/' <<<"$line")"
  git init -q "$dir"
  git -C "$dir" fetch -q --depth 1 "$url" "$sha"
  git -C "$dir" checkout -q FETCH_HEAD
done
if [ ! -d "$WORK/libjpeg-turbo/.git" ]; then
  git clone -q --depth 1 -b "$JPEG_TURBO_TAG" https://github.com/libjpeg-turbo/libjpeg-turbo.git "$WORK/libjpeg-turbo"
fi
[ -x "$WORK/skia/bin/gn" ] || python3 "$WORK/skia/bin/fetch-gn"
for patch in "$HERE"/patches/*.patch; do
  git -C "$WORK/skia" apply --reverse --check "$patch" 2>/dev/null || git -C "$WORK/skia" apply "$patch"
done

# --- libjpeg-turbo (static, SIMD) ----------------------------------------------
# libturbojpeg.a carries both the TurboJPEG API (TurboJpeg.cs) and the libjpeg
# API Skia's codec uses, so it is installed as libjpeg.a for skia_use_system_libjpeg_turbo.
JT="$WORK/jt-$RID"
cmake_args=(-DCMAKE_BUILD_TYPE=Release -DENABLE_SHARED=OFF -DENABLE_STATIC=ON
  -DWITH_TURBOJPEG=ON -DWITH_TOOLS=OFF -DWITH_TESTS=OFF
  -DCMAKE_POSITION_INDEPENDENT_CODE=ON
  "-DCMAKE_C_FLAGS=-ffunction-sections -fdata-sections"
  "-DCMAKE_INSTALL_PREFIX=$JT/install")
if [ "$RID" = osx-arm64 ]; then
  cmake_args+=(-DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=12.0)
fi
cmake -S "$WORK/libjpeg-turbo" -B "$JT" "${cmake_args[@]}" >/dev/null
cmake --build "$JT" -j"$JOBS" >/dev/null
cmake --install "$JT" >/dev/null
mkdir -p "$JT/link"
cp "$JT/install/lib/libturbojpeg.a" "$JT/link/libjpeg.a"

# --- export list ---------------------------------------------------------------
EXPORTS="$WORK/exports-$RID"
if [ "$RID" = osx-arm64 ]; then
  sed 's/^/_/' "$HERE/exports.txt" > "$EXPORTS"
else
  # An implicit linker script: GNU ld only pulls archive members for EXTERN names,
  # never for names that appear only in a version script.
  { echo "EXTERN($(tr '\n' ' ' < "$HERE/exports.txt"))"
    echo 'VERSION { { global:'; sed 's/$/;/' "$HERE/exports.txt"; echo 'local: *; }; }'; } > "$EXPORTS"
fi

# --- skia ------------------------------------------------------------------------
mkdir -p "$WORK/skia/nexus"
cp "$HERE/BUILD.gn" "$HERE/stubs.cpp" "$WORK/skia/nexus/"
# GN only loads build files the root BUILD.gn reaches.
grep -q '//nexus:SkiaSharp' "$WORK/skia/BUILD.gn" ||
  echo 'group("nexus") { deps = [ "//nexus:SkiaSharp" ] }' >> "$WORK/skia/BUILD.gn"

common_args="is_official_build=true is_debug=false skia_enable_tools=false
  skia_enable_ganesh=false skia_enable_graphite=false skia_use_gl=false
  skia_use_vulkan=false skia_use_metal=false skia_use_direct3d=false skia_use_dawn=false
  skia_enable_pdf=false skia_use_xps=false skia_enable_svg=false skia_enable_skottie=false
  skia_use_expat=false skia_use_dng_sdk=false skia_use_piex=false skia_use_icu=false
  skia_use_harfbuzz=false skia_use_perfetto=false skia_enable_precompile=false
  skia_use_fontations=false skia_use_jpeg_gainmaps=false skia_use_partition_alloc=false
  skia_use_libwebp_encode=false skia_use_no_webp_encode=true
  skia_use_system_libjpeg_turbo=true skia_use_system_libpng=false
  skia_use_system_libwebp=false skia_use_system_zlib=false
 
  nexus_exports=\"$EXPORTS\""
cflags="'-DSKIA_C_DLL', '-DSK_AVOID_SLOW_RASTER_PIPELINE_BLURS', '-DSK_ENABLE_LEGACY_SHADERCONTEXT', '-DSK_DISABLE_EFFECT_DESERIALIZATION', '-I$JT/install/include'"

if [ "$RID" = osx-arm64 ]; then
  args="target_os=\"mac\" target_cpu=\"arm64\" min_macos_version=\"12.0\" $common_args
    extra_cflags=[ $cflags, '-DHAVE_ARC4RANDOM_BUF' ]
    extra_ldflags=[ '-L$JT/link' ]"
else
  args="target_os=\"linux\" target_cpu=\"x64\" skia_use_x11=false skia_use_system_freetype2=false $common_args
    cc=\"clang\" cxx=\"clang++\"
    extra_cflags=[ $cflags, '-DHAVE_SYSCALL_GETRANDOM', '-DXML_DEV_URANDOM' ]
    extra_ldflags=[ '-L$JT/link' ]"
fi

cd "$WORK/skia"
mkdir -p "out/$RID"
echo "${args//\'/\"}" > "out/$RID/args.gn"
bin/gn gen "out/$RID"
ninja -C "out/$RID" nexus:SkiaSharp

# --- output ---------------------------------------------------------------------
mkdir -p "$OUT"
if [ "$RID" = osx-arm64 ]; then
  cp "out/$RID/libSkiaSharpNexus.dylib" "$OUT/libSkiaSharp.dylib"
  strip -x "$OUT/libSkiaSharp.dylib"
  install_name_tool -id @rpath/libSkiaSharp.dylib "$OUT/libSkiaSharp.dylib"
  codesign --force -s - "$OUT/libSkiaSharp.dylib"
else
  cp "out/$RID/libSkiaSharpNexus.so" "$OUT/libSkiaSharp.so"
  strip --strip-unneeded "$OUT/libSkiaSharp.so"
  patchelf --set-soname libSkiaSharp.so "$OUT/libSkiaSharp.so"
fi
ls -l "$OUT"
