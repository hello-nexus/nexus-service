using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

[assembly: SupportedOSPlatform("windows")]

namespace NexusStoreLauncher;

// Full-trust MSIX entry point (see the "Microsoft Store (MSIX)" section in
// installer/README.md). Install-if-missing, else launch; no upgrade logic -
// the installed suite's own OTA engine owns updates once the suite is present.
internal static partial class Program
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    // The packaged app is listed and installed as "Hello Nexus" ("Nexus" is
    // reserved by another publisher); the suite it installs is still "Nexus".
    private const string Caption = "Hello Nexus";
    private const int ErrorCancelled = 1223;
    private const int ExitOk = 0;
    private const int ExitFatalError = 1;
    private const int ExitUserCancelledElevation = 2;

    // Inno's uninstall key is named "<AppId>_is1"; the GUID is the [Setup] AppId in Nexus.iss.
    private const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8F2E3A4D-9C5B-4E7A-B1F8-3C2A5E9D0F12}_is1";

    // user32.dll exports only the suffixed MessageBoxA/MessageBoxW, not a bare
    // "MessageBox"; LibraryImport never probes A/W suffixes the way DllImport
    // does, so the method must be named MessageBoxW or carry an EntryPoint.
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint hWnd, string text, string caption, uint type);

    private static int Main()
    {
        try
        {
            return Run();
        }
        catch (Exception ex)
        {
            return ShowFatalError($"Nexus could not start: {ex.Message}");
        }
    }

    private static int Run()
    {
        var nexusExe = ResolveNexusExe();
        if (File.Exists(nexusExe))
        {
            return LaunchNexus(nexusExe);
        }

        var setupExe = Path.Combine(AppContext.BaseDirectory, "Nexus-Setup.exe");
        if (!File.Exists(setupExe))
        {
            return ShowFatalError(
                "The Nexus setup file is missing from this package. Reinstall Hello Nexus from the Microsoft Store, or download Nexus from hellonexus.com.");
        }

        var exitCode = RunSetup(setupExe);
        if (exitCode is null)
        {
            // The user declined the UAC prompt; a quiet decline, not a fault.
            return ExitUserCancelledElevation;
        }

        if (exitCode != ExitOk)
        {
            return ShowFatalError(
                $"Nexus installation failed (exit code {exitCode}). Try again, or download the installer from hellonexus.com.");
        }

        nexusExe = ResolveNexusExe();
        if (!File.Exists(nexusExe))
        {
            return ShowFatalError(
                "Nexus installed but Nexus.exe was not found. Open it from the Start menu, or restart your PC and try again.");
        }

        return LaunchNexus(nexusExe);
    }

    // Nexus.iss leaves DisableDirPage=no, so an attended install can land
    // anywhere; Inno always records the real path as InstallLocation under its
    // uninstall key, checked in both registry views since the install's
    // bitness view is not known ahead of time. Falls back to the Inno default
    // directory only when the key is absent (no suite installed yet).
    private static string ResolveNexusExe()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hklm.OpenSubKey(UninstallKeyPath);
            if (key?.GetValue("InstallLocation") is string installLocation && installLocation.Length > 0)
            {
                var exe = Path.Combine(installLocation, "Nexus.exe");
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Path.Combine(programFiles, "Nexus", "Nexus.exe");
    }

    // Returns null when the user declines the UAC prompt (ERROR_CANCELLED),
    // else the setup process's exit code.
    private static int? RunSetup(string setupExe)
    {
        // The setup exe carries its own elevation manifest (PrivilegesRequired=admin
        // in Nexus.iss). ShellExecute honors that manifest and raises UAC; a direct
        // CreateProcess (UseShellExecute = false) from this unelevated full-trust
        // launcher cannot elevate the child.
        // No /DESKTOPICON=1, deliberately: Nexus.iss creates one under a silent
        // install only when asked, and a Store install places nothing on the
        // desktop (see installer/README.md).
        var install = new ProcessStartInfo
        {
            FileName = setupExe,
            Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = true,
        };

        Process installProcess;
        try
        {
            installProcess = Process.Start(install) ?? throw new InvalidOperationException("Nexus setup did not start.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }

        using (installProcess)
        {
            installProcess.WaitForExit();
            return installProcess.ExitCode;
        }
    }

    private static int LaunchNexus(string nexusExe)
    {
        // The installer's own [Run] --open-app entry is skipifsilent, so a silent
        // install never opens the dashboard itself; the launcher does it here for
        // both the fresh-install and already-installed paths.
        Process.Start(new ProcessStartInfo
        {
            FileName = nexusExe,
            Arguments = "--open-app",
            UseShellExecute = false,
        });
        return ExitOk;
    }

    private static int ShowFatalError(string message)
    {
        MessageBoxW(0, message, Caption, MbOk | MbIconError);
        return ExitFatalError;
    }
}
