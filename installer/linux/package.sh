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
  echo "error: $PUBLISH_DIR/Nexus not found — run 'dotnet publish -r linux-x64 -o $PUBLISH_DIR' first" >&2
  exit 1
fi

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
APP="$STAGE/nexus"
mkdir -p "$APP"

# App payload (binary + wwwroot + openrgb + ffmpeg + apps + native libs).
cp -a "$PUBLISH_DIR/." "$APP/"

# Install tooling + service definition.
cp "$HERE/install.sh" "$HERE/uninstall.sh" "$HERE/README.md" \
   "$HERE/nexus.service" "$APP/"
chmod +x "$APP/install.sh" "$APP/uninstall.sh" "$APP/Nexus"

# App icon (best-effort — menu entry uses it).
ICON="$HERE/../../Bundled/linux/nexus-512.png"
[ -f "$ICON" ] && cp "$ICON" "$APP/nexus.png" || true

mkdir -p "$OUT_DIR"
tar -czf "$OUT_DIR/Nexus-Linux-x64.tar.gz" -C "$STAGE" nexus
echo "Wrote $OUT_DIR/Nexus-Linux-x64.tar.gz"
