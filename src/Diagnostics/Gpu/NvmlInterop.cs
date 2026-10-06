using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Nexus.Service.Diagnostics.Gpu;

/// <summary>
/// Dynamic NVML interop for GPU health readout: name, driver version,
/// temperature, power draw, clocks event/throttle reasons, and per-policy
/// violation-time counters. Windows loads nvml.dll; Linux loads
/// libnvidia-ml.so.1 (same library <see cref="Nexus.Service.Cooling.Nvml"/>
/// uses for fan control, kept separate here since that wrapper is a distinct,
/// smaller symbol set for a different consumer). Neither is on the default
/// library search path for every install, so symbols are resolved through
/// <see cref="NativeLibrary"/> and invoked via function pointers rather than
/// static [LibraryImport] (AOT-safe, no marshalling stubs) - the
/// unmanaged[Stdcall] calling convention modifier only affects x86 name
/// mangling and is a no-op on x64, so the same function pointer declarations
/// are ABI-correct calling into either the Windows x64 or Linux x64 (System V)
/// NVML build. Every call returns the raw nvmlReturn_t int; nonzero means the
/// caller skips the value and keeps going - a partially-populated snapshot
/// beats none.
/// </summary>
internal static unsafe class NvmlInterop
{
    public const int Success = 0;               // NVML_SUCCESS
    public const int NotSupported = 3;           // NVML_ERROR_NOT_SUPPORTED

    private const string WindowsLibraryName = "nvml.dll";
    private const string WindowsFallbackPath = @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll";
    // Same candidates Nexus.Service.Cooling.Nvml (the separate Linux fan-control
    // wrapper) loads: the driver package always installs the versioned .so.1
    // symlink; the unversioned name is a fallback for a dev-only libnvidia-ml
    // without the packaged symlink.
    private const string LinuxLibraryName = "libnvidia-ml.so.1";
    private const string LinuxFallbackLibraryName = "libnvidia-ml.so";

    private const uint TemperatureGpu = 0;       // NVML_TEMPERATURE_GPU
    private const int NameBufferSize = 96;       // NVML_DEVICE_NAME_V2_BUFFER_SIZE
    private const int DriverVersionBufferSize = 80; // NVML_SYSTEM_DRIVER_VERSION_BUFFER_SIZE

    // nvmlClocksEventReasons / nvmlClocksThrottleReasons bitmask values (same bits
    // under either symbol name; the "EventReasons" name replaced "ThrottleReasons"
    // in newer drivers, older drivers only export the latter).
    public const ulong ReasonIdle = 0x1;
    public const ulong ReasonApplicationsClocksSetting = 0x2;
    public const ulong ReasonSwPowerCap = 0x4;
    public const ulong ReasonHwSlowdown = 0x8;
    public const ulong ReasonSyncBoost = 0x10;
    public const ulong ReasonSwThermalSlowdown = 0x20;
    public const ulong ReasonHwThermalSlowdown = 0x40;
    public const ulong ReasonHwPowerBrakeSlowdown = 0x80;
    public const ulong ReasonDisplayClockSetting = 0x100;

    // nvmlPerfPolicyType_t values used for cumulative violation-time counters.
    public const int PolicyPower = 0;
    public const int PolicyThermal = 1;
    public const int PolicyBoardLimit = 3;

    private static readonly object Gate = new();
    private static bool _attempted;
    private static bool _loaded;
    private static IntPtr _handle;

    private static IntPtr _pInit;
    private static IntPtr _pShutdown;
    private static IntPtr _pDeviceGetCount;
    private static IntPtr _pDeviceGetHandleByIndex;
    private static IntPtr _pDeviceGetName;
    private static IntPtr _pSystemGetDriverVersion;
    private static IntPtr _pDeviceGetTemperature;
    private static IntPtr _pDeviceGetPowerUsage;
    private static IntPtr _pDeviceGetEnforcedPowerLimit;
    private static IntPtr _pDeviceGetClocksReasons;
    private static IntPtr _pDeviceGetViolationStatus;
    private static IntPtr _pDeviceGetTemperatureThreshold;

    /// <summary>nvmlViolationTime_t: cumulative microseconds since driver load.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlViolationTime
    {
        public ulong ReferenceTimeUs;
        public ulong ViolationTimeUs;
    }

