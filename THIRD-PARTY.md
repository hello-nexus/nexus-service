# Third-Party Software

This service bundles third-party software which is distributed under separate
licenses. Their respective license texts ship alongside the bundled binaries.

## OpenRGB (headless build)

- **Project**: OpenRGB
- **Upstream**: https://gitlab.com/CalcProgrammer1/OpenRGB
- **Headless fork (source code, GPLv2 §3 compliance)**: https://github.com/hello-nexus/openrgb-headless
- **License**: GNU General Public License version 2 or later (GPL-2.0-or-later)
- **License text**: shipped with the bundled binary at
  `openrgb/LICENSE-OpenRGB.txt` in the publish output
- **Communication boundary**: This service launches the bundled OpenRGB binary
  as a child process and communicates with it exclusively over a local TCP
  socket (the OpenRGB SDK protocol on `127.0.0.1:6742`). No part of this service
  links against, dynamically loads, or calls into OpenRGB's address space. Per
  the GPL FAQ this constitutes "mere aggregation" via IPC, not a derivative work.
- **Modifications**: The headless fork strips the Qt5 dependency entirely
  (Widgets, Gui, Core, DBus) so the SDK server can ship as a small Qt-less
  binary. The patches are tiny and viewable in the fork's diff against
  upstream `master`.

## FFmpeg (minimal build)

- **Project**: FFmpeg
- **Upstream**: https://ffmpeg.org
- **License**: GNU Lesser General Public License version 2.1 or later
  (LGPL-2.1-or-later)
- **License text**: shipped with the bundled binary at `ffmpeg/LICENSE.txt`
  in the publish output
- **Build**: compiled from unmodified upstream release source by
  `scripts/build-ffmpeg-minimal.sh` with an LGPL-only configuration
  (`--disable-gpl --disable-nonfree`); no GPL components are enabled and no
  source changes are made, so the corresponding source is the upstream
  release tarball of the pinned version
- **Communication boundary**: launched as a separate child process; this
  service does not link against the FFmpeg libraries

## LibreHardwareMonitor

- **Project**: LibreHardwareMonitorLib
- **Upstream**: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
- **License**: Mozilla Public License 2.0 (MPL-2.0)
- **Usage**: consumed unmodified as the official NuGet package; provides the
  Windows sensor backend (CPU / GPU / motherboard / fan / temperature)

## PawnIO

- **Project**: PawnIO (signed kernel I/O driver + module loader)
- **Upstream**: https://github.com/namazso/PawnIO
- **License**: GNU General Public License version 2 with a special linking
  exception (see the bundled `NOTICE`)
- **License text**: shipped alongside the bundled files (`COPYING` and
  `NOTICE` under the bundled `pawnio/` directory)
- **Usage**: `PawnIOLib.dll` is loaded in-process on Windows
  (LibreHardwareMonitorLib uses it for LPC/SMBus access, permitted by the
  linking exception) and the bundled OpenRGB child process uses it to probe
  SMBus for RGB DRAM

## Indirect display driver sample

- **Project**: IddSampleDriver (Windows-driver-samples, `video/IndirectDisplay`)
- **Upstream**: https://github.com/microsoft/Windows-driver-samples
- **License**: Microsoft Public License (MS-PL)
- **License text**: `Bundled/windows/nexus-vdd/LICENSE-MS-PL.txt`
- **Usage**: the Nexus Virtual Display driver (`Bundled/windows/nexus-vdd`,
  shipped as `vdd/NexusVirtualDisplay.dll`) is built on this sample. It runs in
  its own UMDF host process and the service talks to it through a shared memory
  section; the service does not link against it

## dfu-util

- **Project**: dfu-util
- **Upstream**: https://dfu-util.sourceforge.net
- **License**: GNU General Public License version 2 (see `COPYING` under the
  bundled `dfu-util/` directory)
- **Communication boundary**: spawned as a child process by the firmware
  flasher to write device firmware in DFU mode; not linked against

## Android platform-tools (adb)

- **Project**: Android SDK Platform-Tools
- **Upstream**: https://developer.android.com/tools/releases/platform-tools
- **License**: Apache License 2.0
- **License text**: shipped with the bundled binaries as `adb/NOTICE.txt` under
  each runtime identifier
