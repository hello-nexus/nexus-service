#if WINDOWS
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Nexus.Service.Lifecycle.Native;
using Nexus.Service.Platform;

namespace Nexus.Service.Platform.Displays;

/// <summary>
/// Forces a digitizer's HID devnode to re-enumerate via SetupAPI
/// DIF_PROPERTYCHANGE/DICS_PROPCHANGE - the only mechanism that applies a
/// Digimon registry write live; WM_SETTINGCHANGE does not (bench-proven,
/// plans/touch-mapping-auto-repair.md section 4a). Same SetupDi family as
/// PawnIoInstaller's device creation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTouchDigitizerDevnodeRestarter : ITouchDigitizerDevnodeRestarter
{
    public bool Restart(string digitizerInterfacePath)
    {
        var deviceInfoSet = SetupApi.SetupDiCreateDeviceInfoListAny(IntPtr.Zero, IntPtr.Zero);
        if (deviceInfoSet == SetupApi.INVALID_HANDLE_VALUE)
        {
            ServiceLog.Error($"[touch-map] SetupDiCreateDeviceInfoList failed: {Marshal.GetLastWin32Error()}");
            return false;
        }

        try
        {
            var ifaceData = new SetupApi.SP_DEVICE_INTERFACE_DATA
            {
                cbSize = Marshal.SizeOf<SetupApi.SP_DEVICE_INTERFACE_DATA>(),
            };
            if (!SetupApi.SetupDiOpenDeviceInterface(deviceInfoSet, digitizerInterfacePath, 0, ref ifaceData))
            {
                ServiceLog.Error($"[touch-map] SetupDiOpenDeviceInterface failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            var devInfoData = new SetupApi.SP_DEVINFO_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SetupApi.SP_DEVINFO_DATA>(),
            };
            if (!SetupApi.SetupDiGetDeviceInterfaceDetailForInfoData(
                    deviceInfoSet, ref ifaceData, IntPtr.Zero, 0, IntPtr.Zero, ref devInfoData))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != SetupApi.ERROR_INSUFFICIENT_BUFFER)
                {
                    ServiceLog.Error($"[touch-map] SetupDiGetDeviceInterfaceDetail failed: {error}");
                    return false;
                }
            }

            var propChange = new SetupApi.SP_PROPCHANGE_PARAMS
            {
                ClassInstallHeader = new SetupApi.SP_CLASSINSTALL_HEADER
                {
                    cbSize = (uint)Marshal.SizeOf<SetupApi.SP_CLASSINSTALL_HEADER>(),
                    InstallFunction = SetupApi.DIF_PROPERTYCHANGE,
                },
                StateChange = SetupApi.DICS_PROPCHANGE,
                Scope = SetupApi.DICS_FLAG_GLOBAL,
                HwProfile = 0,
            };
            if (!SetupApi.SetupDiSetClassInstallParams(
                    deviceInfoSet, ref devInfoData, ref propChange,
                    (uint)Marshal.SizeOf<SetupApi.SP_PROPCHANGE_PARAMS>()))
            {
                ServiceLog.Error($"[touch-map] SetupDiSetClassInstallParams failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            if (!SetupApi.SetupDiCallClassInstaller(SetupApi.DIF_PROPERTYCHANGE, deviceInfoSet, ref devInfoData))
            {
                ServiceLog.Error($"[touch-map] SetupDiCallClassInstaller(DIF_PROPERTYCHANGE) failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            return true;
        }
        finally
        {
            SetupApi.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }
}
#endif
