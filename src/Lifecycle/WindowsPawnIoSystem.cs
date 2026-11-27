using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform;

namespace Nexus.Service.Lifecycle;

/// <summary>Windows implementation of <see cref="IPawnIoSystem"/>; every member is inert elsewhere.</summary>
public sealed class WindowsPawnIoSystem : IPawnIoSystem
{
    private const string ServiceName = "PawnIO";
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\PawnIO";

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceStart = 0x0010;
    private const int ErrorServiceAlreadyRunning = 1056;

    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public bool DeviceOpen() => PawnIoInstaller.IsDeviceAvailable();

    public bool ServiceRegistered()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(ServiceKey);
            return key is not null;
        }
        catch { return false; }
    }

    public bool ServiceMarkedForDeletion()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(ServiceKey);
            return key?.GetValue("DeleteFlag") is not null;
        }
        catch { return false; }
    }

    public int StartService()
    {
        if (!OperatingSystem.IsWindows()) return -1;
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return Marshal.GetLastWin32Error();
        try
        {
            var svc = OpenService(scm, ServiceName, ServiceStart);
            if (svc == IntPtr.Zero) return Marshal.GetLastWin32Error();
            try
            {
                if (StartServiceW(svc, 0, IntPtr.Zero)) return 0;
                var err = Marshal.GetLastWin32Error();
                return err == ErrorServiceAlreadyRunning ? 0 : err;
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    public int? DevnodeProblemCode()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            foreach (var d in Platform.Windows.WindowsDevnodes.EnumeratePresent(enumerator: "ROOT"))
            {
                if (d.Name.Contains("PawnIO", StringComparison.OrdinalIgnoreCase))
                {
                    return d.ProblemCode == 0 ? null : (int)d.ProblemCode;
                }
            }
            return null;
        }
        catch { return null; }
    }

    public Task<PawnIoInstallResult> EnsureInstalledAsync(CancellationToken ct) => PawnIoInstaller.EnsureInstalledAsync(ct);

    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    public void RestartPc()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[pawnio] restart request failed: {ex.Message}");
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr service, uint argc, IntPtr argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