    /// <summary>Loads the platform NVML library and resolves every symbol once.
    /// Returns false (and stays false) when the driver isn't present or lacks
    /// the core enumeration entry points; never throws.</summary>
    public static bool TryLoad()
    {
        lock (Gate)
        {
            // _loaded is the full load+export predicate, not just handle-nonzero -
            // a re-entrant call must return the same value the first call computed,
            // or a handle-loaded-but-export-missing state would let a later caller
            // invoke through a null function pointer (an uncatchable native crash).
            if (_attempted) return _loaded;
            _attempted = true;
            try
            {
                if (!TryLoadPlatformLibrary())
                {
                    _loaded = false;
                    return false;
                }

                _pInit = Export("nvmlInit_v2");
                _pShutdown = Export("nvmlShutdown");
                _pDeviceGetCount = Export("nvmlDeviceGetCount_v2");
                _pDeviceGetHandleByIndex = Export("nvmlDeviceGetHandleByIndex_v2");
                _pDeviceGetName = Export("nvmlDeviceGetName");
                _pSystemGetDriverVersion = Export("nvmlSystemGetDriverVersion");
                _pDeviceGetTemperature = Export("nvmlDeviceGetTemperature");
                _pDeviceGetPowerUsage = Export("nvmlDeviceGetPowerUsage");
                _pDeviceGetEnforcedPowerLimit = Export("nvmlDeviceGetEnforcedPowerLimit");
                _pDeviceGetViolationStatus = Export("nvmlDeviceGetViolationStatus");
                _pDeviceGetTemperatureThreshold = Export("nvmlDeviceGetTemperatureThreshold");

                // Newer drivers renamed ThrottleReasons -> EventReasons (same
                // signature/bitmask); fall back to the old symbol for older drivers.
                _pDeviceGetClocksReasons = Export("nvmlDeviceGetCurrentClocksEventReasons");
                if (_pDeviceGetClocksReasons == IntPtr.Zero)
                {
                    _pDeviceGetClocksReasons = Export("nvmlDeviceGetCurrentClocksThrottleReasons");
                }

                _loaded = _pInit != IntPtr.Zero && _pDeviceGetCount != IntPtr.Zero && _pDeviceGetHandleByIndex != IntPtr.Zero;
                return _loaded;
            }
            catch
            {
                _handle = IntPtr.Zero;
                _loaded = false;
                return false;
            }
        }
    }

    private static bool TryLoadPlatformLibrary()
    {
        if (OperatingSystem.IsWindows())
        {
            return NativeLibrary.TryLoad(WindowsLibraryName, out _handle) ||
                   NativeLibrary.TryLoad(WindowsFallbackPath, out _handle);
        }
        if (OperatingSystem.IsLinux())
        {
            return NativeLibrary.TryLoad(LinuxLibraryName, out _handle) ||
                   NativeLibrary.TryLoad(LinuxFallbackLibraryName, out _handle);
        }
        return false;
    }

    private static IntPtr Export(string name) =>
        NativeLibrary.TryGetExport(_handle, name, out var addr) ? addr : IntPtr.Zero;

    public static int Init() => ((delegate* unmanaged[Stdcall]<int>)_pInit)();

