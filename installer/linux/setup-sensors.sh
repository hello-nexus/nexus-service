#!/usr/bin/env bash
# Bring up the two kernel interfaces Nexus needs from the motherboard: the
# chipset SMBus (i2c-dev + the host driver) that OpenRGB reads RGB RAM and
# board controllers over, and the Super-I/O sensor chip that exposes fans
# through hwmon - the same universal layer every Linux fan tool (lm-sensors,
# fancontrol, CoolerControl) uses.
#
# SMBus first, unconditionally: it is independent of the fan story below, and
# every early exit there would otherwise skip it.
#
# Super-I/O strategy, lightest first:
#   1. sensors-detect --auto loads the in-tree driver (nct6775, in-tree it87,
#      w83627…). This covers the large majority of boards - no build needed.
#   2. If that exposes controllable PWM, persist the loaded modules and stop.
#   3. Newest ITE chips (e.g. IT8689E / IT8696E) aren't in the mainline it87.
#      When we see an "unknown ITE ID" and DKMS is available, build+install the
#      maintained frankcrawford/it87 fork (the same module CoolerControl's docs
#      point users to). Otherwise print the one-line install hint and move on -
#      Nexus still works, fans just stay BIOS-controlled until the driver lands.
#
# Run as root (install.sh calls it via sudo). Never writes to /usr; module
# autoload + options go under /etc, which is writable on immutable distros.
set -euo pipefail

IT87_FORK="https://github.com/frankcrawford/it87"
MODLOAD=/etc/modules-load.d/nexus-sensors.conf
MODOPTS=/etc/modprobe.d/nexus-sensors.conf
GRUB_BACKUP=/etc/default/grub.nexus-backup

log(){ echo "   $*"; }
have_pwm(){ compgen -G "/sys/class/hwmon/hwmon*/pwm[0-9]" >/dev/null 2>&1; }

# USB liquid coolers / AIOs / fan hubs are driven through the liquidctl CLI
# (Nexus auto-detects it at runtime). We only detect + guide - installing a
# system package or touching the user's Python env without consent isn't ours
# to do. Most distros: `<pkg-mgr> install liquidctl`.
if ! command -v liquidctl >/dev/null 2>&1; then
  echo "==> USB coolers (NZXT/Corsair/etc.): install 'liquidctl' to enable them"
  echo "    e.g. dnf install liquidctl  |  apt install liquidctl  |  pacman -S liquidctl"
fi

# SMBus modules loaded below; every MODLOAD writer re-includes them so a later
# write cannot drop them.
SMBUS_MODS=""

write_modload(){
  { printf '# Written by Nexus setup-sensors.sh\n'
    for m in $SMBUS_MODS "$@"; do printf '%s\n' "$m"; done
  } > "$MODLOAD"
}

# RGB on RAM, and many motherboard controllers, sit behind the chipset SMBus,
# which OpenRGB reaches through /dev/i2c-*. Those nodes need i2c-dev plus the
# host SMBus driver; loading the wrong vendor's driver is a no-op, so try both
# rather than branch on CPU vendor.
#
# Loading them is necessary but often not sufficient. On AM5 and other recent
# boards the firmware's ACPI tables claim the SMBus I/O region, so i2c-piix4
# binds nothing and fails its probe silently - the kernel only says
# "ACPI: OSL: Resource conflict". i2c-piix4 has no per-module bypass (unlike
# it87's ignore_resource_conflict) and acpi_enforce_resources is not writable at
# runtime, so a kernel parameter plus a reboot is the only route - taken only
# when the user opts in, since it relaxes ACPI enforcement machine-wide.
smbus_present(){
  grep -lsi smbus /sys/class/i2c-dev/i2c-*/name >/dev/null 2>&1
}

# The kernel ring buffer wraps, so prefer the persistent journal where there is
# one. Counted, never `grep -q`: -q exits on the first match and SIGPIPEs the
# producer, which under `set -o pipefail` makes the whole test read as false.
kernel_log(){
  if command -v journalctl >/dev/null 2>&1; then
    journalctl -k -b --no-pager 2>/dev/null
  else
    dmesg 2>/dev/null
  fi
}

# An ACPI OpRegion clash over the SMBus region specifically. The generic
# "OSL: Resource conflict" line alone is not enough - the Super-I/O conflict
# this script works around at step 5 prints the identical text.
smbus_acpi_conflict(){
  local logtext
  logtext="$(kernel_log || true)"
  [ "$(printf '%s' "$logtext" | grep -ci "OSL: Resource conflict" || true)" != 0 ] || return 1
  [ "$(printf '%s' "$logtext" \
       | grep -i "SystemIO range" \
       | grep -ci "SMB" || true)" != 0 ]
}

