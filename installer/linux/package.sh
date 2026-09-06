#!/usr/bin/env bash
# Assemble a Nexus Linux tarball from a `dotnet publish -r linux-x64` output dir.
# Usage: package.sh <publish-dir> [output-dir]
# Produces <output-dir>/Nexus-Linux-x64.tar.gz containing a top-level nexus/
# directory (app payload + install scripts + systemd unit).
set -euo pipefail

PUBLISH_DIR="${1:?usage: package.sh <publish-dir> [output-dir]}"
OUT_DIR="${2:-$(pwd)}"
HERE="$(cd "$(dirname "$0")" && pwd)"

if [ ! -e "$PUBLISH_DIR/Nexus" ]; then
  echo "error: $PUBLISH_DIR/Nexus not found - run 'dotnet publish -r linux-x64 -o $PUBLISH_DIR' first" >&2
  exit 1
fi

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
APP="$STAGE/nexus"
mkdir -p "$APP"

# App payload (binary + wwwroot + openrgb + ffmpeg + widgets + native libs).
cp -a "$PUBLISH_DIR/." "$APP/"

# Install tooling + service definition.
cp "$HERE/install.sh" "$HERE/uninstall.sh" "$HERE/README.md" \
   "$HERE/nexus.service" "$HERE/setup-sensors.sh" "$APP/"
chmod +x "$APP/install.sh" "$APP/uninstall.sh" "$APP/setup-sensors.sh" "$APP/Nexus"

# App icon (best-effort - menu entry uses it).
ICON="$HERE/../../Bundled/linux/nexus-512.png"
[ -f "$ICON" ] && cp "$ICON" "$APP/nexus.png" || true

# Fail if wwwroot/assets holds a bundle unreachable from index.html. The
# BuildWeb=false publish path skips the wwwroot wipe in Nexus.Service.csproj, so
# bundles from earlier builds accumulate; this asserts every bundle belongs to
# the current build. Closure: seed from index.html, expand through inter-chunk
# references (Vite hashed basenames), flag the rest.
verify_wwwroot_clean() {
  local assets="$1/assets" index="$1/index.html"
  [ -d "$assets" ] && [ -f "$index" ] || return 0
  local tmp; tmp="$(mktemp -d)"
  ls -1 "$assets" 2>/dev/null | grep -E '\.(js|css)$' | sort -u > "$tmp/all" || true
  while read -r f; do
    if grep -qF "$f" "$index"; then printf '%s\n' "$f"; fi
  done < "$tmp/all" | sort -u > "$tmp/reach"
  while :; do
    local before; before="$(wc -l < "$tmp/reach")"
    while read -r f; do
      case "$f" in
        *.js) while read -r g; do
                if grep -qF "$g" "$assets/$f"; then printf '%s\n' "$g"; fi
              done < "$tmp/all" ;;
      esac
    done < "$tmp/reach" | cat - "$tmp/reach" | sort -u > "$tmp/reach.next"
    mv "$tmp/reach.next" "$tmp/reach"
    [ "$before" = "$(wc -l < "$tmp/reach")" ] && break
  done
  local count orphans
  count="$(wc -l < "$tmp/all")"
  orphans="$(comm -23 "$tmp/all" "$tmp/reach")"
  rm -rf "$tmp"
  if [ -n "$orphans" ]; then
    echo "error: wwwroot has orphaned bundles (stale build artifacts not referenced by index.html):" >&2
    printf '%s\n' "$orphans" | sed 's/^/  /' >&2
    echo "rebuild nexus-web (which wipes wwwroot) before packaging" >&2
    exit 1
  fi
  echo "wwwroot clean: $count bundles, 0 orphaned"
}
verify_wwwroot_clean "$APP/wwwroot"

mkdir -p "$OUT_DIR"
tar -czf "$OUT_DIR/Nexus-Linux-x64.tar.gz" -C "$STAGE" nexus
echo "Wrote $OUT_DIR/Nexus-Linux-x64.tar.gz"
