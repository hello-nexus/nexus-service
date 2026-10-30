using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Pins the calling thread to the performance cores of a hybrid CPU. A paced encode thread
/// sleeps between frames and the scheduler otherwise lands it on efficiency cores, where the
/// same JPEG encode takes several times longer. No-op on a uniform CPU or when sysfs reports
/// no hybrid layout.
/// </summary>
internal static unsafe partial class LinuxPerformanceCoreHint
{
    private const int MaskBytes = 128;
    private const string CpuRoot = "/sys/devices/system/cpu";
    private const string IntelCoreList = "/sys/devices/cpu_core/cpus";
    private const string IntelAtomList = "/sys/devices/cpu_atom/cpus";

    private static int[]? _cpus;
    private static bool _resolved;
    private static readonly object Gate = new();

    [SupportedOSPlatform("linux")]
    public static void ApplyToCurrentThread()
    {
        int[] cpus;
        lock (Gate)
        {
            if (!_resolved)
            {
                _resolved = true;
                _cpus = SelectPerformanceCpus(ReadSysfs, EnumerateCpuIds());
            }
            cpus = _cpus ?? Array.Empty<int>();
        }
        if (cpus.Length == 0)
        {
            return;
        }
        try
        {
            var mask = stackalloc byte[MaskBytes];
            new Span<byte>(mask, MaskBytes).Clear();
            foreach (var cpu in cpus)
            {
                if (cpu >= 0 && cpu < MaskBytes * 8)
                {
                    mask[cpu / 8] |= (byte)(1 << (cpu % 8));
                }
            }
            // pid 0 addresses the calling thread, not the process.
            sched_setaffinity(0, (nuint)MaskBytes, mask);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// Intel hybrid exposes the P-core list directly; ARM big.LITTLE and newer x86 kernels expose
    /// per-CPU cpu_capacity. Returns empty when every CPU is equal or nothing is readable.
    /// </summary>
    internal static int[] SelectPerformanceCpus(Func<string, string?> read, IReadOnlyList<int> cpuIds)
    {
        var intel = read(IntelCoreList);
        if (intel is not null && read(IntelAtomList) is not null)
        {
            var list = ParseCpuList(intel);
            if (list.Length > 0)
            {
                return list;
            }
        }
        var capacity = new Dictionary<int, long>();
        foreach (var id in cpuIds)
        {
            if (long.TryParse(read($"{CpuRoot}/cpu{id}/cpu_capacity"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
            {
                capacity[id] = c;
            }
        }
        if (capacity.Count == 0)
        {
            return Array.Empty<int>();
        }
        var max = long.MinValue;
        var min = long.MaxValue;
        foreach (var c in capacity.Values)
        {
            max = Math.Max(max, c);
            min = Math.Min(min, c);
        }
        if (max == min)
        {
            return Array.Empty<int>();
        }
        var best = new List<int>();
        foreach (var (id, c) in capacity)
        {
            if (c == max)
            {
                best.Add(id);
            }
        }
        best.Sort();
        return best.ToArray();
    }

    /// <summary>Parses a kernel cpulist such as "0-7,16".</summary>
    internal static int[] ParseCpuList(string text)
    {
        var result = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var one) && one >= 0 && one < MaskBytes * 8)
                {
                    result.Add(one);
                }
                continue;
            }
            if (int.TryParse(part.AsSpan(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lo)
                && int.TryParse(part.AsSpan(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hi)
                && lo >= 0 && hi >= lo && hi < MaskBytes * 8)
            {
                for (var i = lo; i <= hi; i++)
                {
                    result.Add(i);
                }
            }
        }
        return result.ToArray();
    }

    private static string? ReadSysfs(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<int> EnumerateCpuIds()
    {
        var ids = new List<int>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(CpuRoot, "cpu*"))
            {
                var name = Path.GetFileName(dir);
                if (int.TryParse(name.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    ids.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return ids;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int sched_setaffinity(int pid, nuint cpusetsize, byte* mask);
}
