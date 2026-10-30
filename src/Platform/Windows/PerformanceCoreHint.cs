using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Nexus.Service.Platform.Windows;


public enum CoreHintMode
{
    Off,
    /// <summary>Opts out of EcoQoS, prefers one performance core as ideal processor, raises priority; the scheduler may still use any core.</summary>
    Soft,
    /// <summary>Restricts the thread to the performance cores' CPU sets.</summary>
    Hard,
}

/// <summary>The platform-free decisions behind <see cref="PerformanceCoreHint"/>.</summary>
public static class CoreHintPolicy
{
    public readonly record struct CpuSet(uint Id, byte Group, byte LogicalProcessorIndex, byte EfficiencyClass);

    public static CoreHintMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" => CoreHintMode.Off,
        "hard" => CoreHintMode.Hard,
        _ => CoreHintMode.Soft,
    };

    /// <summary>Reads SYSTEM_CPU_SET_INFORMATION records: Size u32, Type u32 (0 = cpu set), then Id u32 at 8, Group u16 at 12, LogicalProcessorIndex u8 at 14, EfficiencyClass u8 at 18.</summary>
    public static List<CpuSet> ParseCpuSets(ReadOnlySpan<byte> buffer)
    {
        var all = new List<CpuSet>();
        var offset = 0;
        while (offset + 20 <= buffer.Length)
        {
            var size = BitConverter.ToUInt32(buffer.Slice(offset, 4));
            if (size < 20)
            {
                break;
            }
            if (BitConverter.ToUInt32(buffer.Slice(offset + 4, 4)) == 0)
            {
                all.Add(new CpuSet(
                    BitConverter.ToUInt32(buffer.Slice(offset + 8, 4)),
                    (byte)BitConverter.ToUInt16(buffer.Slice(offset + 12, 2)),
                    buffer[offset + 14],
                    buffer[offset + 18]));
            }
            offset += (int)size;
        }
        return all;
    }

    /// <summary>The CPU sets of the highest efficiency class, or empty when every set shares one class.</summary>
    public static List<CpuSet> SelectBestClass(IReadOnlyList<CpuSet> all)
    {
        byte min = byte.MaxValue, max = 0;
        foreach (var set in all)
        {
            min = Math.Min(min, set.EfficiencyClass);
            max = Math.Max(max, set.EfficiencyClass);
        }
        var best = new List<CpuSet>();
        if (all.Count == 0 || min == max)
        {
            return best;
        }
        foreach (var set in all)
        {
            if (set.EfficiencyClass == max)
            {
                best.Add(set);
            }
        }
        return best;
    }
}

/// <summary>
/// Keeps a paced encode thread off the efficiency cores of a hybrid CPU: it sleeps between frames
/// and the scheduler otherwise lands it there, where the same JPEG encode takes several times
/// longer. No-op on a CPU with a single efficiency class. Soft is the default; NEXUS_STREAM_CORE_HINT
/// (off, soft, hard) is a support toggle for tracing stutter, not a user setting.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class PerformanceCoreHint
{
    private const int ThreadPowerThrottling = 3;
    private const uint ThrottlingExecutionSpeed = 1;
    private static readonly object Gate = new();
    private static List<CoreHintPolicy.CpuSet>? _best;
    private static bool _resolved;

    public static CoreHintMode Mode { get; } = CoreHintPolicy.ParseMode(Environment.GetEnvironmentVariable("NEXUS_STREAM_CORE_HINT"));

    public static void ApplyToCurrentThread()
    {
        var mode = Mode;
        if (mode == CoreHintMode.Off)
        {
            return;
        }
        List<CoreHintPolicy.CpuSet> best;
        lock (Gate)
        {
            if (!_resolved)
            {
                _resolved = true;
                _best = Resolve();
            }
            best = _best ?? new List<CoreHintPolicy.CpuSet>();
        }
        // One efficiency class means no hybrid split, so there is nothing to steer away from.
        if (best.Count == 0)
        {
            return;
        }
        if (mode == CoreHintMode.Hard)
        {
            var ids = new uint[best.Count];
            for (var i = 0; i < ids.Length; i++)
            {
                ids[i] = best[i].Id;
            }
            fixed (uint* p = ids)
            {
                SetThreadSelectedCpuSets(GetCurrentThread(), p, (uint)ids.Length);
            }
            return;
        }
        var throttling = stackalloc uint[3] { 1, ThrottlingExecutionSpeed, 0 };
        SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling, throttling, 12);
        var ideal = new ProcessorNumber { Group = best[0].Group, Number = best[0].LogicalProcessorIndex };
        SetThreadIdealProcessorEx(GetCurrentThread(), &ideal, null);
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
    }

    private static List<CoreHintPolicy.CpuSet>? Resolve()
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
            return CoreHintPolicy.SelectBestClass(CoreHintPolicy.ParseCpuSets(buffer.AsSpan(0, (int)length)));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorNumber
    {
        public ushort Group;
        public byte Number;
        public byte Reserved;
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadInformation(nint thread, int informationClass, uint* information, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadIdealProcessorEx(nint thread, ProcessorNumber* ideal, ProcessorNumber* previous);
}
