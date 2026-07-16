#if WINDOWS
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text;

namespace Nexus.Service.Platform;

/// <summary>
/// Appends to nexus-helper.log. The helper runs in the user session, so its
/// stdout/stderr go nowhere visible and this file is the only record of what
/// it did. Co-located with nexus-service.log under the canonical logs dir;
/// the user session owns the file it creates.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class HelperLog
{
    public static void Write(string message)
    {
        try
        {
            var dir = ServiceLog.LogsDirectory;
            Directory.CreateDirectory(dir);
            using var fs = new FileStream(
                Path.Combine(dir, "nexus-helper.log"),
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            var bytes = Encoding.UTF8.GetBytes($"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {message}\n");
            fs.Write(bytes, 0, bytes.Length);
        }
        catch { }
    }
}
#endif