- **Files**: `adb` (linux-x64, osx-arm64), `adb.exe` plus `AdbWinApi.dll`,
  `AdbWinUsbApi.dll` and `libwinpthread-1.dll` (win-x64) - redistributed
  unmodified from Google's `sdk-repo-*-platform-tools` archive, which is what
  the bundled `NOTICE.txt` covers
- **Communication boundary**: spawned as a child process to reach Android-based
  panels (Q-series, Y70 touch); not linked against

## Benchmark tools

Each ships as a separate executable, spawned as a child process by the
benchmark subsystem. None is linked against, and none is modified except
STREAM as noted.

| Tool | Upstream | License | License text |
| --- | --- | --- | --- |
| clpeak | https://github.com/krrishnarraj/clpeak | Apache-2.0 | `bench/clpeak/LICENSE` |
| DiskSpd | https://github.com/microsoft/diskspd | see the note below | `bench/diskspd/LICENSE` |
| primesieve | https://github.com/kimwalisch/primesieve | BSD-2-Clause | `bench/primesieve/LICENSE` |
| vkpeak | https://github.com/nihui/vkpeak | MIT | `bench/vkpeak/LICENSE` |
| STREAM | https://www.cs.virginia.edu/stream/ | McCalpin STREAM license (see below) | header of `bench/stream/stream.c` |

**DiskSpd** is dual-natured upstream: the source repository is MIT-licensed,
but the prebuilt release archive ships Microsoft Software License Terms, and
that EULA is what `bench/diskspd/LICENSE` reproduces. The bundled `diskspd.exe`
is byte-identical to the v2.2 release's `amd64/diskspd.exe`
(SHA-256 `8f3b2f09...71aee9`), so it is the EULA-covered build.

**STREAM** is John D. McCalpin's memory-bandwidth benchmark, Copyright
1991-2013 John D. McCalpin. Its license permits use, redistribution and
modification, and requires that published results either conform to the STREAM
Run Rules or be clearly labelled as being based on a variant. We build
`stream.exe` from the v5.10 source committed at `bench/stream/stream.c` with
three MSVC portability patches recorded in `bench/stream/BUILD.md`, so any
result Nexus reports is a *modified-source* STREAM result and is labelled as
such wherever it is surfaced. `bench/stream/vcomp140.dll` is Microsoft's Visual
C++ OpenMP runtime, redistributed under the Visual Studio redistributable
terms, so the benchmark runs on hosts without the VC++ runtime installed.

## Protocol implementations derived from GPL projects

Several first-party device drivers were written against, or transcribed
constants from, two GPL-licensed projects. This service is AGPL-3.0, which both
upstream licenses combine with, and the derivation is recorded here as well as
at each site in the source.

### OpenRGB (GPL-2.0-or-later)

Beyond the bundled headless binary documented above, the following carry
protocol constants or frame layouts taken from OpenRGB device controllers:

- `src/Lighting/Galahad2LightingModes.cs` - mode bytes and color-slot counts
  from `LianLiGAIITrinityController.cpp`
- `src/Peripherals/Hyte/Cnvs/CnvsHub.cs` - the 157-byte LED stream frame and
  the firmware-animation-off prologue, from HYTE's `HYTEMousematController` /
  `CNVSBaseController` contributions
- `src/Peripherals/CorsairLink/CorsairLinkModels.cs` - model table merged with
  `CorsairICueLinkProtocol.h`
- `src/Peripherals/Galahad2/`, `src/Peripherals/Nollie/`,
  `src/Peripherals/Strimer/`, `src/Peripherals/Hyte/Keeb/KeebLayout.cs` -
  device ids, zone sizes and layout tables cross-checked against the
  corresponding upstream controllers

### OpenLinkHub (GPL-3.0)

- **Upstream**: https://github.com/jurkovic-nikola/OpenLinkHub
- The Corsair iCUE LINK support (`src/Peripherals/CorsairLink/*`,
  `src/Lighting/CorsairLinkLightingFrameWriter.cs`,
  `src/Cooling/CorsairLinkCoolingProvider.cs`,
  `src/Panel/Streams/CorsairLinkLcdStreamTransport.cs`) implements the hub
  protocol described by `src/devices/lsh/lsh.go` and the model database
  `database/external/lsh.json`. The LCD write-buffer layout in
  `CorsairLinkLcd.cs` is byte-identical to that project's `transferToLcd`, and
  the pump/AIO minimum-duty floors mirror its manual-write behaviour.
