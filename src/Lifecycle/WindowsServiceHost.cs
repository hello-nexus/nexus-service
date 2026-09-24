#if WINDOWS
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Windows SCM dispatcher for running the Nexus daemon as a real Windows
/// Service (LocalSystem, Automatic). AOT-safe: raw P/Invoke against
/// advapi32.dll, no `System.ServiceProcess.ServiceBase` reflection.
///
/// Usage from Program.cs:
///
///   if (args.Contains("--service"))
///   {
///       return WindowsServiceHost.Run(args, RunWebApp);
///   }
///
/// Where RunWebApp(args, ct) builds and runs the WebApplication, returning
/// when ct is cancelled.
///
/// When launched outside SCM (e.g. directly from the shell with --service),
/// StartServiceCtrlDispatcher returns ERROR_FAILED_SERVICE_CONTROLLER_CONNECT
/// (1063) and we fall back to plain console mode.
/// </summary>
internal static class WindowsServiceHost
{
    private const string ServiceName = "NexusService";

    // Service state codes
    private const uint SERVICE_STOPPED = 0x00000001;
    private const uint SERVICE_START_PENDING = 0x00000002;
    private const uint SERVICE_STOP_PENDING = 0x00000003;
    private const uint SERVICE_RUNNING = 0x00000004;

    // Service type
    private const uint SERVICE_WIN32_OWN_PROCESS = 0x00000010;

    // Accepted controls
    private const uint SERVICE_ACCEPT_STOP = 0x00000001;
    private const uint SERVICE_ACCEPT_SHUTDOWN = 0x00000004;
    private const uint SERVICE_ACCEPT_SESSIONCHANGE = 0x00000080;
    // Accepting preshutdown is what makes the OS-shutdown path reachable at all.
    // Measured on T1 2026-08-27: without it the earliest signal a service sees is
    // the console CTRL_SHUTDOWN event at logoff+0.22s, which .NET's ConsoleLifetime
    // turns into StopApplication - so the process is already gone when
    // SERVICE_CONTROL_SHUTDOWN arrives 5.0s later and IsOsShutdown never gets set.
    // Preshutdown lands at logoff+1.14s, BEFORE that console event is dispatched,
    // and the SCM waited 25s for the probe without complaint.
    private const uint SERVICE_ACCEPT_PRESHUTDOWN = 0x00000100;

    // Control codes
    private const uint SERVICE_CONTROL_STOP = 0x00000001;
    private const uint SERVICE_CONTROL_SHUTDOWN = 0x00000005;
    private const uint SERVICE_CONTROL_PRESHUTDOWN = 0x0000000F;
    private const uint SERVICE_CONTROL_INTERROGATE = 0x00000004;
    private const uint SERVICE_CONTROL_SESSIONCHANGE = 0x0000000E;

    // WTS session-change event types. The lock pair drives the blackout;
    // logon re-arms the user-session helper spawn. Console connect and the
    // rest describe a session appearing, which is neither.
    private const uint WTS_SESSION_LOGON = 0x5;
    private const uint WTS_SESSION_LOCK = 0x7;
    private const uint WTS_SESSION_UNLOCK = 0x8;

    // Common Win32 error
    private const int ERROR_FAILED_SERVICE_CONTROLLER_CONNECT = 1063;

