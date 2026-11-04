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
/// LocalAppData when that file is not writable. Lines use <see cref="LogLineFormat"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class HelperLog
{
    public static void Write(string message) => Append(LogLineFormat.Info, message);

    public static void Warn(string message) => Append(LogLineFormat.Warn, message);

    private static void Append(string level, string message)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(LogLineFormat.Line(DateTime.UtcNow, level, "helper", message, Environment.ProcessId, newline: true));
            ServiceLog.AppendUserSessionLog("nexus-helper.log", bytes);
        }
        catch { }
    }
}
#endif
