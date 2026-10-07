using System;
using System.Runtime.InteropServices;

namespace Nexus.Service.Platform.Mac;

/// <summary>
/// Locks the screen to the login window the way the menu bar's Lock Screen
/// does. <c>pmset displaysleepnow</c> only sleeps the display and, with no
/// "require password after sleep" delay set, leaves the session open. The call
/// lives in the private login.framework, which a plain DllImport reaches (the
/// framework ships in the dyld shared cache), so it needs no reflection and
/// stays AOT-safe.
/// </summary>
internal static class MacScreenLock
{
    /// <summary>True when the call was made; false when the private symbol is not there on this macOS.</summary>
    public static bool Lock()
    {
        try
        {
            SACLockScreenImmediate();
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            ServiceLog.Info($"[mac-lock] SACLockScreenImmediate unavailable: {ex.GetType().Name}");
            return false;
        }
    }

    [DllImport("/System/Library/PrivateFrameworks/login.framework/login")]
    private static extern void SACLockScreenImmediate();
}
