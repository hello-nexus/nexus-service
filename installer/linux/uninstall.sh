#!/usr/bin/env bash
# Remove the Nexus root daemon. Needs sudo.
set -euo pipefail

echo "==> Stopping + disabling the system service (sudo)"
sudo systemctl disable --now nexus.service 2>/dev/null || true
sudo rm -f /etc/systemd/system/nexus.service
sudo systemctl daemon-reload 2>/dev/null || true

echo "==> Removing app + menu entry (sudo)"
sudo rm -rf /opt/nexus
# Mirror install.sh: under sudo, $HOME is root's, not where the entry was written.
TARGET_HOME="$HOME"
if [ -n "${SUDO_USER:-}" ]; then
  SUDO_HOME="$(getent passwd "$SUDO_USER" | cut -d: -f6)"
  [ -n "$SUDO_HOME" ] && TARGET_HOME="$SUDO_HOME"
fi
rm -f "$TARGET_HOME/.local/share/applications/nexus.desktop"
rm -f "$TARGET_HOME/.local/share/icons/hicolor/512x512/apps/nexus.png"

echo "Nexus removed. Settings, profiles and media were left in /var/lib/nexus;"
echo "delete it by hand to wipe them. The it87/sensors module config under"
echo "/etc/modules-load.d + /etc/modprobe.d (if installed) and any DKMS module"
echo "were left in place."
