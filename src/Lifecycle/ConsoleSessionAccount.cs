using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Nexus.Service.Lifecycle;

/// <summary>
/// The console session's SID, LSA account name and WTS user name, all read for
/// one session id. The token needs LocalSystem's SeTcbPrivilege; null without it.
/// </summary>
internal static class ConsoleSessionAccount
{
    [SupportedOSPlatform("windows")]
    internal static (string Sid, string? Name, string User)? TryResolve()
    {
        try
        {
            var sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF || !WTSQueryUserToken(sessionId, out var raw) || raw == IntPtr.Zero)
            {
                return null;
            }
            using var token = new SafeAccessTokenHandle(raw);
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            if (identity.User is not { } sid)
            {
                return null;
            }
            string? name;
            try { name = identity.Name; }
            catch { name = null; }
            return (sid.Value, name, SessionUserName(sessionId));
        }
        catch
        {
            return null;
        }
    }

    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    /// <summary>The domain (the PC's own name for a local account) of the console session's user; empty when unavailable.</summary>
    [SupportedOSPlatform("windows")]
    internal static string ConsoleDomain()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        return sessionId == 0xFFFFFFFF ? string.Empty : SessionString(sessionId, WtsDomainName);
    }

    private static string SessionUserName(uint sessionId) => SessionString(sessionId, WtsUserName);

    private static string SessionString(uint sessionId, int infoClass)
    {
        var buf = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out buf, out _) || buf == IntPtr.Zero)
            {
                return string.Empty;
            }
            return Marshal.PtrToStringUni(buf) ?? string.Empty;
        }
        finally
        {
            if (buf != IntPtr.Zero) WTSFreeMemory(buf);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, uint sessionId, int infoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);
}
