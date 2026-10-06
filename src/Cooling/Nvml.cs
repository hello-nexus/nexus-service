using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Nexus.Service.Cooling;

/// <summary>
/// Minimal NVML (libnvidia-ml) interop for per-fan GPU read + control. Wraps the
/// handful of calls <see cref="LinuxNvidiaFanProvider"/> needs. Every entry point
/// is guarded: a box without the NVIDIA driver throws <c>DllNotFoundException</c>
/// on first call (caught → <see cref="Available"/> false), and an older driver
/// missing a <c>_v2</c> symbol throws <c>EntryPointNotFoundException</c> (also
/// caught). Blittable P/Invoke only - AOT-safe.
/// </summary>
internal static unsafe partial class Nvml
{
    private const string Lib = "libnvidia-ml.so.1";
    private const int Success = 0;          // NVML_SUCCESS
    private const uint TemperatureGpu = 0;  // NVML_TEMPERATURE_GPU
    private const uint ThresholdSlowdown = 1; // NVML_TEMPERATURE_THRESHOLD_SLOWDOWN
    private const int NameBuf = 96;         // NVML_DEVICE_NAME_V2_BUFFER_SIZE

    private static readonly object Gate = new();
    private static bool? _available;

    public static bool Available
    {
        get
        {
            lock (Gate)
            {
                _available ??= TryInit();
                return _available.Value;
            }
        }
    }

    // Initialised once and never paired with nvmlShutdown: this is a long-lived
    // daemon, the driver refcounts, and the OS reclaims on exit - tearing NVML
    // down while a concurrent Read() is mid-call would be worse. Intentional.
    private static bool TryInit()
    {
        try { return nvmlInit_v2() == Success; }
        catch { return false; } // DllNotFound (no driver) / EntryPointNotFound (old driver)
    }

