#if WINDOWS
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Nexus.Service.Lifecycle;

/// <summary>Per-thread CPU of this process with each thread's start module; shared by the ANGLE probe and [gpu-check].</summary>
internal static partial class AngleDiagThreads
{
    internal readonly record struct Row(uint Tid, double MsPerSec, string Module, string Name);

    private static readonly Dictionary<uint, (long Created, string Name)> ModuleCache = new();

    // prev holds the last cumulative CPU per thread.
    internal static List<Row> Sample(Dictionary<uint, long> prev, double secs)
    {
        var rows = new List<Row>();
        var snap = CreateToolhelp32Snapshot(SnapThread, 0);
        if (snap == -1 || snap == 0)
        {
            return rows;
        }
        try
        {
            var pid = (uint)Environment.ProcessId;
            var e = new ThreadEntry32 { Size = (uint)Marshal.SizeOf<ThreadEntry32>() };
            for (var ok = Thread32First(snap, ref e); ok != 0; ok = Thread32Next(snap, ref e))
            {
                if (e.OwnerProcessId != pid)
                {
                    continue;
                }
                var h = OpenThread(ThreadQueryLimitedInformation, 0, e.ThreadId);
                if (h == 0)
                {
                    continue;
                }
                try
                {
                    if (GetThreadTimes(h, out var created, out _, out var kernel, out var user) == 0)
                    {
                        continue;
                    }
                    var cpu = kernel + user;
                    prev.TryGetValue(e.ThreadId, out var before);
                    prev[e.ThreadId] = cpu;
                    if (cpu < before)
                    {
                        continue;
                    }
                    rows.Add(new Row(e.ThreadId, (cpu - before) / 10_000.0 / secs,
                        StartModule(e.ThreadId, created), ThreadName(h)));
                }
                finally
                {
                    CloseHandle(h);
                }
            }
        }
        finally
        {
            CloseHandle(snap);
        }
        rows.Sort((a, b) => b.MsPerSec.CompareTo(a.MsPerSec));
        return rows;
    }

    /// <summary>True for an Intel graphics driver module, ig*.dll.</summary>
    internal static bool IsIntelDriver(string module) =>
        module.StartsWith("ig", StringComparison.OrdinalIgnoreCase)
        && module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    internal static string Top(List<Row> rows, int n)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        for (var i = 0; i < Math.Min(n, rows.Count); i++)
        {
            sb.Append(' ').Append(rows[i].Tid.ToString(ci)).Append(":'").Append(rows[i].Name).Append("'@")
                .Append(rows[i].Module).Append('=').Append(rows[i].MsPerSec.ToString("0.0", ci));
        }
        return sb.ToString();
    }

    /// <summary>Loaded modules named ig*.dll, sorted; empty when enumeration fails.</summary>
    internal static List<string> LoadedIntelModules()
    {
        var list = new List<string>();
        try
        {
            using var p = Process.GetCurrentProcess();
            foreach (ProcessModule m in p.Modules)
            {
                var name = m.ModuleName ?? "";
                if (IsIntelDriver(name))
                {
                    list.Add(name);
                }
            }
        }
        catch (Exception)
        {
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>True when a loaded module matches ig*icd*.dll (Intel's OpenGL ICD).</summary>
    internal static bool IcdLoaded(List<string> intelModules) =>
        intelModules.Exists(m => m.Contains("icd", StringComparison.OrdinalIgnoreCase));

    private static unsafe string StartModule(uint tid, long created)
    {
        lock (ModuleCache)
        {
            if (ModuleCache.TryGetValue(tid, out var cached) && cached.Created == created)
            {
                return cached.Name;
            }
        }
        var name = "?";
        var h = OpenThread(ThreadQueryLimitedInformation | ThreadQueryInformation, 0, tid);
        if (h != 0)
        {
            try
            {
                if (NtQueryInformationThread(h, ThreadQuerySetWin32StartAddress, out var start, nint.Size, out _) == 0
                    && start != 0
                    && GetModuleHandleExW(ModuleFromAddress | ModuleUnchangedRefcount, start, out var module) != 0)
                {
                    var buf = stackalloc char[260];
                    var len = GetModuleFileNameW(module, buf, 260);
                    if (len > 0)
                    {
                        var full = new string(buf, 0, (int)len);
                        name = full.Substring(full.LastIndexOf('\\') + 1);
                    }
                }
            }
            catch (Exception)
            {
                name = "?";
            }
            finally
            {
                CloseHandle(h);
            }
        }
        lock (ModuleCache)
        {
            ModuleCache[tid] = (created, name);
        }
        return name;
    }

    private static string ThreadName(nint h)
    {
        try
        {
            if (GetThreadDescription(h, out var p) < 0 || p == 0)
            {
                return "";
            }
            var s = Marshal.PtrToStringUni(p) ?? "";
            LocalFree(p);
            return s;
        }
        catch (Exception)
        {
            return "";
        }
    }

    private const uint ThreadQueryLimitedInformation = 0x800;
    private const uint ThreadQueryInformation = 0x40;
    private const uint SnapThread = 0x4;
    private const int ThreadQuerySetWin32StartAddress = 9;
    private const uint ModuleFromAddress = 0x4;
    private const uint ModuleUnchangedRefcount = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ThreadId;
        public uint OwnerProcessId;
        public int BasePriority;
        public int DeltaPriority;
        public uint Flags;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenThread(uint access, int inherit, uint threadId);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial int GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial int Thread32First(nint snapshot, ref ThreadEntry32 entry);

    [LibraryImport("kernel32.dll")]
    private static partial int Thread32Next(nint snapshot, ref ThreadEntry32 entry);

    [LibraryImport("kernel32.dll")]
    private static partial int GetThreadDescription(nint thread, out nint description);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint mem);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationThread(nint thread, int infoClass, out nint info, int length, out uint returned);

    [LibraryImport("kernel32.dll")]
    private static partial int GetModuleHandleExW(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint GetModuleFileNameW(nint module, char* buffer, uint size);
}
#endif
