using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Real macOS autostart via launchd. Writes a plist to ~/Library/LaunchAgents/
/// and loads/unloads via launchctl. The plist runs the service binary at user
/// login with KeepAlive=true and RunAtLoad=true.
/// </summary>
public sealed class MacStartupProvider : IStartupProvider
{
    // internal so FactoryReset can drive the same launchd agent on restart.
    internal const string PlistLabel = "com.hellonexus.panel.service";

    internal static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", $"{PlistLabel}.plist");

    public bool IsEnabled()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return false;
        }

        return File.Exists(PlistPath);
    }

    public bool SetEnabled(bool enabled, string path, string arguments)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return false;
        }

        try
        {
            if (enabled)
            {
                return WritePlistAndLoad(path, arguments);
            }
            else
            {
                return UnloadAndDelete();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mac-startup] failed: {ex.Message}");
            return false;
        }
    }

    private bool WritePlistAndLoad(string execPath, string arguments)
    {
        var dir = Path.GetDirectoryName(PlistPath)!;
        Directory.CreateDirectory(dir);

        // Build argument array for the plist ProgramArguments key.
        var args = string.IsNullOrWhiteSpace(arguments)
            ? $"<string>{Escape(execPath)}</string>"
            : $"<string>{Escape(execPath)}</string>\n        <string>{Escape(arguments)}</string>";

        var plist = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>{PlistLabel}</string>
    <key>ProgramArguments</key>
    <array>
        {args}
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>StandardOutPath</key>
    <string>{Diagnostics.SupportBundleExtras.LaunchdStdoutPath}</string>
    <key>StandardErrorPath</key>
    <string>{Diagnostics.SupportBundleExtras.LaunchdStderrPath}</string>
</dict>
</plist>";

        File.WriteAllText(PlistPath, plist);

        // Unload first (in case a stale version was loaded), then load.
        ShellOut("/bin/launchctl", "unload", "-w", PlistPath);
        var exitCode = ShellOut("/bin/launchctl", "load", "-w", PlistPath);
        return exitCode == 0;
    }

    private bool UnloadAndDelete()
    {
        if (!File.Exists(PlistPath))
        {
            return true;
        }

        ShellOut("/bin/launchctl", "unload", "-w", PlistPath);
        File.Delete(PlistPath);
        return true;
    }

    private static int ShellOut(string fileName, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return -1;
            }

            proc.WaitForExit(5000);
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