    public static List<GpuInfo> Read()
    {
        var list = new List<GpuInfo>();
        if (!Available)
            return list;
        try
        {
            if (nvmlDeviceGetCount_v2(out var count) != Success)
                return list;
            for (uint i = 0; i < count; i++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(i, out var dev) != Success)
                    continue;
                var fans = new List<GpuFan>();
                if (nvmlDeviceGetNumFans(dev, out var nfans) == Success)
                {
                    for (uint f = 0; f < nfans; f++)
                    {
                        if (nvmlDeviceGetFanSpeed_v2(dev, f, out var speed) == Success)
                            fans.Add(new GpuFan((int)f, (int)speed, FanRpm(dev, f)));
                    }
                }
                float? temp = nvmlDeviceGetTemperature(dev, TemperatureGpu, out var t) == Success ? t : null;
                list.Add(new GpuInfo((int)i, ReadName(dev), temp, fans));
            }
        }
        catch { /* driver removed mid-read - return what we have */ }
        return list;
    }

    /// <summary>Set a fan to a fixed duty %, or null to restore the driver's auto curve. Needs root.</summary>
    public static bool SetFan(int gpu, int fan, int? duty)
    {
        if (!Available)
            return false;
        try
        {
            if (nvmlDeviceGetHandleByIndex_v2((uint)gpu, out var dev) != Success)
                return false;
            var rc = duty is null
                ? nvmlDeviceSetDefaultFanSpeed_v2(dev, (uint)fan)
                : nvmlDeviceSetFanSpeed_v2(dev, (uint)fan, (uint)Math.Clamp(duty.Value, 0, 100));
            return rc == Success;
        }
        catch { return false; }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> SlowdownCache = new();

    /// <summary>
    /// The GPU's slowdown temperature threshold in C, or null when NVML or the symbol is
    /// unavailable. Windows goes through the nvml.dll loader the GPU health monitor already
    /// uses; the index is NVML's, which is assumed to match the vendor-neutral enumeration order.
    /// </summary>
    public static int? GetSlowdownThreshold(int gpu)
    {
        if (SlowdownCache.TryGetValue(gpu, out var cached))
            return cached;
        var read = OperatingSystem.IsWindows() ? ReadSlowdownWindows(gpu) : ReadSlowdownLinux(gpu);
        if (read is { } value)
            SlowdownCache[gpu] = value;
        return read;
    }

    private static int? ReadSlowdownLinux(int gpu)
    {
        if (!Available)
            return null;
        try
        {
            if (nvmlDeviceGetHandleByIndex_v2((uint)gpu, out var dev) != Success)
                return null;
            return nvmlDeviceGetTemperatureThreshold(dev, ThresholdSlowdown, out var t) == Success ? (int)t : null;
        }
        catch { return null; }
    }

    private static int? ReadSlowdownWindows(int gpu)
    {
        try
        {
            if (!Nexus.Service.Diagnostics.Gpu.NvmlInterop.TryLoad())
                return null;
            // Refcounted by the driver: a second init alongside the health monitor is harmless.
            if (Nexus.Service.Diagnostics.Gpu.NvmlInterop.Init() != Success)
                return null;
            if (Nexus.Service.Diagnostics.Gpu.NvmlInterop.DeviceGetHandleByIndex((uint)gpu, out var dev) != Success)
                return null;
            return Nexus.Service.Diagnostics.Gpu.NvmlInterop.GetTemperatureThreshold(dev, ThresholdSlowdown, out var t) == Success
                ? (int)t
                : null;
        }
        catch { return null; }
    }

    private static string ReadName(IntPtr dev)
    {
        Span<byte> buf = stackalloc byte[NameBuf];
        fixed (byte* p = buf)
        {
            if (nvmlDeviceGetName(dev, p, NameBuf) != Success)
                return "NVIDIA GPU";
        }
        var end = buf.IndexOf((byte)0);
        return Encoding.UTF8.GetString(buf[..(end < 0 ? NameBuf : end)]).Trim();
    }

    // Tach RPM via the versioned-struct API (driver R520+). Returns 0 when the
    // driver lacks the symbol (EntryPointNotFound) or the call fails - the duty
    // % is still reported. NVML_STRUCT_VERSION = sizeof | (version << 24).
    private static int FanRpm(IntPtr dev, uint fan)
    {
        try
        {
            var info = new FanSpeedInfo { Version = (uint)(sizeof(FanSpeedInfo) | (1 << 24)), Fan = fan };
            return nvmlDeviceGetFanSpeedRPM(dev, ref info) == Success ? (int)info.Speed : 0;
        }
        catch { return 0; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FanSpeedInfo
    {
        public uint Version;
        public uint Fan;   // in
        public uint Speed; // out (RPM)
    }

    [LibraryImport(Lib)] private static partial int nvmlDeviceGetFanSpeedRPM(IntPtr device, ref FanSpeedInfo fanSpeed);
    [LibraryImport(Lib)] private static partial int nvmlInit_v2();
    [LibraryImport(Lib)] private static partial int nvmlDeviceGetCount_v2(out uint count);
    [LibraryImport(Lib)] private static partial int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [LibraryImport(Lib)] private static partial int nvmlDeviceGetName(IntPtr device, byte* name, uint length);
    [LibraryImport(Lib)] private static partial int nvmlDeviceGetNumFans(IntPtr device, out uint numFans);
    [LibraryImport(Lib)] private static partial int nvmlDeviceGetFanSpeed_v2(IntPtr device, uint fan, out uint speed);
    [LibraryImport(Lib)] private static partial int nvmlDeviceGetTemperatureThreshold(IntPtr device, uint thresholdType, out uint temp);
    [LibraryImport(Lib)] private static partial int nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temp);
    [LibraryImport(Lib)] private static partial int nvmlDeviceSetFanSpeed_v2(IntPtr device, uint fan, uint speed);
    [LibraryImport(Lib)] private static partial int nvmlDeviceSetDefaultFanSpeed_v2(IntPtr device, uint fan);
}
