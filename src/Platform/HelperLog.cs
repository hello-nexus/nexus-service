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
/// the user session owns the file it creates, and falls back to its own
/// LocalAppData when that file is not writable.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class HelperLog
{
    public static void Write(string message)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes($"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {message}\n");
            ServiceLog.AppendUserSessionLog("nexus-helper.log", bytes);
        }
        catch { }
    }
}
#endif
