using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Platform.Windows;

/// <summary>
/// Prefers the highest efficiency class (performance cores) for the calling thread on a hybrid
/// CPU. A paced encode thread sleeps between frames and the scheduler otherwise lands it on
/// efficiency cores, where the same JPEG encode takes several times longer. No-op elsewhere.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class PerformanceCoreHint
{
    private static uint[]? _ids;
    private static bool _resolved;
    private static readonly object Gate = new();

    public static void ApplyToCurrentThread()
    {
        uint[] ids;
        lock (Gate)
        {
            if (!_resolved)
            {
                _resolved = true;
                _ids = Resolve();
            }
            ids = _ids ?? Array.Empty<uint>();
        }
        if (ids.Length == 0)
        {
            return;
        }
        fixed (uint* p = ids)
        {
            SetThreadSelectedCpuSets(GetCurrentThread(), p, (uint)ids.Length);
        }
    }

    // Record layout (SYSTEM_CPU_SET_INFORMATION): Size u32, Type u32, then Id u32 at 8 and EfficiencyClass u8 at 18.
    private static uint[]? Resolve()
    {
        try
        {
            uint length = 0;
            GetSystemCpuSetInformation(null, 0, &length, GetCurrentProcess(), 0);
            if (length == 0)
            {
                return null;
            }
            var buffer = new byte[length];
            fixed (byte* b = buffer)
            {
                if (!GetSystemCpuSetInformation(b, length, &length, GetCurrentProcess(), 0))
                {
                    return null;
                }
            }
            byte best = 0;
            for (var offset = 0; offset + 20 <= length; offset += (int)BitConverter.ToUInt32(buffer, offset))
            {
                if (BitConverter.ToUInt32(buffer, offset + 4) == 0)
                {
                    best = Math.Max(best, buffer[offset + 18]);
                }
                if (BitConverter.ToUInt32(buffer, offset) == 0)
                {
                    break;
                }
            }
            if (best == 0)
            {
                return null;
            }
            var ids = new System.Collections.Generic.List<uint>();
            for (var offset = 0; offset + 20 <= length; offset += (int)BitConverter.ToUInt32(buffer, offset))
            {
                if (BitConverter.ToUInt32(buffer, offset + 4) == 0 && buffer[offset + 18] == best)
                {
                    ids.Add(BitConverter.ToUInt32(buffer, offset + 8));
                }
                if (BitConverter.ToUInt32(buffer, offset) == 0)
                {
                    break;
                }
            }
            return ids.ToArray();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemCpuSetInformation(byte* information, uint bufferLength, uint* returnedLength, nint process, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadSelectedCpuSets(nint thread, uint* cpuSetIds, uint count);
}
