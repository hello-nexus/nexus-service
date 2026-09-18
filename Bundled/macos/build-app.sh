#!/bin/bash
# Wrap the AOT-published macOS binary into a proper .app bundle.
# Usage: ./build-app.sh <staging-dir> <output-dir>
#   staging-dir: where `dotnet publish` output lives (contains Nexus binary + wwwroot)
#   output-dir:  where Nexus.app should be created

set -euo pipefail

STAGING="${1:-publish-mac/staging}"
OUT="${2:-publish-mac}"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
APP="$OUT/Nexus.app"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
mkdir -p "$APP/Contents/Resources"

# macOS ships AOT-only - the publish output is a single self-contained native
# binary, no managed Nexus.dll / *.deps.json / *.runtimeconfig.json sidecars.
# Copy whatever the AOT publish produced at the staging root.
cp "$STAGING/Nexus" "$APP/Contents/MacOS/"
[ -d "$STAGING/wwwroot" ] && cp -R "$STAGING/wwwroot" "$APP/Contents/MacOS/"
[ -d "$STAGING/openrgb" ] && cp -R "$STAGING/openrgb" "$APP/Contents/MacOS/"
[ -d "$STAGING/tools" ] && cp -R "$STAGING/tools" "$APP/Contents/MacOS/"
[ -f "$STAGING/status-icon.png" ] && cp "$STAGING/status-icon.png" "$APP/Contents/MacOS/"
[ -f "$STAGING/status-icon@2x.png" ] && cp "$STAGING/status-icon@2x.png" "$APP/Contents/MacOS/"

# Build and copy the Swift audio-helper sidecar so SCK system-audio loopback
# works for music-reactive lighting. Lives next to the main binary so TCC
# attributes Screen Recording permission to the Nexus bundle.
bash "$SCRIPT_DIR/audio-helper/build-helper.sh" "$APP/Contents/MacOS"

# Build and copy the Swift overlay-helper sidecar - per-screen transparent
# borderless NSWindow + WKWebView for the floating desktop widgets. The
# service spawns this when the user pins a widget; killed when the last
# widget is unpinned.
bash "$SCRIPT_DIR/overlay-helper/build-helper.sh" "$APP/Contents/MacOS"

# Build and copy the camera helper (VideoToolbox decode + CMIO sink feed) and
# the activation dylib the service P/Invokes for OSSystemExtensionRequest.
bash "$SCRIPT_DIR/camera-helper/build.sh" "$APP/Contents/MacOS"

# Embed the CMIO camera extension. Activation additionally requires the app
# to be signed with a real identity and installed in /Applications.
# NEXUS_SKIP_CAMERA_EXTENSION builds a notarizable bundle without the extension
# (the extension needs xcodebuild provisioning that headless CI can't do yet).
if [ -z "${NEXUS_SKIP_CAMERA_EXTENSION:-}" ]; then
    bash "$SCRIPT_DIR/camera-extension/build-extension.sh" "$APP/Contents/Library/SystemExtensions"
else
    echo "Skipping camera system extension (NEXUS_SKIP_CAMERA_EXTENSION set)"
fi

