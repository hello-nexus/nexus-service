using System.Diagnostics;

namespace Nexus.Service.Tests.Lifecycle;

/// <summary>Creates a real directory link: an NTFS junction on Windows (no privilege needed), a symlink elsewhere.</summary>
internal static class TestLinks
{
    internal static void CreateDirLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new IOException($"mklink /J failed: {p.StandardError.ReadToEnd()}");
        }
    }
}
