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
# Under `sudo ./install.sh`, $HOME is root's - resolve the invoking user so the
# menu entry and the data migration below both target the same real home.
TARGET_HOME="$HOME"
if [ -n "${SUDO_USER:-}" ]; then
  SUDO_HOME="$(getent passwd "$SUDO_USER" | cut -d: -f6)"
  [ -n "$SUDO_HOME" ] && TARGET_HOME="$SUDO_HOME"
fi
APPS_DIR="$TARGET_HOME/.local/share/applications"
ICON_DIR="$TARGET_HOME/.local/share/icons/hicolor/512x512/apps"

# Stop a running daemon first: copying onto the live /opt/nexus/Nexus is ETXTBSY
# and would abort the upgrade under set -e, and the migration below must not
# snapshot a store the old daemon is still writing.
sudo systemctl stop nexus.service 2>/dev/null || true

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

# The daemon keeps everything under one machine-scope root - settings.json, db/,
# devices/, media/, apps/, firmware/, drivers/, logs/ - the way the Windows
# service uses %ProgramData%. It has no home of its own, and the logged-in
# user's home is unknowable before login, so deriving these from $HOME gave the
# daemon one config when it started at boot and a different one after a
# restart. World-readable so you can open logs/ in a file manager; the service
# chmods settings.json and the HTTPS key to 0600 itself.
DATA_DIR=/var/lib/nexus
echo "==> Preparing $DATA_DIR (sudo)"
# The unit's StateDirectory= creates this on every start; doing it here too lets
# the migration below run before the first start.
sudo install -d -m 0755 "$DATA_DIR"

# One-shot upgrade from the pre-machine-root layout: move the installing user's
# existing stores in. Only ever runs while $DATA_DIR is still empty, so a
# re-install can't bury the live config under a stale copy.
# TARGET_HOME (resolved at the top) matters here: migrating /root instead of the
# user's home would hand them a blank config - the very failure this removes.
if [ -z "$(sudo ls -A "$DATA_DIR" 2>/dev/null)" ]; then
  MIGRATED=0
  # <old dir>:<name under DATA_DIR>, empty name = merge at the root.
  for spec in \
    "$TARGET_HOME/.config/Nexus:" \
    "$TARGET_HOME/.local/share/Nexus:" \
    "$TARGET_HOME/.cache/Nexus:" \
    "$TARGET_HOME/.local/state/nexus/logs:logs"; do
    src="${spec%:*}"
    dest="${spec##*:}"
    [ -d "$src" ] || continue
    if [ -n "$dest" ]; then
      sudo cp -a "$src" "$DATA_DIR/$dest"
    else
      sudo cp -a "$src/." "$DATA_DIR/"
    fi
    echo "    migrated $src"
    MIGRATED=1
  done
  if [ "$MIGRATED" = 1 ]; then
    # cp -a preserves ownership, permissions AND the SELinux context, so the
    # copies arrive owned by the user and labelled user_home_t - unusable to a
    # confined system service. Re-stamp all three.
    sudo chown -R root:root "$DATA_DIR"
    sudo chmod -R go-w "$DATA_DIR"
    sudo find "$DATA_DIR" -type d -exec chmod go+rx {} +
    [ -d "$DATA_DIR/db" ] && sudo chmod -R go-rwx "$DATA_DIR/db"
    # The glob matters: .tmp (stranded by a crash mid-write) and .corrupt carry
    # the same auth and cloud tokens as settings.json itself.
    sudo chmod go-rwx "$DATA_DIR"/settings.json* 2>/dev/null || true
    [ -f "$DATA_DIR/nexus-local-https.pfx" ] && sudo chmod go-rwx "$DATA_DIR/nexus-local-https.pfx"
    sudo restorecon -R "$DATA_DIR" 2>/dev/null || true
    echo "    old copies left in place - delete them once you have confirmed the upgrade"
  fi
fi

# Load the motherboard Super-I/O fan driver (it87 etc.). Root daemon reads/writes
# hwmon directly; this only ensures the kernel module is present.
sudo bash "$HERE/setup-sensors.sh" || echo "   (sensor driver setup skipped)"

echo "==> Installing + enabling the system service (sudo)"
sudo cp "$HERE/nexus.service" "$UNIT"
sudo restorecon "$UNIT" 2>/dev/null || true
sudo systemctl daemon-reload
sudo systemctl enable nexus.service
# restart, not `enable --now`: --now leaves an already-running unit alone, so an
# upgrade would keep serving the old binary until the next boot.
sudo systemctl restart nexus.service

echo
echo "Nexus installed as a root daemon. Dashboard: http://localhost:9400"
echo "Data lives in $DATA_DIR. Hardware control starts at boot; the tray and"
echo "media controls attach to your login session as soon as you log in."