# Native dylibs (libe_sqlite3, libglfw, etc) are dlopen'd at runtime from
# AppContext.BaseDirectory. Publish drops them at the staging root next to
# the exe; mirror that placement inside the bundle. Single-level glob is
# deliberate - openrgb/*.dylib must stay alongside the openrgb binary, not
# get flattened up next to Nexus.
shopt -s nullglob
for dylib in "$STAGING"/*.dylib; do
    cp "$dylib" "$APP/Contents/MacOS/"
done
shopt -u nullglob

# Copy Info.plist + .icns (legacy fallback for macOS < 11)
cp "$SCRIPT_DIR/Info.plist" "$APP/Contents/"
[ -f "$SCRIPT_DIR/AppIcon.icns" ] && cp "$SCRIPT_DIR/AppIcon.icns" "$APP/Contents/Resources/"

# Compile Assets.xcassets -> Assets.car (modern icon: edge-to-edge artwork
# + appearance variants for Sequoia tinted Dock). actool also emits a
# partial Info.plist with the icon keys it expects (CFBundleIconName etc).
# Keep the hand-built AppIcon.icns above as the fallback path - actool can
# generate one too, but ours preserves more detail at small sizes.
if [ -d "$SCRIPT_DIR/Assets.xcassets" ] && command -v actool >/dev/null; then
    ACTOOL_TMP="$(mktemp -d)"
    trap 'rm -rf "$ACTOOL_TMP"' EXIT
    actool "$SCRIPT_DIR/Assets.xcassets" \
        --compile "$ACTOOL_TMP" \
        --platform macosx \
        --minimum-deployment-target 12.0 \
        --app-icon AppIcon \
        --output-partial-info-plist "$ACTOOL_TMP/partial.plist" \
        > /dev/null
    if [ ! -f "$ACTOOL_TMP/Assets.car" ]; then
        echo "actool produced no Assets.car - check --app-icon name matches the .appiconset" >&2
        exit 1
    fi
    cp "$ACTOOL_TMP/Assets.car" "$APP/Contents/Resources/"
fi

# Ensure the binary is executable
chmod +x "$APP/Contents/MacOS/Nexus"

# Only Mach-O code may live directly under Contents/MacOS. codesign treats any
# data file there (html, fonts, png, README) as unsigned nested code and the
# bundle seal fails notarization. Relocate every data item (and the openrgb/adb
# child-process dirs) into Contents/Resources and symlink it back where the
# service resolves it (AppContext.BaseDirectory is Contents/MacOS).
# Relocate a data item only when it exists, so absent items leave no dangling
# symlink (a broken link breaks xattr/codesign over the bundle).
relocate_to_resources() {
    local item="$1"
    local src="$APP/Contents/MacOS/$item"
    [ -L "$src" ] && return 0
    if [ -e "$src" ]; then
        rm -rf "$APP/Contents/Resources/$item"
        mv "$src" "$APP/Contents/Resources/$item"
        ln -s "../Resources/$item" "$src"
    fi
    return 0
}
mkdir -p "$APP/Contents/Resources"
# wwwroot is often populated AFTER this script (BuildWeb=false). Always provide
# Resources/wwwroot + the symlink so a later copy into MacOS/wwwroot lands in
# Resources and the bundle still seals.
if [ -d "$APP/Contents/MacOS/wwwroot" ] && [ ! -L "$APP/Contents/MacOS/wwwroot" ]; then
    rm -rf "$APP/Contents/Resources/wwwroot"
    mv "$APP/Contents/MacOS/wwwroot" "$APP/Contents/Resources/wwwroot"
fi
mkdir -p "$APP/Contents/Resources/wwwroot"
[ -L "$APP/Contents/MacOS/wwwroot" ] || ln -s "../Resources/wwwroot" "$APP/Contents/MacOS/wwwroot"
for _item in widgets openrgb tools status-icon.png "status-icon@2x.png"; do
    relocate_to_resources "$_item"
done

# Remove extended attributes that would trigger Gatekeeper quarantine warnings
xattr -cr "$APP" 2>/dev/null || true

# Optional dev signing: the system extension only activates from a bundle
# signed with a real identity (plus a provisioning profile carrying the
# system-extension.install entitlement). Default builds stay ad-hoc; CI and
# smoke builds are unaffected. Set:
#   NEXUS_MAC_SIGN_IDENTITY  codesign identity string
#   NEXUS_MAC_PROFILE        provisioning profile to embed (optional)
#   NEXUS_MAC_APP_ENTITLEMENTS  app entitlements plist (optional)
if [ -n "${NEXUS_MAC_SIGN_IDENTITY:-}" ]; then
    # The extension is NOT re-signed here: xcodebuild already signed it with
    # build-setting expansion ($(TeamIdentifierPrefix) in the app group and
    # mach name). codesign cannot expand those, so a re-sign with the raw
    # entitlements file ships literal variables and the cmio category
    # delegate rejects the extension at activation.
    [ -n "${NEXUS_MAC_PROFILE:-}" ] && cp "$NEXUS_MAC_PROFILE" "$APP/Contents/embedded.provisionprofile"
    # Sign the main binary, not the bundle: data trees under Contents/MacOS
    # (wwwroot, openrgb, tools/) break whole-bundle resource sealing, and
    # sysextd validates the requesting process signature + entitlement, not
    # the outer seal. A stale partial seal must go or verification trips.
    rm -rf "$APP/Contents/_CodeSignature"
    # Sign the binary outside the bundle: in place, codesign promotes the
    # main executable to a whole-bundle sign and trips on the data trees.
    # The signature lives in the Mach-O and survives the move back.
    SIGN_TMP="$(mktemp -d)/Nexus"
    cp "$APP/Contents/MacOS/Nexus" "$SIGN_TMP"
    if [ -n "${NEXUS_MAC_APP_ENTITLEMENTS:-}" ]; then
        codesign --force --identifier com.hellonexus.panel.service --entitlements "$NEXUS_MAC_APP_ENTITLEMENTS" -s "$NEXUS_MAC_SIGN_IDENTITY" "$SIGN_TMP"
    else
        codesign --force --identifier com.hellonexus.panel.service -s "$NEXUS_MAC_SIGN_IDENTITY" "$SIGN_TMP"
    fi
    mv "$SIGN_TMP" "$APP/Contents/MacOS/Nexus"
    chmod +x "$APP/Contents/MacOS/Nexus"
    echo "Signed: $APP/Contents/MacOS/Nexus ($NEXUS_MAC_SIGN_IDENTITY)"
fi

echo "Built: $APP"
