using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Peripherals.Tryx.Panorama;

/// <summary>
/// Whether the signed-in Windows user's own short time pattern is 12 hour. The service runs
/// as LocalSystem under invariant globalization, so neither CurrentCulture nor HKCU is the
/// user's; the pattern is read from the console user's hive through ConsoleUserSid. The read
/// runs off the caller's thread and the last answer is served meanwhile, so the heartbeat
/// never waits on the registry. Non-Windows, no console user or an unreadable value is 24 hour.
/// </summary>
internal static class TryxUserClockFormat
{
    private const long RefreshMs = 60_000;

    private static long _readAtMs = long.MinValue / 2;
    private static int _refreshing;
    private static volatile bool _uses12Hour;

    public static bool SystemUses12Hour()
    {
        var now = Environment.TickCount64;
        if (now - Volatile.Read(ref _readAtMs) >= RefreshMs
            && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
        {
            Task.Run(Refresh);
        }
        return _uses12Hour;
    }

    private static void Refresh()
    {
        try
        {
            _uses12Hour = TryReadShortTime() is { } pattern && ParseUses12Hour(pattern);
        }
        catch
        {
            _uses12Hour = false;
        }
        finally
        {
            Volatile.Write(ref _readAtMs, Environment.TickCount64);
            Volatile.Write(ref _refreshing, 0);
        }
    }

    /// <summary>Maps a Windows sShortTime pattern: an AM/PM marker or a lowercase hour field is
    /// 12 hour, an uppercase hour field is 24 hour, anything else is 24 hour.</summary>
    internal static bool ParseUses12Hour(string? shortTimePattern)
    {
        if (string.IsNullOrEmpty(shortTimePattern))
        {
            return false;
        }
        if (shortTimePattern.Contains('t'))
        {
            return true;
        }
        if (shortTimePattern.Contains('H'))
        {
            return false;
        }
        return shortTimePattern.Contains('h');
    }

    private static string? TryReadShortTime()
    {
#if WINDOWS
        const string International = @"Control Panel\International";
        var sid = Nexus.Service.Lifecycle.ConsoleUserSid.Resolve(International, quietIfHiveMissing: true);
        if (sid is null)
        {
            return null;
        }
        using var key = Microsoft.Win32.Registry.Users.OpenSubKey($@"{sid}\{International}");
        return key?.GetValue("sShortTime") as string;
#else
        return null;
#endif
    }
}
