# Nexus for Linux (x86-64)

A single `linux-x64` build covers mainstream **glibc x86-64 desktop Linux** -
Ubuntu/Debian, Fedora (incl. Bazzite/SteamOS-like), Arch, openSUSE, Pop!_OS,
Mint. Not covered: musl distros (Alpine) and ARM64.

## Install

```sh
tar xf Nexus-Linux-x64.tar.gz
cd nexus
./install.sh
```

This installs Nexus as a **root systemd daemon** for full hardware access
(motherboard PWM, NVML GPU fans, kernel modules, raw i2c/hidraw) with no
udev rules or group membership to juggle:

- app in `/opt/nexus`, unit at `/etc/systemd/system/nexus.service`
  (enabled and started immediately)
- data in `/var/lib/nexus` - settings, profiles, device media, apps, caches
  and logs, one machine-scope root like `%ProgramData%` on Windows
- an application menu entry that opens the dashboard
- the motherboard Super-I/O sensor driver, loaded via `setup-sensors.sh`

Upgrading from a build that stored data under your home (`~/.config/Nexus`,
`~/.local/share/Nexus`, `~/.cache/Nexus`, `~/.local/state/nexus/logs`)?
`install.sh` copies it into `/var/lib/nexus` the first time, and leaves the
originals alone so you can delete them once the upgrade looks right.

Needs `sudo`. Immutable-distro friendly (Bazzite/rpm-ostree): `/opt` and
`/etc` are writable there.

Open the dashboard at <http://localhost:9400>.

> Hardware control - fans, pump, RGB - comes up at boot and never waits for a
> login. The tray, MPRIS media, volume and dashboard need a graphical session,
> so the daemon adopts yours as soon as one appears, whether that is at
> startup or when you log in later.

## Manage

```sh
systemctl status nexus            # state
sudo systemctl restart nexus      # restart
journalctl -u nexus -f            # logs
```

## Uninstall

From the extracted tarball directory (`uninstall.sh` is not copied to
`/opt/nexus`):

```sh
./uninstall.sh
```

Your data in `/var/lib/nexus` is left in place - remove it by hand to wipe
settings, profiles and media. The sensors module config under
`/etc/modules-load.d` + `/etc/modprobe.d` and any DKMS module are left too.

## What works on Linux

Sensors (hwmon), CPU/GPU performance, network, USB enumeration, screen-time,
shortcuts, weather, audio-reactive lighting, system tray, start at boot -
plus, in this build: RGB (OpenRGB, native i2c/hidraw), HYTE serial devices
(NP50 / MiniHub / CNVS / Q-series cooler / Y70), the Q-series panel (Q60 /
Q80, driven over the bundled adb; no USB-reset recovery on Linux yet),
motherboard fan control (hwmon PWM), keyboard macros (uinput), media (MPRIS), volume
(PipeWire/PulseAudio), display brightness (backlight + DDC/CI), and
screen-mirror lighting (xdg-desktop-portal ScreenCast).

The Y70 panel and any promoted monitor run as a Chromium-family kiosk window,
so they need `chromium`, `chrome`, `brave` or `edge` installed - by package or
flatpak. Display layout is the compositor's, and Nexus renders to whatever
geometry it gives the kiosk:

- Rotate the Y70 to portrait and keep your main monitor primary in the
  desktop's display settings (KDE persists this in `kwinoutputconfig.json`).
- Wayland gives clients no way to pick an output, and KWin puts a new
  fullscreen window on the primary screen. On KDE the service loads a small
  KWin script (`nexus-panel-y70`, alongside the `nexus-focus` one) that moves
  the kiosk onto the portrait strip and keeps it fullscreen. Other
  compositors need their own equivalent.

Not available on Linux: the in-game FPS overlay and the floating
desktop-widget overlay (no viable host).
