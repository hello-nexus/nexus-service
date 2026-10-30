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
/// service's own token retargeted to the console session, with its privileges
/// stripped, the Administrators group made deny-only and the integrity level
/// lowered. The SYSTEM user SID stays: a token cannot change its user. Every
/// process started here is tracked and ended once OOBE is over or a user is on
/// the console, so the real user's overlay and helper start clean.
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
            // The start time rejects a recycled pid and the image check rejects a pid file
            // someone else wrote; only the process itself is ended.
            if (!p.HasExited
                && p.StartTime.ToUniversalTime().Ticks == startTicks
                && OobePanelPolicy.IsOwnImage(p.MainModule?.FileName, AppContext.BaseDirectory))
            {
                p.Kill(entireProcessTree: false);
            }
        }
        catch { /* already gone */ }
    }

    private static void Track(int pid, long startTicks, string tag)
    {
        lock (s_lock)
        {
            s_tracked.Add((pid, startTicks));
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
        Console.WriteLine($"[oobe-session] started {tag} pid {pid}");
    }

    private static void Untrack(int pid)
    {
        lock (s_lock) { s_tracked.RemoveAll(t => t.Pid == pid); }
    }

    internal static string ConsoleUser()
    {
#if WINDOWS
        return OperatingSystem.IsWindows() ? UserHelperBootstrapper.ResolveActiveConsoleUsername() : string.Empty;
#else
        return string.Empty;
#endif
    }

    /// <summary>Ends every tracked process once OOBE is over, then stops the timer.</summary>
    internal static void ReapIfOobeOver()
    {
        List<(int Pid, long StartTicks)> victims;
        lock (s_lock)
        {
            if (!OobePanelPolicy.ShouldReap(s_tracked.Count, OobeDetector.IsInOobe, () => !string.IsNullOrEmpty(ConsoleUser()))) return;
            victims = new List<(int, long)>(s_tracked);
            s_tracked.Clear();
            s_reaper?.Dispose();
            s_reaper = null;
            try { File.Delete(PidFilePath); } catch { }
        }
        foreach (var (pid, ticks) in victims) Kill(pid, ticks);
        Console.WriteLine($"[oobe-session] OOBE over or a user is on the console; ended {victims.Count} setup-session process(es)");
    }

    /// <summary>Starts <paramref name="exePath"/> on the console session's default desktop; null when it could not.</summary>
    internal static Process? Start(string exePath, string arguments, string workingDir, string tag)
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows()) return null;
        var launched = StartWindows(exePath, arguments, workingDir, tag);
        if (launched is null) return null;
        var (pid, ticks) = launched.Value;
        try { return Process.GetProcessById(pid); }
        catch
        {
            Kill(pid, ticks);
            Untrack(pid);
            return null;
        }
#else
        return null;
#endif
    }

#if WINDOWS
    private const uint CreateUnicodeEnvironment = 0x400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionIdClass = 12;
    private const int TokenIntegrityLevelClass = 25;
    private const uint DisableMaxPrivilege = 1;
    private const uint SeGroupIntegrity = 0x20;
    private const string MediumIntegritySid = "S-1-16-8192";

    [SupportedOSPlatform("windows")]
    private static (int Pid, long StartTicks)? StartWindows(string exePath, string arguments, string workingDir, string tag)
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
            // A user may have signed in since the caller decided nobody had; that user's
            // processes start through the normal path.
            if (!string.IsNullOrEmpty(ConsoleUser())) return null;
            if (!CreateProcessAsUserW(token, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment | CreateNoWindow, env, workingDir, ref si, out var pi))
            {
                var error = Marshal.GetLastWin32Error();
                Console.Error.WriteLine($"[oobe-session] CreateProcessAsUser failed for {tag} ({source}): {error}");
                return null;
            }
            CloseHandle(pi.hThread);
            try
            {
                // Tracked from the handle the create returned, before anything else can throw.
                if (!GetProcessTimes(pi.hProcess, out var created, out _, out _, out _))
                {
                    TerminateProcess(pi.hProcess, 1);
                    return null;
                }
                var ticks = DateTime.FromFileTimeUtc(((long)created.dwHighDateTime << 32) | created.dwLowDateTime).Ticks;
                Track((int)pi.dwProcessId, ticks, tag);
                Console.WriteLine($"[oobe-session] {tag} launched with {source}");
                return ((int)pi.dwProcessId, ticks);
            }
            finally
            {
                CloseHandle(pi.hProcess);
            }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    /// <summary>
    /// The service token, retargeted to the console session, with privileges removed,
    /// the Administrators group deny-only and Medium integrity. The user stays SYSTEM.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IntPtr StrippedServiceToken(uint session)
    {
        const uint Access = TokenDuplicate | TokenAssignPrimary | TokenQuery | TokenAdjustDefault | TokenAdjustSessionId;
        IntPtr own = IntPtr.Zero, dup = IntPtr.Zero, restricted = IntPtr.Zero;
        IntPtr adminSid = IntPtr.Zero, adminSidBuf = IntPtr.Zero, labelBuf = IntPtr.Zero, mediumSid = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), Access, out own)) return IntPtr.Zero;
            if (!DuplicateTokenEx(own, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out dup)) return IntPtr.Zero;
            if (!SetTokenInformation(dup, TokenSessionIdClass, ref session, sizeof(uint))) return IntPtr.Zero;

            if (!ConvertStringSidToSidW(MediumIntegritySid, out mediumSid)) return IntPtr.Zero;
            labelBuf = Marshal.AllocHGlobal(Marshal.SizeOf<SID_AND_ATTRIBUTES>());
            Marshal.StructureToPtr(new SID_AND_ATTRIBUTES { Sid = mediumSid, Attributes = SeGroupIntegrity }, labelBuf, false);
            if (!SetTokenInformation(dup, TokenIntegrityLevelClass, labelBuf, Marshal.SizeOf<SID_AND_ATTRIBUTES>() + (int)GetLengthSid(mediumSid))) return IntPtr.Zero;

            var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            var bytes = new byte[admins.BinaryLength];
            admins.GetBinaryForm(bytes, 0);
            adminSid = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, adminSid, bytes.Length);
            adminSidBuf = Marshal.AllocHGlobal(Marshal.SizeOf<SID_AND_ATTRIBUTES>());
            Marshal.StructureToPtr(new SID_AND_ATTRIBUTES { Sid = adminSid, Attributes = 0 }, adminSidBuf, false);

            if (!CreateRestrictedToken(dup, DisableMaxPrivilege, 1, adminSidBuf, 0, IntPtr.Zero, 0, IntPtr.Zero, out restricted)) return IntPtr.Zero;
            return restricted;
        }
        finally
        {
            if (own != IntPtr.Zero) CloseHandle(own);
            if (dup != IntPtr.Zero) CloseHandle(dup);
            if (labelBuf != IntPtr.Zero) Marshal.FreeHGlobal(labelBuf);
            if (adminSidBuf != IntPtr.Zero) Marshal.FreeHGlobal(adminSidBuf);
            if (adminSid != IntPtr.Zero) Marshal.FreeHGlobal(adminSid);
            if (mediumSid != IntPtr.Zero) LocalFree(mediumSid);
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

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "SetTokenInformation")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSidToSidW(string stringSid, out IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern uint GetLengthSid(IntPtr sid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

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
