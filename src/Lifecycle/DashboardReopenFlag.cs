using System;
using System.Diagnostics;
using System.IO;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// Windows-only signal file at <c>%ProgramData%\Nexus\reopen-dashboard.flag</c>
/// telling the next <c>Nexus.exe --helper</c> start to reopen the dashboard
/// window. Written by any LocalSystem-side path that needs the dashboard back
/// after the helper (and, for a full factory reset, the overlay) restarts -
/// the OTA self-update install and factory reset both use it, so the read
/// side (<see cref="WindowsUserHelper"/>) has one signal to check regardless
/// of cause. Compiled cross-platform (matches the pre-existing pattern in
/// this file's former home, <c>UpdateService.cs</c>) but only ever written
/// from Windows-gated call sites; <c>icacls.exe</c> is a caught no-op off
/// Windows.
/// </summary>
internal static class DashboardReopenFlag
{
    public static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexus",
        "reopen-dashboard.flag");

    /// <summary>The route the flag carries, or null when it has none, it is stale or invalid. Does not delete the flag.</summary>
    public static string? ReadRoute(string? path = null)
    {
        path ??= Path;
        try
        {
            return ReopenRoute.Parse(File.ReadAllText(path), File.GetLastWriteTimeUtc(path), DateTime.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    /// <param name="route">Validated dashboard route for the reopened window; empty opens the default page.</param>
    public static void Write(string? route = null, string? path = null)
    {
        path ??= Path;
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, ReopenRoute.Validate(route) ?? "");
            // Grant BUILTIN\Users (S-1-5-32-545) Modify so the user-session
            // helper can delete this LocalSystem-written flag after reopening.
            // Without it the delete fails and the dashboard reopens on every
            // later helper start.
            var psi = new ProcessStartInfo("icacls.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("/grant");
            psi.ArgumentList.Add("*S-1-5-32-545:(M)");
            using var icacls = Process.Start(psi);
            icacls?.WaitForExit(5000);
        }
        catch { /* best-effort */ }
    }
}
