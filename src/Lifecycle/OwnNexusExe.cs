using System;
using System.Collections.Generic;
using Nexus.Service.Conflicts;
#if WINDOWS
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
#endif

namespace Nexus.Service.Lifecycle;

/// <summary>Tells our Nexus.exe apart from other products' executables of the same name by full path.</summary>
internal static partial class OwnNexusExe
{
    /// <summary>True when <paramref name="exePath"/> is one of <paramref name="ownExes"/>.</summary>
    public static bool IsOwn(string? exePath, IEnumerable<string?> ownExes)
    {
        var path = Normalize(exePath);
        if (path.Length == 0) return false;
        foreach (var own in ownExes)
        {
            if (string.Equals(path, Normalize(own), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>True only for an image positively read as another file; an unreadable path counts as ours, so a filtered kill never spares a process a by-name kill would take.</summary>
    public static bool IsForeignImage(string? imagePath, IEnumerable<string?> ownExes)
        => Normalize(imagePath).Length > 0 && !IsOwn(imagePath, ownExes);

    /// <summary>True when a Run command line launches one of <paramref name="ownExes"/>.</summary>
    public static bool IsOwnCommand(string? command, IEnumerable<string?> ownExes)
        => !string.IsNullOrWhiteSpace(command) && IsOwn(ConflictAutostart.ExecutablePath(command), ownExes);

    private static string Normalize(string? path)
        => path is null ? "" : path.Trim().Trim('"').Replace('/', '\\');

#if WINDOWS
    private const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\" + WindowsServiceInstaller.ServiceName;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>Our exe paths as seen from this process: its own image, the registered service binary, the default install location, plus <paramref name="extra"/>.</summary>
    [SupportedOSPlatform("windows")]
    public static List<string?> Known(params string?[] extra)
    {
        var known = new List<string?>(extra)
        {
            Environment.ProcessPath,
            RegisteredServiceExe(),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                WindowsServiceInstaller.InstallDirName,
                WindowsServiceInstaller.BinaryName),
        };
        return known;
    }

    /// <summary>The exe NexusService is registered to run, or null when the service is not installed.</summary>
    [SupportedOSPlatform("windows")]
    public static string? RegisteredServiceExe()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKey);
            return key?.GetValue("ImagePath") is string imagePath ? ConflictAutostart.ExecutablePath(imagePath) : null;
        }
        catch { return null; }
    }

    /// <summary>Full image path of <paramref name="pid"/>, or null; the limited-query right reaches other sessions and integrity levels, where Process.MainModule throws.</summary>
    [SupportedOSPlatform("windows")]
    public static unsafe string? ImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == 0) return null;
        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024u;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>True when <paramref name="exePath"/> sits in a folder holding our payload, which catches our install reached through a junction, subst drive or 8.3 path.</summary>
    [SupportedOSPlatform("windows")]
    public static bool HasOurPayload(string? exePath)
    {
        var dir = string.IsNullOrEmpty(exePath) ? null : Path.GetDirectoryName(exePath);
        return dir is not null && File.Exists(Path.Combine(dir, "overlay", "nexus-overlay.exe"));
    }

    /// <summary>True only when <paramref name="pid"/> positively runs another product's file.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsForeignProcess(int pid, IReadOnlyCollection<string?> ownExes)
    {
        var image = ImagePath(pid);
        return IsForeignImage(image, ownExes) && !HasOurPayload(image);
    }

    /// <summary>Running Nexus.exe processes other than this one, minus any <see cref="IsForeignProcess"/>. The caller disposes them.</summary>
    [SupportedOSPlatform("windows")]
    public static List<Process> Siblings(IReadOnlyCollection<string?> ownExes)
    {
        var siblings = new List<Process>();
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(WindowsServiceInstaller.BinaryName)))
        {
            if (p.Id == Environment.ProcessId || IsForeignProcess(p.Id, ownExes))
            {
                p.Dispose();
                continue;
            }
            siblings.Add(p);
        }
        return siblings;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(nint process, uint flags, char* exeName, ref uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
#endif
}
