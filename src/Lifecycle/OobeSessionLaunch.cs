using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Starts a process in the console session while Windows is in OOBE and nobody
/// is signed in, where the schtasks route has no user to run as. Only reached
/// through <see cref="OobePanelPolicy.Decide"/> returning OobeNoUser.
///
/// Token: the console session's user token when one exists, otherwise this
/// service's own token with its privileges stripped and retargeted to the
/// console session. Every process started here is tracked and ended once OOBE
/// is over, so the real user's overlay and helper start clean.
/// </summary>
internal static class OobeSessionLaunch
{
    private static readonly object s_lock = new();
    private static readonly List<(int Pid, long StartTicks)> s_tracked = new();
    private static Timer? s_reaper;
#if WINDOWS
    private static bool s_logonHooked;
#endif

    private static string PidFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexus", "oobe-session-pids.txt");

    internal static bool ConsoleSessionExists()
    {
#if WINDOWS
        return OperatingSystem.IsWindows() && WTSGetActiveConsoleSessionId() != 0xFFFFFFFF;
#else
        return false;
#endif
    }

    /// <summary>Kills processes a previous service instance left in an OOBE session. A no-op unless the pid file exists.</summary>
    internal static void ReapLeftovers()
    {
        try
        {
            if (!File.Exists(PidFilePath)) return;
            foreach (var line in File.ReadAllLines(PidFilePath))
            {
                var parts = line.Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out var pid) && long.TryParse(parts[1], out var ticks))
                {
                    Kill(pid, ticks);
                }
            }
            File.Delete(PidFilePath);
        }
        catch { /* best-effort */ }
    }

    private static void Kill(int pid, long startTicks)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            // The start time rejects a recycled pid.
            if (p.StartTime.ToUniversalTime().Ticks == startTicks && !p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch { /* already gone */ }
    }

    private static void Track(Process p, string tag)
    {
        long ticks;
        try { ticks = p.StartTime.ToUniversalTime().Ticks; }
        catch { return; }
        lock (s_lock)
        {
            s_tracked.Add((p.Id, ticks));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PidFilePath)!);
                File.WriteAllLines(PidFilePath, s_tracked.ConvertAll(t => $"{t.Pid},{t.StartTicks}"));
            }
            catch { /* best-effort */ }
            s_reaper ??= new Timer(_ => ReapIfOobeOver(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
#if WINDOWS
            if (!s_logonHooked)
            {
                s_logonHooked = true;
                WindowsServiceHost.SessionLogon += () => _ = System.Threading.Tasks.Task.Run(ReapIfOobeOver);
            }
#endif
        }
        Console.WriteLine($"[oobe-session] started {tag} pid {p.Id}");
    }

    /// <summary>Ends every tracked process once OOBE is over, then stops the timer.</summary>
    internal static void ReapIfOobeOver()
    {
        List<(int Pid, long StartTicks)> victims;
        lock (s_lock)
        {
            if (!OobePanelPolicy.ShouldReap(s_tracked.Count, OobeDetector.IsInOobe)) return;
            victims = new List<(int, long)>(s_tracked);
            s_tracked.Clear();
            s_reaper?.Dispose();
            s_reaper = null;
            try { File.Delete(PidFilePath); } catch { }
        }
        foreach (var (pid, ticks) in victims) Kill(pid, ticks);
        Console.WriteLine($"[oobe-session] OOBE over; ended {victims.Count} setup-session process(es)");
    }

    /// <summary>Starts <paramref name="exePath"/> on the console session's default desktop; null when it could not.</summary>
    internal static Process? Start(string exePath, string arguments, string workingDir, string tag)
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows()) return null;
        var p = StartWindows(exePath, arguments, workingDir, tag);
        if (p is not null) Track(p, tag);
        return p;
#else
        return null;
#endif
    }

#if WINDOWS
    [SupportedOSPlatform("windows")]
    private static Process? StartWindows(string exePath, string arguments, string workingDir, string tag)
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF) return null;
        IntPtr token = IntPtr.Zero, env = IntPtr.Zero, desktop = IntPtr.Zero;
        try
        {
            string source;
            if (WTSQueryUserToken(session, out token) && token != IntPtr.Zero)
            {
                source = "console user token";
            }
            else
            {
                token = StrippedServiceToken(session);
                source = "service token, privileges stripped";
                if (token == IntPtr.Zero)
                {
                    Console.Error.WriteLine($"[oobe-session] no token for {tag}: {Marshal.GetLastWin32Error()}");
                    return null;
                }
            }
            if (!CreateEnvironmentBlock(out env, token, false)) env = IntPtr.Zero;
            desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = desktop };
            var cmd = new StringBuilder($"\"{exePath}\"");
            if (arguments.Length > 0) cmd.Append(' ').Append(arguments);
            const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
            const uint CREATE_NO_WINDOW = 0x08000000;
            if (!CreateProcessAsUserW(token, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, workingDir, ref si, out var pi))
            {
                Console.Error.WriteLine($"[oobe-session] CreateProcessAsUser failed for {tag} ({source}): {Marshal.GetLastWin32Error()}");
                return null;
            }
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            Console.WriteLine($"[oobe-session] {tag} launched with {source}");
            return Process.GetProcessById((int)pi.dwProcessId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[oobe-session] launch of {tag} threw: {ex.Message}");
            return null;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (desktop != IntPtr.Zero) Marshal.FreeHGlobal(desktop);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr StrippedServiceToken(uint session)
    {
        const uint TOKEN_ALL_NEEDED = 0x0002 | 0x0001 | 0x0008 | 0x0080 | 0x0100; // DUPLICATE, ASSIGN_PRIMARY, QUERY, ADJUST_DEFAULT, ADJUST_SESSIONID
        IntPtr own = IntPtr.Zero, dup = IntPtr.Zero, restricted = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_NEEDED, out own)) return IntPtr.Zero;
            // SecurityImpersonation = 2, TokenPrimary = 1
            if (!DuplicateTokenEx(own, 0x02000000, IntPtr.Zero, 2, 1, out dup)) return IntPtr.Zero;
            // TokenSessionId = 12
            if (!SetTokenInformation(dup, 12, ref session, sizeof(uint))) return IntPtr.Zero;
            // DISABLE_MAX_PRIVILEGE = 1
            if (!CreateRestrictedToken(dup, 1, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out restricted)) return IntPtr.Zero;
            return restricted;
        }
        finally
        {
            if (own != IntPtr.Zero) CloseHandle(own);
            if (dup != IntPtr.Zero) CloseHandle(dup);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint info, int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(IntPtr existing, uint flags, uint disableSidCount, IntPtr sidsToDisable,
        uint deletePrivCount, IntPtr privsToDelete, uint restrictSidCount, IntPtr sidsToRestrict, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr env);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string? application, StringBuilder commandLine,
        IntPtr processAttrs, IntPtr threadAttrs, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr env, string? currentDirectory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
#endif
}
