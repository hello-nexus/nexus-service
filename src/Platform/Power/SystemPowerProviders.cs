using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Platform.Power;

/// <summary>
/// Cross-platform session/power control for deck buttons. One logical action
/// per method with a per-OS backend; returns false when the action could not be
/// performed.
/// </summary>
public interface ISystemPowerProvider
{
    bool Lock();
    bool Sleep();
    bool Shutdown();
    bool Restart();
    bool Logout();
}

/// <summary>Windows: LockWorkStation / SetSuspendState P/Invoke + shutdown.exe.</summary>
public sealed class WindowsSystemPowerProvider : ISystemPowerProvider
{
    public bool Lock()
    {
        if (!OperatingSystem.IsWindows()) return false;
#if WINDOWS
        // The service runs as LocalSystem in Session 0, where LockWorkStation
        // no-ops (no interactive desktop). Run it as the active console user via
        // a one-shot scheduled task - the same mechanism the user-session helper
        // uses. Fall back to a direct call (works if ever run interactively).
        if (Nexus.Service.Lifecycle.UserHelperBootstrapper.RunInUserSession(
                "rundll32.exe user32.dll,LockWorkStation", "lock", "NexusLock"))
        {
            return true;
        }
#endif
        return LockWorkStation();
    }
    public bool Sleep() => OperatingSystem.IsWindows() && SetSuspendState(false, false, false);
    public bool Shutdown() => Run("/s", "/t", "0");
    public bool Restart() => Run("/r", "/t", "0");
    public bool Logout() => Run("/l");

    private static bool Run(params string[] args)
        => OperatingSystem.IsWindows() && ShellExecutor.RunExit("shutdown", 4000, args) == 0;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.I1)] bool hibernate,
        [MarshalAs(UnmanagedType.I1)] bool forceCritical,
        [MarshalAs(UnmanagedType.I1)] bool disableWakeEvent);
}

/// <summary>macOS: pmset + System Events osascript (no sudo).</summary>
public sealed class MacSystemPowerProvider : ISystemPowerProvider
{
    public bool Lock()
    {
#if MACOS
        return OperatingSystem.IsMacOS() && Nexus.Service.Platform.Mac.MacScreenLock.Lock();
#else
        return false;
#endif
    }
    public bool Sleep() => OperatingSystem.IsMacOS() && ShellExecutor.RunExit("/usr/bin/pmset", 2000, "sleepnow") == 0;
    public bool Shutdown() => Osa("shut down");
    public bool Restart() => Osa("restart");
    public bool Logout() => Osa("log out");

    private static bool Osa(string verb)
        => OperatingSystem.IsMacOS() && ShellExecutor.RunExit("osascript", 4000, "-e", $"tell application \"System Events\" to {verb}") == 0;
}

/// <summary>Linux: loginctl / systemctl (best-effort; depends on systemd + polkit).</summary>
public sealed class LinuxSystemPowerProvider : ISystemPowerProvider
{
    public bool Lock() => OperatingSystem.IsLinux() && ShellExecutor.RunExit("loginctl", 3000, "lock-session") == 0;
    public bool Sleep() => OperatingSystem.IsLinux() && ShellExecutor.RunExit("systemctl", 3000, "suspend") == 0;
    public bool Shutdown() => OperatingSystem.IsLinux() && ShellExecutor.RunExit("systemctl", 3000, "poweroff") == 0;
    public bool Restart() => OperatingSystem.IsLinux() && ShellExecutor.RunExit("systemctl", 3000, "reboot") == 0;
    public bool Logout() => OperatingSystem.IsLinux() && ShellExecutor.RunExit("loginctl", 3000, "terminate-user", Environment.UserName) == 0;
}

public sealed class StubSystemPowerProvider : ISystemPowerProvider
{
    public bool Lock() => false;
    public bool Sleep() => false;
    public bool Shutdown() => false;
    public bool Restart() => false;
    public bool Logout() => false;
}
