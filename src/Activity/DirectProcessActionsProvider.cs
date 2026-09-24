using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Nexus.Service.Activity;

/// <summary>
/// IProcessActionsProvider for macOS and Linux, where the service already
/// runs as the console user (LaunchAgent / user session), so a privilege hop
/// through a helper is unnecessary - actions run directly.
/// </summary>
public sealed class DirectProcessActionsProvider : IProcessActionsProvider
{
    public bool IsAvailable => true;

    public Task<(int Killed, int Failed)> KillAsync(string processName)
        => Task.FromResult(ProcessKiller.KillAllCounted(processName));

    public Task<bool> OpenLocationAsync(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return Task.FromResult(false);
        }

        try
        {
            var psi = new ProcessStartInfo { UseShellExecute = false };
            if (OperatingSystem.IsMacOS())
            {
                // "open -R" reveals the file in Finder with it selected,
                // the mac equivalent of explorer.exe /select,.
                psi.FileName = "open";
                psi.ArgumentList.Add("-R");
                psi.ArgumentList.Add(exePath);
            }
            else
            {
                // xdg-open has no "select" concept; opening the containing
                // folder is the closest a generic Linux desktop supports.
                psi.FileName = "xdg-open";
                psi.ArgumentList.Add(Path.GetDirectoryName(exePath) ?? exePath);
            }
            Process.Start(psi);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>macOS/Linux never call this - RecentAppsActivator launches by
    /// shortcut id instead, and "open -a" already activates a running app.</summary>
    public Task<bool> ActivateWindowAsync(int pid) => Task.FromResult(false);
}
