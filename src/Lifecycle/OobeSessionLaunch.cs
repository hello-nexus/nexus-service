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
/// Starts a process in the console session while Windows is in OOBE, where the
/// ordinary schtasks route cannot show the panel. Reached through
/// <see cref="OobePanelPolicy.Decide"/> returning OobeNoUser (nobody signed in) or
/// OobeTempUser (the temporary setup account is signed in).
///
/// Token: the console session's user token when there is one. With nobody signed in
/// and <c>allowServiceToken</c>, this service's own token retargeted to the console
/// session instead, with privileges stripped, the Administrators group made
/// deny-only and Medium integrity; the SYSTEM user SID stays, since a token cannot
/// change its user. Every process started here is tracked and ended once OOBE is
/// over or a real user is on the console, so that user's overlay and helper start
/// clean.
/// </summary>
internal static class OobeSessionLaunch
{
    private static readonly object s_lock = new();
    // Start time as the raw FILETIME value of GetProcessTimes, the same source on both sides.
    private static readonly List<(int Pid, long StartFileTime, bool Helper, int Session, bool ServiceToken)> s_tracked = new();
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
                if (parts.Length == 2 && int.TryParse(parts[0], out var pid) && long.TryParse(parts[1], out var fileTime)
                    && pid != Environment.ProcessId)
                {
                    Kill(pid, fileTime, waitForExit: false);
                }
            }
            File.Delete(PidFilePath);
        }
        catch { /* best-effort */ }
    }

    // The pid file is writable by others, so a process is ended only when its start time
    // and its image both match what this service launched. The open handle keeps the pid
    // from being reused while the tree is ended.
    private static bool Kill(int pid, long startFileTime, bool waitForExit = true)
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows()) return false;
        var handle = OpenProcess(ProcessQueryLimited | ProcessTerminate | Synchronize, false, (uint)pid);
        if (handle == IntPtr.Zero) return false;
        var exitWaitMs = waitForExit ? ExitWaitMs : 0;
        try
        {
            if (!GetProcessTimes(handle, out var created, out _, out _, out _)
                || FileTimeValue(created) != startFileTime)
            {
                return false;
            }
            var buf = new StringBuilder(1024);
            var size = (uint)buf.Capacity;
            if (!QueryFullProcessImageNameW(handle, 0, buf, ref size)
                || !OobePanelPolicy.IsOwnImage(buf.ToString(), AppContext.BaseDirectory))
            {
                return false;
            }
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                // The next launch may need what this process held (the helper's session mutex).
                WaitForSingleObject(handle, exitWaitMs);
                return true;
            }
            catch
            {
                // The root may already be gone when ending a child threw.
                if (WaitForSingleObject(handle, 0) == WaitObject0) return true;
                if (!TerminateProcess(handle, 1)) return false;
                WaitForSingleObject(handle, exitWaitMs);
                return true;
            }
        }
        finally
        {
            CloseHandle(handle);
        }
#else
        return false;
#endif
    }

    // Whether the tracked process is still the one launched: running, with the same start time.
    private static bool StillRunning(int pid, long startFileTime)
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows()) return false;
        var handle = OpenProcess(ProcessQueryLimited | Synchronize, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            // Only "no such process" means it is gone; any other failure keeps the entry.
            return Marshal.GetLastWin32Error() != ErrorInvalidParameter;
        }
        try
        {
            return WaitForSingleObject(handle, 0) != WaitObject0
                && GetProcessTimes(handle, out var created, out _, out _, out _)
                && FileTimeValue(created) == startFileTime;
        }
        finally
        {
            CloseHandle(handle);
        }
#else
        return false;
#endif
    }

    private static readonly ReapedHelperSet s_reaped = new(
        // Monotonic: OOBE often applies a wall-clock jump.
        () => new DateTime(Environment.TickCount64 * TimeSpan.TicksPerMillisecond), StartMatchesOrGone, TimeSpan.FromMinutes(2));

    /// <summary>True once for a helper pid the reap ended; also forgets it.</summary>
    internal static bool TakeReaped(int pid) => s_reaped.Take(pid);

    /// <summary>Whether a registered helper connection is a setup helper that has been ended.</summary>
    internal static bool IsReapedHelper(int pid) => s_reaped.IsReaped(pid);

    /// <summary>A new helper connected in <paramref name="session"/>: nothing recorded about an earlier one there applies.</summary>
    internal static void ClearReaped(int session) => s_reaped.Clear(session);

    // A pid that now runs a different process (reused) is not the one that was ended. Only "no such
    // process" means gone; any other open failure is not a match.
    private static bool StartMatchesOrGone(int pid, long startFileTime)
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows()) return true;
        var handle = OpenProcess(ProcessQueryLimited, false, (uint)pid);
        if (handle == IntPtr.Zero) return Marshal.GetLastWin32Error() == ErrorInvalidParameter;
        try { return !GetProcessTimes(handle, out var created, out _, out _, out _) || FileTimeValue(created) == startFileTime; }
        finally { CloseHandle(handle); }
