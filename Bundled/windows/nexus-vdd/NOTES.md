# Nexus Virtual Display

An IddCx (UMDF 2) indirect display driver that gives Windows one extra monitor and publishes
each desktop frame to the Nexus service. It backs the "secondary monitor" mode of streamed
panels that can take a desktop (the ZMatrices LCD).

Derived from Microsoft's IddSampleDriver (Windows-driver-samples, video/IndirectDisplay),
used under the Microsoft Public License (MS-PL).

## Pieces

| File | Role |
| --- | --- |
| `Driver.cpp` / `Driver.h` | The driver. One monitor with an EDID ("NXS0001", native timing = requested size, physical size at 96 dpi). Frames go to `Global\NexusVirtualDisplayFrame` + `Global\NexusVirtualDisplayFrameReady`. |
| `Host.cpp` | `NexusVirtualDisplayHost.exe`: creates the software device (`SWD\NexusVirtualDisplay\NexusVirtualDisplay`) and removes it when its parent exits or the stop event is set. Native because `SwDeviceCreate` returns `ERROR_MOD_NOT_FOUND` when called from .NET (both .NET Framework and .NET 10); the same call from C++ succeeds. |
| `NexusVirtualDisplay.inf` | Hardware id `NexusVirtualDisplay`, `IddCx0102`, `IndirectKmd` upper filter. |
| `build.ps1` | Builds with the VS Build Tools and the WDK from the `Microsoft.Windows.WDK.x64` NuGet package (no WDK install), stamps the INF, runs Inf2Cat, signs when given a thumbprint. Output: `Bundled\win-x64\vdd`. |

The service writes the monitor size to `HKLM\SOFTWARE\Nexus\VirtualDisplay` (`Width`, `Height`)
before starting the host; the driver reads it when the device starts.

## Signing and install

The DLL, the host and the catalog carry the Nexus Authenticode signature (not a Microsoft
driver signature). `pnputil /add-driver` then installs without a prompt only on a PC where that
publisher certificate is in LocalMachine\TrustedPublisher; otherwise Windows refuses with "The
publisher of an Authenticode signed catalog has not yet been established as trusted".
TrustedPublisher trusts a certificate, not a publisher name, and Trusted Signing issues
short-lived certificates, so a build signed on another day is a different certificate.