    public static void Shutdown()
    {
        if (_pShutdown != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<int>)_pShutdown)();
        }
    }

    public static int DeviceGetCount(out uint count) =>
        ((delegate* unmanaged[Stdcall]<out uint, int>)_pDeviceGetCount)(out count);

    public static int DeviceGetHandleByIndex(uint index, out IntPtr device) =>
        ((delegate* unmanaged[Stdcall]<uint, out IntPtr, int>)_pDeviceGetHandleByIndex)(index, out device);

    public static string? GetDriverVersion()
    {
        if (_pSystemGetDriverVersion == IntPtr.Zero) return null;
        Span<byte> buf = stackalloc byte[DriverVersionBufferSize];
        fixed (byte* p = buf)
        {
            var rc = ((delegate* unmanaged[Stdcall]<byte*, uint, int>)_pSystemGetDriverVersion)(p, DriverVersionBufferSize);
            if (rc != Success) return null;
        }
        return TrimAtNull(buf);
    }

    public static string GetDeviceName(IntPtr device)
    {
        if (_pDeviceGetName == IntPtr.Zero) return "NVIDIA GPU";
        Span<byte> buf = stackalloc byte[NameBufferSize];
        fixed (byte* p = buf)
        {
            var rc = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, int>)_pDeviceGetName)(device, p, NameBufferSize);
            if (rc != Success) return "NVIDIA GPU";
        }
        var name = TrimAtNull(buf);
        return name.Length == 0 ? "NVIDIA GPU" : name;
    }

    public static int GetTemperature(IntPtr device, out uint tempC)
    {
        tempC = 0;
        if (_pDeviceGetTemperature == IntPtr.Zero) return NotSupported;
        return ((delegate* unmanaged[Stdcall]<IntPtr, uint, out uint, int>)_pDeviceGetTemperature)(device, TemperatureGpu, out tempC);
    }

    public static int GetTemperatureThreshold(IntPtr device, uint thresholdType, out uint tempC)
    {
        tempC = 0;
        if (_pDeviceGetTemperatureThreshold == IntPtr.Zero) return NotSupported;
        return ((delegate* unmanaged[Stdcall]<IntPtr, uint, out uint, int>)_pDeviceGetTemperatureThreshold)(device, thresholdType, out tempC);
    }

    public static int GetPowerUsageMilliwatts(IntPtr device, out uint milliwatts)
    {
        milliwatts = 0;
        if (_pDeviceGetPowerUsage == IntPtr.Zero) return NotSupported;
        return ((delegate* unmanaged[Stdcall]<IntPtr, out uint, int>)_pDeviceGetPowerUsage)(device, out milliwatts);
    }

    public static int GetEnforcedPowerLimitMilliwatts(IntPtr device, out uint milliwatts)
    {
        milliwatts = 0;
        if (_pDeviceGetEnforcedPowerLimit == IntPtr.Zero) return NotSupported;
        return ((delegate* unmanaged[Stdcall]<IntPtr, out uint, int>)_pDeviceGetEnforcedPowerLimit)(device, out milliwatts);
    }

    public static int GetClocksReasons(IntPtr device, out ulong reasons)
    {
        reasons = 0;
        if (_pDeviceGetClocksReasons == IntPtr.Zero) return NotSupported;
        return ((delegate* unmanaged[Stdcall]<IntPtr, out ulong, int>)_pDeviceGetClocksReasons)(device, out reasons);
    }

    public static int GetViolationStatus(IntPtr device, int policyType, out NvmlViolationTime violation)
    {
        violation = default;
        if (_pDeviceGetViolationStatus == IntPtr.Zero) return NotSupported;
        return ((delegate* unmanaged[Stdcall]<IntPtr, int, out NvmlViolationTime, int>)_pDeviceGetViolationStatus)(device, policyType, out violation);
    }

    private const uint ThresholdSlowdown = 1; // NVML_TEMPERATURE_THRESHOLD_SLOWDOWN

    private static readonly object SlowdownInitGate = new();
    private static bool _slowdownInitOk;
    private static long _slowdownInitRetryAtMs;

    /// <summary>
    /// One attempt at a GPU's slowdown temperature threshold. NVML is initialised once on
    /// success (refcounted by the driver, never shut down by this daemon); a failed init is
    /// retried only after the caller's backoff has passed.
    /// </summary>
    public static Nexus.Service.Cooling.GpuSlowdownThreshold.Read ReadSlowdownThreshold(int gpu)
    {
        var notSupported = Nexus.Service.Cooling.GpuSlowdownThreshold.Read.NotSupported;
        var transient = Nexus.Service.Cooling.GpuSlowdownThreshold.Read.Transient;
        try
        {
            if (!TryLoad())
                return notSupported;
            lock (SlowdownInitGate)
            {
                if (!_slowdownInitOk)
                {
                    var now = Environment.TickCount64;
                    if (now < _slowdownInitRetryAtMs)
                        return transient;
                    _slowdownInitOk = Init() == Success;
                    _slowdownInitRetryAtMs = now + Nexus.Service.Cooling.GpuSlowdownThreshold.RetryBackoffMs;
                    if (!_slowdownInitOk)
                        return transient;
                }
            }
            if (DeviceGetHandleByIndex((uint)gpu, out var dev) != Success)
                return transient;
            var rc = GetTemperatureThreshold(dev, ThresholdSlowdown, out var t);
            if (rc == Success)
                return Nexus.Service.Cooling.GpuSlowdownThreshold.Read.Ok((int)t);
            return rc == NotSupported ? notSupported : transient;
        }
        catch
        {
            return transient;
        }
    }

    private static string TrimAtNull(Span<byte> buf)
    {
        var end = buf.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? buf : buf[..end]).Trim();
    }

    // Known reason bits Idle/ApplicationsClocksSetting never map to an active
    // throttle key. Anything set outside the recognised set (SyncBoost,
    // DisplayClockSetting, or a future bit) folds into "other".
    private const ulong KnownReasonBits =
        ReasonIdle | ReasonApplicationsClocksSetting | ReasonSwPowerCap | ReasonHwSlowdown |
        ReasonSwThermalSlowdown | ReasonHwThermalSlowdown | ReasonHwPowerBrakeSlowdown;

    /// <summary>Maps a clocks-reasons bitmask to the contract's throttle active
    /// keys: swPower, hwSlowdown, hwThermal, hwPowerBrake, swThermal, other.</summary>
    public static string[] MapReasonsToActiveKeys(ulong reasons)
    {
        if (reasons == 0) return Array.Empty<string>();
        var active = new List<string>(4);
        if ((reasons & ReasonSwPowerCap) != 0) active.Add("swPower");
        if ((reasons & ReasonHwSlowdown) != 0) active.Add("hwSlowdown");
        if ((reasons & ReasonSwThermalSlowdown) != 0) active.Add("swThermal");
        if ((reasons & ReasonHwThermalSlowdown) != 0) active.Add("hwThermal");
        if ((reasons & ReasonHwPowerBrakeSlowdown) != 0) active.Add("hwPowerBrake");
        if ((reasons & ~KnownReasonBits) != 0) active.Add("other");
        return active.ToArray();
    }
}