setup_smbus(){
  # i2c-dev provides the /dev/i2c-* nodes and is always worth keeping. A host
  # driver loads on the wrong vendor's board too, so persist one only once an
  # adapter has actually appeared.
  modprobe i2c-dev >/dev/null 2>&1 && SMBUS_MODS="i2c-dev" || true
  for m in i2c-piix4 i2c-i801; do
    modprobe "$m" >/dev/null 2>&1 || true
    [ "$(lsmod | grep -c "^${m//-/_} " || true)" != 0 ] || continue
    if smbus_present; then SMBUS_MODS="$SMBUS_MODS $m"; break; fi
  done
  [ -n "$SMBUS_MODS" ] && write_modload
  if smbus_present; then
    log "SMBus ready - RGB RAM and board controllers are visible to OpenRGB"
    return 0
  fi
  log "no SMBus adapter - RGB RAM and SMBus board controllers stay hidden"
  # Only the firmware conflict is fixable; a board with no SMBus controller at
  # all cannot be helped by a kernel parameter. The conflict must be the SMBus
  # one: the same "OSL: Resource conflict" line covers any ACPI OpRegion clash,
  # including the Super-I/O one step 5 below works around, which is logged on
  # every boot once it87 is persisted.
  smbus_acpi_conflict || return 0
  if grep -qs acpi_enforce_resources=lax /proc/cmdline; then
    log "the ACPI override is already active, so this board exposes no usable"
    log "SMBus controller"
    return 0
  fi
  log "cause: ACPI claimed the SMBus region"
  [ "${NEXUS_ENABLE_SMBUS:-1}" = 1 ] || { print_smbus_hint; return 0; }
  apply_acpi_lax
}

print_smbus_hint(){
  log "to enable it yourself, set this kernel parameter and reboot:"
  if command -v rpm-ostree >/dev/null 2>&1; then
    log "  sudo rpm-ostree kargs --append=acpi_enforce_resources=lax"
  else
    log "  add acpi_enforce_resources=lax to GRUB_CMDLINE_LINUX in"
    log "  /etc/default/grub, then update-grub"
  fi
}

# Edit GRUB_CMDLINE_LINUX and regenerate. Returns non-zero unless the parameter
# is verifiably in the file AND a regenerator ran, so the caller never reports
# success over a no-op: the variable is absent, unquoted or single-quoted often
# enough that a bare sed silently matches nothing.
apply_grub_karg(){
  local karg=acpi_enforce_resources=lax
  if ! grep -q "$karg" /etc/default/grub; then
    cp /etc/default/grub "$GRUB_BACKUP" || return 1
    if grep -q '^[[:space:]]*GRUB_CMDLINE_LINUX=' /etc/default/grub; then
      sed -i "s|^\([[:space:]]*GRUB_CMDLINE_LINUX=[\"']\{0,1\}[^\"']*\)|\1 $karg|" \
        /etc/default/grub || return 1
    else
      printf '\n# Added by Nexus setup-sensors.sh\nGRUB_CMDLINE_LINUX="%s"\n' "$karg" \
        >> /etc/default/grub || return 1
    fi
    grep -q "$karg" /etc/default/grub || {
      log "could not edit /etc/default/grub; restored from $GRUB_BACKUP"
      cp "$GRUB_BACKUP" /etc/default/grub || true
      return 1
    }
    log "previous grub config saved to $GRUB_BACKUP"
  fi
  { command -v update-grub >/dev/null 2>&1 && update-grub >/dev/null 2>&1; } && return 0
  local cfg
  for cfg in /boot/grub/grub.cfg /boot/grub2/grub.cfg /boot/efi/EFI/*/grub.cfg; do
    [ -f "$cfg" ] || continue
    grub2-mkconfig -o "$cfg" >/dev/null 2>&1 && return 0
    grub-mkconfig -o "$cfg" >/dev/null 2>&1 && return 0
  done
  log "edited /etc/default/grub but could not regenerate the boot config"
  return 1
}

# Reached only when this machine has no SMBus AND the kernel logged the SMBus
# ACPI conflict, so it never runs where it would not help. --no-smbus opts out.
# The parameter relaxes ACPI's exclusive claim on IO regions for every driver,
# not just ours, and needs a reboot.
apply_acpi_lax(){
  if command -v rpm-ostree >/dev/null 2>&1; then
    # The pending deployment, not /proc/cmdline: the parameter only reaches
    # /proc/cmdline after the reboot, so re-running before that would stack it.
    if [ "$(rpm-ostree kargs 2>/dev/null | grep -c acpi_enforce_resources=lax || true)" != 0 ]; then
      log "already set for the next boot - REBOOT to enable it"
      return 0
    fi
    rpm-ostree kargs --append=acpi_enforce_resources=lax >/dev/null 2>&1 \
      || { log "could not set the kernel parameter"; print_smbus_hint; return 0; }
  elif command -v grubby >/dev/null 2>&1; then
    # Fedora/RHEL/Nobara boot from BLS entries in /boot/loader/entries, which
    # grub2-mkconfig does not touch; grubby is the supported writer for both.
    grubby --update-kernel=ALL --args=acpi_enforce_resources=lax >/dev/null 2>&1 \
      || { log "could not set the kernel parameter"; print_smbus_hint; return 0; }
  elif [ -f /etc/default/grub ]; then
    apply_grub_karg || { print_smbus_hint; return 0; }
  else
    print_smbus_hint; return 0
  fi
  log "set acpi_enforce_resources=lax - REBOOT to enable RGB RAM and SMBus"
  log "board controllers"
}

# Record the SuperIO hwmon modules currently loaded so they re-load at boot.
persist_loaded(){
  local mods
  mods=$(for m in nct6775 nct6683 it87 w83627ehf w83627hf f71882fg; do
           lsmod | grep -q "^$m " && echo "$m"; done)
  [ -n "$mods" ] || return 0
  write_modload $mods
  log "persisted module autoload: $(echo $mods | tr '\n' ' ')"
}

echo "==> Enabling SMBus access (RGB RAM, motherboard controllers)"
setup_smbus || log "SMBus setup skipped; continuing with fan setup"

echo "==> Detecting motherboard sensor chip"

# 1. In-tree drivers via lm-sensors (answer every prompt with the default).
if command -v sensors-detect >/dev/null 2>&1; then
  yes '' | sensors-detect --auto >/tmp/nexus-sensors-detect.log 2>&1 || true
fi

# 2. Did we get controllable fans for free?
if have_pwm; then
  log "fan PWM exposed by in-tree driver"
  persist_loaded
  exit 0
fi

# 3. Look for an ITE chip the mainline driver rejected.
ite_id=$(grep -oiE 'unknown chip with ID 0x8[0-9a-f]{3}' /tmp/nexus-sensors-detect.log 2>/dev/null \
           | grep -oiE '0x8[0-9a-f]{3}' | head -1 || true)
if [ -z "$ite_id" ]; then
  log "no software-controllable fan controller found - fans stay BIOS-managed"
  exit 0
fi
log "found unsupported ITE Super-I/O chip $ite_id (not in mainline it87)"

# 4. Build the maintained it87 fork. DKMS is the portable, kernel-update-safe
#    path; without it we don't hand-roll an install - just guide the user.
if ! command -v dkms >/dev/null 2>&1; then
  log "DKMS not installed - install it plus kernel headers, then re-run, or"
  log "install the distro 'it87-dkms' package. Skipping fan-driver build."
  exit 0
fi
if [ ! -d "/lib/modules/$(uname -r)/build" ]; then
  log "kernel headers for $(uname -r) missing - install them and re-run. Skipping."
  exit 0
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
if ! git clone --depth 1 "$IT87_FORK" "$work/it87" >/dev/null 2>&1; then
  log "could not fetch it87 fork (offline?) - skipping"; exit 0
fi
# Fixed package name+version (NOT date-stamped) so re-running upgrades in place
# instead of orphaning a prior DKMS module. Remove any earlier copy first.
PKG=nexus-it87; VER=1.0; src="/usr/src/$PKG-$VER"
dkms remove "$PKG/$VER" --all >/dev/null 2>&1 || true
rm -rf "$src"; cp -a "$work/it87" "$src"
# Minimal dkms.conf if the fork ships none.
[ -f "$src/dkms.conf" ] || cat > "$src/dkms.conf" <<DKMS
PACKAGE_NAME="$PKG"
PACKAGE_VERSION="$VER"
BUILT_MODULE_NAME[0]="it87"
DEST_MODULE_LOCATION[0]="/extra"
AUTOINSTALL="yes"
DKMS

if dkms add -m "$PKG" -v "$VER" >/dev/null 2>&1 \
   && dkms install -m "$PKG" -v "$VER" >/dev/null 2>&1; then
  log "it87 fork installed via DKMS (survives kernel updates)"
else
  log "DKMS build failed - see 'dkms status'. Skipping fan-driver setup."
  exit 0
fi

# 5. Autoload it87 at boot. ignore_resource_conflict lets it bind past the ACPI
#    region claim that blocks it on many recent boards.
write_modload it87
printf '# Written by Nexus setup-sensors.sh\noptions it87 ignore_resource_conflict=1\n' > "$MODOPTS"
modprobe it87 ignore_resource_conflict=1 2>/dev/null || true
have_pwm && log "fan PWM now available via it87" || log "it87 loaded; verify fan headers in Nexus"
exit 0
