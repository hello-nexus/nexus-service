# Firmware Update Strategy

## Overview

Device firmware updates are managed entirely by qos-service (unlike the original
Nexus client which delegated to external tools). This keeps the update flow self-contained
and controllable via the REST API.

## Storage

Firmware binaries are stored in a local cache directory.

- **Windows**: `%ProgramData%\Qos\firmware\` (machine-scope so the
  LocalSystem service can write it and every user on the box sees the
  same versions).
- **Linux**: `$XDG_CACHE_HOME/Qos/firmware/` (defaults to
  `~/.cache/Qos/firmware/`).

```
firmware/
├── cnvs/
│   ├── 1.2.3.bin
│   └── manifest.json
├── np50/
│   ├── 2.0.5.1.hex
│   └── manifest.json
└── ...
```

Each device type has its own subdirectory. `manifest.json` tracks available versions:

```json
{
  "latest": "1.2.3",
  "files": {
    "1.2.3": { "sha256": "abc...", "size": 65536, "url": "https://..." }
  }
}
```

## Download Flow

1. Service checks a remote manifest URL for each device type (configurable per handler)
2. If a newer version exists, downloads the binary to the local cache
3. Validates SHA-256 checksum before marking as available
4. The binary is never served over HTTP — the service flashes it directly via USB/HID

## Update Flow (API)

```
GET  /devices/fw/{type}/version     → current firmware version from device
POST /devices/fw/{type}/check       → check remote manifest for updates
POST /devices/fw/{type}/update      → start firmware flash (async)
GET  /devices/fw/{type}/progress    → poll flash progress (0-100%)
```

## Flash Protocol

Each device type defines its own flash protocol in its handler. Common patterns:

- **STM32 DFU**: CNVS uses STM32 bootloader protocol via USB HID
- **Custom serial**: Some devices use proprietary serial commands
- **USB mass storage**: Some displays accept firmware via USB mass storage mode

The `IDeviceHandler` interface will be extended with:

```csharp
bool CanUpdateFirmware { get; }
Task<bool> FlashFirmwareAsync(string firmwarePath, IProgress<int> progress);
```

## TODO

- [x] Implement FirmwareStore (local cache manager) — `FirmwareStore.cs`
- [ ] Define remote manifest URL per device type (NP50 pending: see `plans/np50-support.md`)
- [ ] Implement STM32 DFU flash protocol for CNVS
- [ ] Add progress reporting via WebSocket
- [ ] Add firmware rollback support
