#!/usr/bin/env bash
# Install Nexus as a ROOT system daemon (like coolercontrol's coolercontrold)
# for full hardware access - motherboard pwm, NVML GPU fans, kernel modules,
# raw i2c/hidraw - with no udev rules or group membership to juggle. The daemon
# adopts the active user's login session at startup so the tray, MPRIS media,
# volume, and dashboard still work. Needs sudo.
# Immutable-distro friendly (Bazzite/rpm-ostree): /opt and /etc are writable.
set -euo pipefail

# setup-sensors.sh sets the ACPI kernel parameter that unblocks the chipset
# SMBus (RGB RAM, SMBus board controllers) when it finds the firmware blocking
# it. --no-smbus leaves the boot config alone and prints the command instead.
NEXUS_ENABLE_SMBUS=1
for arg in "$@"; do
  case "$arg" in
    --no-smbus) NEXUS_ENABLE_SMBUS=0 ;;
    -h|--help) echo "usage: install.sh [--no-smbus]"; exit 0 ;;
  esac
done
export NEXUS_ENABLE_SMBUS

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
# sudo's env_reset drops exported variables, so the opt-out is passed explicitly.
sudo NEXUS_ENABLE_SMBUS="$NEXUS_ENABLE_SMBUS" bash "$HERE/setup-sensors.sh" \
  || echo "   (sensor driver setup skipped)"

echo "==> Installing + enabling the system service (sudo)"
sudo cp "$HERE/nexus.service" "$UNIT"
sudo restorecon "$UNIT" 2>/dev/null || true
# An older unit was WantedBy=multi-user.target. `systemctl enable` only manages
# the symlinks the CURRENT [Install] names, so that one survives every upgrade
# and forms an ordering cycle (graphical.target is after multi-user.target),
# which systemd breaks by deleting this unit's start job: Nexus then never
# starts at boot and has to be started by hand after every reboot.
# Only where graphical.target is the default - on a headless box it never
# activates, so that symlink is the only thing that starts the service.
STALE_WANTS=/etc/systemd/system/multi-user.target.wants/nexus.service
if [ -e "$STALE_WANTS" ]; then
  if [ "$(systemctl get-default 2>/dev/null)" = "graphical.target" ]; then
    sudo rm -f "$STALE_WANTS"
    echo "   removed a stale multi-user.target link that stopped Nexus starting at boot"
  else
    echo "   this system boots to $(systemctl get-default 2>/dev/null); keeping the"
    echo "   multi-user.target link so Nexus still starts without a graphical session"
  fi
fi
sudo systemctl daemon-reload
sudo systemctl enable nexus.service
# restart, not `enable --now`: --now leaves an already-running unit alone, so an
# upgrade would keep serving the old binary until the next boot.
sudo systemctl restart nexus.service

# Preflight the two runtime dependencies Nexus cannot bundle. Both fail
# silently at the far end otherwise - openrgb-headless exits before it opens
# its port (RGB just never appears), and the panel kiosk has nothing to spawn -
# so say it here, once, while the user is still looking at a terminal.
echo
MISSING_LIBS="$(ldd "$APP_DIR/openrgb/openrgb-headless" 2>/dev/null | awk '/not found/ {print $1}' | sort -u || true)"
if [ -n "$MISSING_LIBS" ]; then
  echo "WARNING: the RGB engine is missing shared libraries, so lighting will not come up:"
  printf '  %s\n' $MISSING_LIBS
  echo "  Debian/Ubuntu/Mint: sudo apt install libhidapi-hidraw0 libusb-1.0-0"
  echo "  Fedora/Bazzite:     sudo dnf install hidapi libusb1"
  echo "  Arch:               sudo pacman -S hidapi libusb"
fi

# Mirrors LinuxBrowsers.SearchDirs/BinaryNames: a narrower list here would warn
# about a browser the service goes on to find.
HAVE_BROWSER=0
BROWSER_DIRS=(
  "$TARGET_HOME/.local/share/flatpak/exports/bin" /var/lib/flatpak/exports/bin
  /usr/bin /usr/local/bin /bin /opt/bin /snap/bin /var/lib/snapd/snap/bin
  "$TARGET_HOME/.local/bin"
)
BROWSER_NAMES=(
  org.chromium.Chromium com.google.Chrome com.brave.Browser com.microsoft.Edge
  chromium chromium-browser chromium-freeworld ungoogled-chromium
  google-chrome google-chrome-stable brave brave-browser
  microsoft-edge microsoft-edge-stable vivaldi vivaldi-stable
  thorium-browser helium
)
for dir in "${BROWSER_DIRS[@]}"; do
  for name in "${BROWSER_NAMES[@]}"; do
    [ -e "$dir/$name" ] && HAVE_BROWSER=1 && break 2
  done
done
if [ "$HAVE_BROWSER" = 0 ]; then
  echo "WARNING: no Chromium-family browser found. The Y70 panel and any promoted"
  echo "  monitor run as a Chromium --app --kiosk window and cannot open without one."
  echo "  Install chromium, chrome, brave, edge or vivaldi (package, flatpak or"
  echo "  snap), then: sudo systemctl restart nexus"
fi

echo
echo "Nexus installed as a root daemon. Dashboard: http://localhost:9400"
echo "Data lives in $DATA_DIR. Hardware control starts at boot; the tray and"
echo "media controls attach to your login session as soon as you log in."
