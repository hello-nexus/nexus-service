#!/usr/bin/env bash
# Install Nexus as a ROOT system daemon (like coolercontrol's coolercontrold)
# for full hardware access - motherboard pwm, NVML GPU fans, kernel modules,
# raw i2c/hidraw - with no udev rules or group membership to juggle. The daemon
# adopts the active user's login session at startup so the tray, MPRIS media,
# volume, and dashboard still work. Needs sudo.
# Immutable-distro friendly (Bazzite/rpm-ostree): /opt and /etc are writable.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
APP_DIR=/opt/nexus
UNIT=/etc/systemd/system/nexus.service
APPS_DIR="$HOME/.local/share/applications"
ICON_DIR="$HOME/.local/share/icons/hicolor/512x512/apps"

echo "==> Installing Nexus (root daemon) to $APP_DIR (sudo)"
sudo mkdir -p "$APP_DIR"
for entry in "$HERE"/* "$HERE"/.[!.]*; do
  [ -e "$entry" ] || continue
  case "$(basename "$entry")" in
    install.sh|uninstall.sh|README.md|nexus.service|setup-sensors.sh) continue ;;
  esac
  sudo cp -a "$entry" "$APP_DIR/"
done
sudo chmod +x "$APP_DIR/Nexus"
[ -f "$APP_DIR/openrgb/openrgb-headless" ] && sudo chmod +x "$APP_DIR/openrgb/openrgb-headless" || true
# SELinux: a system service can't exec from a user home (user_home_t). restorecon
# stamps the default context so systemd can launch it - except on ostree distros
# (Bazzite/Silverblue), where /opt is a symlink to /var/opt and the default there
# is var_t, which systemd refuses to exec (203/EXEC). Pin bin_t on the physical
# path first so restorecon stamps the right label.
APP_REAL="$(readlink -f "$APP_DIR")"
if [ "$APP_REAL" != "$APP_DIR" ] && command -v semanage >/dev/null 2>&1; then
  sudo semanage fcontext -a -t bin_t "${APP_REAL}(/.*)?" >/dev/null 2>&1 || true
fi
sudo restorecon -R "$APP_REAL" 2>/dev/null || true
# No semanage (or a policy that still resolves to var_t): label it directly.
if command -v selinuxenabled >/dev/null 2>&1 && selinuxenabled 2>/dev/null \
   && ! ls -Z "$APP_REAL/Nexus" 2>/dev/null | grep -q bin_t; then
  sudo chcon -R -t bin_t "$APP_REAL" 2>/dev/null || true
fi

# Desktop menu entry just opens the dashboard - the binary is the service now,
# not a user-launched app. (The tray's "Open Dashboard" gives the --app window.)
mkdir -p "$APPS_DIR" "$ICON_DIR"
[ -f "$APP_DIR/nexus.png" ] && cp "$APP_DIR/nexus.png" "$ICON_DIR/nexus.png" 2>/dev/null || true
cat > "$APPS_DIR/nexus.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Nexus
Comment=Nexus hardware monitoring and control
Exec=xdg-open http://localhost:9400
Icon=nexus
Terminal=false
Categories=Utility;System;
EOF

# Load the motherboard Super-I/O fan driver (it87 etc.). Root daemon reads/writes
# hwmon directly; this only ensures the kernel module is present.
sudo bash "$HERE/setup-sensors.sh" || echo "   (sensor driver setup skipped)"

echo "==> Installing + enabling the system service (sudo)"
sudo cp "$HERE/nexus.service" "$UNIT"
sudo restorecon "$UNIT" 2>/dev/null || true
sudo systemctl daemon-reload
sudo systemctl enable --now nexus.service

echo
echo "Nexus installed as a root daemon. Dashboard: http://localhost:9400"
echo "Tray + media attach to your login session at startup - if you installed"
echo "before logging in, run:  sudo systemctl restart nexus"