#else
        return true;
#endif
    }

    private static void PersistLocked()
    {
        try
        {
            if (s_tracked.Count == 0)
            {
                if (File.Exists(PidFilePath)) File.Delete(PidFilePath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(PidFilePath)!);
            File.WriteAllLines(PidFilePath, s_tracked.ConvertAll(t => $"{t.Pid},{t.StartFileTime}"));
        }
        catch { /* best-effort */ }
    }

    private static void Track(int pid, long startFileTime, string tag, int session, bool serviceToken)
    {
        lock (s_lock)
        {
            s_tracked.Add((pid, startFileTime, tag == "helper", session, serviceToken));
            PersistLocked();
            s_reaper ??= new Timer(_ => { ReplaceServiceTokenLaunchesForTempUser(); ReapIfOobeOver(); }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
#if WINDOWS
            if (!s_logonHooked)
            {
                s_logonHooked = true;
                WindowsServiceHost.SessionLogon += () => _ = System.Threading.Tasks.Task.Run(() => ReapIfOobeOver());
            }
#endif
        }
        Console.WriteLine($"[oobe-session] started {tag} pid {pid}");
    }

    private static void Untrack(int pid)
    {
        lock (s_lock)
        {
            s_tracked.RemoveAll(t => t.Pid == pid);
            PersistLocked();
            if (s_tracked.Count == 0)
            {
                s_reaper?.Dispose();
                s_reaper = null;
            }
        }
    }

    internal static string ConsoleUser()
    {
#if WINDOWS
        return OperatingSystem.IsWindows() ? UserHelperBootstrapper.ResolveActiveConsoleUsername() : string.Empty;
#else
        return string.Empty;
#endif
    }

    /// <summary>
    /// Whether the console session's account is local to this PC: the OOBE temporary account
    /// is, a domain or Microsoft account that happens to be named alike is not.
    /// </summary>
    internal static bool ConsoleUserIsLocal()
    {
#if WINDOWS
        return OperatingSystem.IsWindows()
            && string.Equals(ConsoleSessionAccount.ConsoleDomain(), Environment.MachineName, StringComparison.OrdinalIgnoreCase);
#else
        return false;
#endif
    }

    /// <summary>
    /// Ends every tracked process once OOBE is over or a real user is on the console, then stops
    /// the timer. Returns how many it ended. Runs from a timer and from the logon path, which calls
    /// it before deciding whether a helper is already present.
    /// </summary>
    internal static int ReapIfOobeOver()
    {
        try
        {
            List<(int Pid, long StartFileTime, bool Helper, int Session, bool ServiceToken)> victims;
            lock (s_lock)
            {
                PruneExitedLocked();
                if (!OobePanelPolicy.ShouldReap(s_tracked.Count, OobeDetector.IsInOobe, () => OobePanelPolicy.IsRealUser(ConsoleUser()))) return 0;
                victims = new List<(int, long, bool, int, bool)>(s_tracked);
                s_tracked.Clear();
                // Recorded before the kill: the helper's pipe drops while it is still exiting.
                foreach (var v in victims)
                {
                    if (v.Helper) s_reaped.Add(v.Pid, v.StartFileTime, v.Session);
                }
                s_reaper?.Dispose();
                s_reaper = null;
                PersistLocked();
            }
            var ended = 0;
            foreach (var (pid, fileTime, _, _, _) in victims)
            {
                if (Kill(pid, fileTime)) ended++;
            }
            Console.WriteLine($"[oobe-session] OOBE over or a real user is on the console; ended {ended} of {victims.Count} setup-session process(es)");
            return ended;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[oobe-session] reap failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Raised after setup processes started with the service token were ended because the temporary setup account signed in.</summary>
    internal static event Action? ServiceTokenLaunchesReplaced;

    /// <summary>
    /// The setup panel is first started while nobody is signed in, with the service token. Once the
    /// temporary setup account is on the console those processes are ended so the next launch uses
    /// that account's token; WebView2 cannot run as the SYSTEM user the service token carries.
    /// Returns how many it ended. A no-op unless something tracked used the service token.
    /// </summary>
    internal static int ReplaceServiceTokenLaunchesForTempUser()
    {
        try
        {
            List<(int Pid, long StartFileTime, bool Helper, int Session, bool ServiceToken)> victims;
            lock (s_lock)
            {
                PruneExitedLocked();
                var serviceTokenCount = s_tracked.FindAll(t => t.ServiceToken).Count;
                if (serviceTokenCount == 0) return 0;
                var user = ConsoleUser();
                var plan = OobePanelPolicy.Decide(user, ConsoleSessionExists(), OobeDetector.IsInOobe, ConsoleUserIsLocal);
                if (!OobePanelPolicy.ShouldReplaceServiceTokenLaunches(plan, serviceTokenCount)) return 0;
                victims = s_tracked.FindAll(t => t.ServiceToken);
                s_tracked.RemoveAll(t => t.ServiceToken);
                foreach (var v in victims)
                {
                    if (v.Helper) s_reaped.Add(v.Pid, v.StartFileTime, v.Session);
                }
                PersistLocked();
                Console.WriteLine($"[oobe-session] plan={plan} for console user '{user}': ending {victims.Count} process(es) started with the service token so they restart with the user token");
            }
            var ended = 0;
            foreach (var (pid, fileTime, _, _, _) in victims)
            {
                if (Kill(pid, fileTime)) ended++;
            }
            try { ServiceTokenLaunchesReplaced?.Invoke(); }
            catch (Exception ex) { Console.Error.WriteLine($"[oobe-session] replace handler failed: {ex.Message}"); }
            return ended;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[oobe-session] replace failed: {ex.Message}");
            return 0;
        }
    }

    // Drops entries whose process has exited; with none left the timer has nothing to watch.
    private static void PruneExitedLocked()
    {
        if (s_tracked.Count == 0) return;
        if (s_tracked.RemoveAll(t => !StillRunning(t.Pid, t.StartFileTime)) == 0) return;
        PersistLocked();
        if (s_tracked.Count == 0)
        {
            s_reaper?.Dispose();
            s_reaper = null;
        }
    }

    /// <summary>
    /// Starts <paramref name="exePath"/> on the console session's default desktop; null when it could not.
    /// <paramref name="plan"/> is the decision being carried out: only OobeNoUser permits the
    /// stripped service token, when the console session has no user token.
    /// </summary>
    internal static Process? Start(string exePath, string arguments, string workingDir, string tag, ConsoleLaunchPlan plan)
    {
#if WINDOWS
        return OperatingSystem.IsWindows() ? StartWindows(exePath, arguments, workingDir, tag, plan) : null;
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
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimited = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint WaitObject0 = 0;
    private const uint ExitWaitMs = 5000;
    private const int ErrorInvalidParameter = 87;

    private static long FileTimeValue(FILETIME t) => ((long)t.dwHighDateTime << 32) | t.dwLowDateTime;

    [SupportedOSPlatform("windows")]
    private static Process? StartWindows(string exePath, string arguments, string workingDir, string tag, ConsoleLaunchPlan plan)
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF) return null;
        IntPtr token = IntPtr.Zero, env = IntPtr.Zero, desktop = IntPtr.Zero;
        try
        {
            string source;
            var serviceToken = false;
            if (WTSQueryUserToken(session, out token) && token != IntPtr.Zero)
            {
                source = "console user token";
            }
            else
            {
                var wtsError = Marshal.GetLastWin32Error();
                if (plan != ConsoleLaunchPlan.OobeNoUser)
                {
                    Console.Error.WriteLine($"[oobe-session] plan={plan}: no user token for {tag} (WTSQueryUserToken error {wtsError}); not using the service token");
                    return null;
                }
                Console.WriteLine($"[oobe-session] plan={plan}: no console user, {tag} falls back to the stripped service token (WTSQueryUserToken error {wtsError})");
                serviceToken = true;
                token = StrippedServiceToken(session, out var stripError, out var stripStep);
                if (token == IntPtr.Zero)
                {
                    Console.Error.WriteLine($"[oobe-session] no token for {tag}: WTSQueryUserToken error {wtsError}, {stripStep} error {stripError}");
                    return null;
                }
                source = "stripped service token";
            }
            if (!CreateEnvironmentBlock(out env, token, false)) env = IntPtr.Zero;
            desktop = Marshal.StringToHGlobalUni(@"winsta0\default");
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = desktop };
            var cmd = new StringBuilder($"\"{exePath}\"");
            if (arguments.Length > 0) cmd.Append(' ').Append(arguments);
            // A real user may have signed in since the caller decided; that user's processes
            // start through the normal path.
            if (OobePanelPolicy.IsRealUser(ConsoleUser())) return null;
            if (!CreateProcessAsUserW(token, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment | CreateNoWindow, env, workingDir, ref si, out var pi))
            {
                var error = Marshal.GetLastWin32Error();
                Console.Error.WriteLine($"[oobe-session] CreateProcessAsUser failed for {tag} ({source}): {error}");
                return null;
            }
            CloseHandle(pi.hThread);
            // The create's own handle is held until the process is tracked or ended, so a failure
            // below can never leave a live process nothing knows about.
            try
            {
                if (!GetProcessTimes(pi.hProcess, out var created, out _, out _, out _))
                {
                    TerminateProcess(pi.hProcess, 1);
                    return null;
                }
                var pid = (int)pi.dwProcessId;
                Track(pid, FileTimeValue(created), tag, (int)session, serviceToken);
                Console.WriteLine($"[oobe-session] plan={plan}: {tag} launched in the console session as {source}");
                try { return Process.GetProcessById(pid); }
                catch
                {
                    TerminateProcess(pi.hProcess, 1);
                    Untrack(pid);
                    return null;
                }
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
    /// On failure returns zero with the Win32 error and the step that failed, captured at the call.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IntPtr StrippedServiceToken(uint session, out int error, out string step)
    {
        const uint Access = TokenDuplicate | TokenAssignPrimary | TokenQuery | TokenAdjustDefault | TokenAdjustSessionId;
        error = 0;
        step = "";
        IntPtr own = IntPtr.Zero, dup = IntPtr.Zero, restricted = IntPtr.Zero;
        IntPtr adminSid = IntPtr.Zero, adminSidBuf = IntPtr.Zero, labelBuf = IntPtr.Zero, mediumSid = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), Access, out own)) { error = Marshal.GetLastWin32Error(); step = "OpenProcessToken"; return IntPtr.Zero; }
            if (!DuplicateTokenEx(own, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out dup)) { error = Marshal.GetLastWin32Error(); step = "DuplicateTokenEx"; return IntPtr.Zero; }
            if (!SetTokenInformation(dup, TokenSessionIdClass, ref session, sizeof(uint))) { error = Marshal.GetLastWin32Error(); step = "SetTokenInformation(session)"; return IntPtr.Zero; }

            if (!ConvertStringSidToSidW(MediumIntegritySid, out mediumSid)) { error = Marshal.GetLastWin32Error(); step = "ConvertStringSidToSid"; return IntPtr.Zero; }
            labelBuf = Marshal.AllocHGlobal(Marshal.SizeOf<SID_AND_ATTRIBUTES>());
            Marshal.StructureToPtr(new SID_AND_ATTRIBUTES { Sid = mediumSid, Attributes = SeGroupIntegrity }, labelBuf, false);
            if (!SetTokenInformation(dup, TokenIntegrityLevelClass, labelBuf, Marshal.SizeOf<SID_AND_ATTRIBUTES>() + (int)GetLengthSid(mediumSid)))
            {
                error = Marshal.GetLastWin32Error(); step = "SetTokenInformation(integrity)"; return IntPtr.Zero;
            }

            var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            var bytes = new byte[admins.BinaryLength];
            admins.GetBinaryForm(bytes, 0);
            adminSid = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, adminSid, bytes.Length);
            adminSidBuf = Marshal.AllocHGlobal(Marshal.SizeOf<SID_AND_ATTRIBUTES>());
            Marshal.StructureToPtr(new SID_AND_ATTRIBUTES { Sid = adminSid, Attributes = 0 }, adminSidBuf, false);

            if (!CreateRestrictedToken(dup, DisableMaxPrivilege, 1, adminSidBuf, 0, IntPtr.Zero, 0, IntPtr.Zero, out restricted))
            {
                error = Marshal.GetLastWin32Error(); step = "CreateRestrictedToken"; return IntPtr.Zero;
            }
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

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

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
