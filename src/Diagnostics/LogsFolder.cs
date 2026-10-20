using System;
using System.Diagnostics;
using System.IO;
using Nexus.Service.Persistence;

namespace Nexus.Service.Diagnostics;

/// <summary>
/// Opens the Nexus data folder (<see cref="NexusDataPaths.NexusRoot"/>: settings.json,
/// logs/, updates/, db/ on Windows) in the OS file manager from macOS/Linux, where
/// the service already runs in the user session. Windows opens it through the
/// user-session helper (<c>dialog.openFolder</c>) instead.
/// </summary>
internal static class LogsFolder
{
    public static void Open()
    {
        var dir = NexusDataPaths.NexusRoot();
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo { UseShellExecute = true };
        if (OperatingSystem.IsWindows())
        { psi.FileName = "explorer.exe"; psi.Arguments = $"\"{dir}\""; }
        else if (OperatingSystem.IsMacOS())
        { psi.FileName = "open"; psi.Arguments = $"\"{dir}\""; psi.UseShellExecute = false; }
        else
        {
            psi.UseShellExecute = false;
#if LINUX
            // Root daemon: xdg-open must run in the session user's context, not root's.
            var (file, args) = Nexus.Service.Platform.Linux.LinuxSession.WrapSpawnAsSessionUser(
                "xdg-open", new List<string> { dir });
            psi.FileName = file;
            foreach (var a in args)
                psi.ArgumentList.Add(a);
#else
            psi.FileName = "xdg-open";
            psi.Arguments = $"\"{dir}\"";
#endif
        }
        Process.Start(psi);
    }
}