    // No standard error to return to SCM
    private const uint NO_ERROR = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_TABLE_ENTRYW
    {
        public IntPtr lpServiceName;       // LPWSTR
        public IntPtr lpServiceProc;       // LPSERVICE_MAIN_FUNCTIONW
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "StartServiceCtrlDispatcherW")]
    private static extern unsafe bool StartServiceCtrlDispatcherW(SERVICE_TABLE_ENTRYW* lpServiceStartTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "RegisterServiceCtrlHandlerExW")]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpServiceName,
        IntPtr lpHandlerProc,
        IntPtr lpContext);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr hServiceStatus, ref SERVICE_STATUS lpServiceStatus);

    // Static state shared between the main thread and the SCM-spawned ServiceMain thread.
    // The dispatcher blocks the main thread, so the application logic runs in a separate
    // Task started from ServiceMain.
    private static string[] s_args = Array.Empty<string>();
    private static Func<string[], CancellationToken, Task<int>>? s_runWebApp;
    private static IntPtr s_statusHandle = IntPtr.Zero;
    private static SERVICE_STATUS s_status;
    private static CancellationTokenSource? s_cts;
    private static Task<int>? s_appTask;
    private static int s_appExitCode;
    private static readonly ManualResetEventSlim s_stoppedEvent = new(initialState: false);

    /// <summary>
    /// Run the daemon under SCM. If SCM connection fails (e.g. when invoked
    /// directly from a shell with --service for testing), falls back to a
    /// plain console execution of <paramref name="runWebApp"/>.
    /// Returns the process exit code.
    /// </summary>
    public static unsafe int Run(string[] args, Func<string[], CancellationToken, Task<int>> runWebApp)
    {
        s_args = args;
        s_runWebApp = runWebApp;

        var nameBytes = Marshal.StringToHGlobalUni(ServiceName);
        try
        {
            var table = stackalloc SERVICE_TABLE_ENTRYW[2];
            table[0].lpServiceName = nameBytes;
            table[0].lpServiceProc = (IntPtr)(delegate* unmanaged[Stdcall]<uint, IntPtr, void>)&ServiceMain;
            table[1].lpServiceName = IntPtr.Zero;
            table[1].lpServiceProc = IntPtr.Zero;

            if (StartServiceCtrlDispatcherW(table))
            {
                // Dispatcher returned cleanly after the service stopped.
                s_stoppedEvent.Wait(TimeSpan.FromSeconds(30));
                return s_appExitCode;
            }

            var err = Marshal.GetLastPInvokeError();
            if (err == ERROR_FAILED_SERVICE_CONTROLLER_CONNECT)
            {
                // Not under SCM (e.g. dev launched the EXE with --service from a shell).
                // Fall back to console run so the same flag is useful for local testing.
                Console.WriteLine("[nexus-service] --service used outside SCM; running in console fallback mode");
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                return runWebApp(args, cts.Token).GetAwaiter().GetResult();
            }

            Console.Error.WriteLine($"[nexus-service] StartServiceCtrlDispatcher failed: {err}");
            return err;
        }
        finally
        {
            Marshal.FreeHGlobal(nameBytes);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static unsafe void ServiceMain(uint dwArgc, IntPtr lpszArgv)
    {
        try
        {
            // Register the control handler before anything else.
            var handlerPtr = (IntPtr)(delegate* unmanaged[Stdcall]<uint, uint, IntPtr, IntPtr, uint>)&ControlHandler;
            s_statusHandle = RegisterServiceCtrlHandlerExW(ServiceName, handlerPtr, IntPtr.Zero);
            if (s_statusHandle == IntPtr.Zero)
            {
                Console.Error.WriteLine($"[nexus-service] RegisterServiceCtrlHandlerEx failed: {Marshal.GetLastPInvokeError()}");
                return;
            }

            ReportStatus(SERVICE_START_PENDING, waitHintMs: 15_000, controlsAccepted: 0);

            // Start the web app on a worker thread. The dispatcher needs ServiceMain
            // to return promptly with RUNNING status; the app loop runs in s_appTask.
            s_cts = new CancellationTokenSource();
            s_appTask = Task.Run(RunAppGuardedAsync);

            ReportStatus(SERVICE_RUNNING, waitHintMs: 0,
                controlsAccepted: SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN
                    | SERVICE_ACCEPT_PRESHUTDOWN | SERVICE_ACCEPT_SESSIONCHANGE);

            // Block ServiceMain until the app task finishes (either it crashed or the
            // control handler signalled cancellation).
            try { s_appExitCode = s_appTask.GetAwaiter().GetResult(); }
            catch { s_appExitCode = -1; }

            ReportStatus(SERVICE_STOPPED, waitHintMs: 0, controlsAccepted: 0);
        }
        finally
        {
            s_stoppedEvent.Set();
        }

        // SCM now has a clean STOPPED. Exit immediately rather than returning to
        // the dispatcher and unwinding the host (DisposeAsync) or blocking on a
        // lingering foreground thread. A clean stop means no failure-action
        // restart, and the process death reaps every child via its kill-job.
        Environment.Exit(s_appExitCode);
    }

    /// <summary>
    /// Raised on a session lock (true) or unlock (false), from the SCM control
    /// handler thread. Static because the handler is a bare function pointer
    /// registered before any DI container exists; subscribers must return
    /// promptly, as SCM is waiting on this callback.
    /// </summary>
    internal static event Action<bool>? SessionLockChanged;

    /// <summary>A user logged in. The GPU selection listens: the service starts
    /// before any session exists, and a card that needs one has nothing else to
    /// wake it.</summary>
    internal static event Action? SessionLogon;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static uint ControlHandler(uint dwControl, uint dwEventType, IntPtr lpEventData, IntPtr lpContext)
    {
        switch (dwControl)
        {
            // Preshutdown is the one that actually fires on a machine going down;
            // SHUTDOWN stays handled for the case where preshutdown is unavailable.
            case SERVICE_CONTROL_PRESHUTDOWN:
            case SERVICE_CONTROL_SHUTDOWN:
                HostShutdown.IsOsShutdown = true;
                goto case SERVICE_CONTROL_STOP;
            case SERVICE_CONTROL_STOP:
                ReportStatus(SERVICE_STOP_PENDING, waitHintMs: 15_000, controlsAccepted: 0);
                try { s_cts?.Cancel(); } catch { }
                return NO_ERROR;

            case SERVICE_CONTROL_SESSIONCHANGE:
                // The only route to lock state from session 0: a service sees no
                // window messages, and Microsoft.Win32.SystemEvents' SessionSwitch
                // rides those. Delivered per session; the session id sits in
                // lpEventData (WTSSESSION_NOTIFICATION) and is deliberately not
                // read - a machine with one interactive user is the shape this
                // ships into, and the lighting is that machine's, not a session's.
                if (dwEventType == WTS_SESSION_LOCK || dwEventType == WTS_SESSION_UNLOCK)
                {
                    try { SessionLockChanged?.Invoke(dwEventType == WTS_SESSION_LOCK); }
                    catch { }
                }
                else if (dwEventType == WTS_SESSION_LOGON)
                {
                    try { SessionLogon?.Invoke(); }
                    catch { }
                }
                return NO_ERROR;

            case SERVICE_CONTROL_INTERROGATE:
                // Just re-report current status.
                if (s_statusHandle != IntPtr.Zero)
                {
                    SetServiceStatus(s_statusHandle, ref s_status);
                }
                return NO_ERROR;

            default:
                return NO_ERROR;
        }
    }

    // Pulled out of ServiceMain because we can't `await` inside an unsafe
    // context, and the whole class is unsafe (function-pointer fields).
    private static async Task<int> RunAppGuardedAsync()
    {
        try
        {
            return await s_runWebApp!(s_args, s_cts!.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[nexus-service] app crashed: {ex}");
            return -1;
        }
    }

    private static void ReportStatus(uint state, uint waitHintMs, uint controlsAccepted)
    {
        s_status.dwServiceType = SERVICE_WIN32_OWN_PROCESS;
        s_status.dwCurrentState = state;
        s_status.dwControlsAccepted = controlsAccepted;
        s_status.dwWin32ExitCode = NO_ERROR;
        s_status.dwServiceSpecificExitCode = 0;
        s_status.dwWaitHint = waitHintMs;

        // Advance checkpoint when in a *_PENDING state, freeze when terminal.
        if (state == SERVICE_START_PENDING || state == SERVICE_STOP_PENDING)
        {
            s_status.dwCheckPoint++;
        }
        else
        {
            s_status.dwCheckPoint = 0;
        }

        if (s_statusHandle != IntPtr.Zero)
        {
            SetServiceStatus(s_statusHandle, ref s_status);
        }
    }
}
#endif
