using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Platform.Linux.DBus;

namespace Nexus.Service.Platform.Linux;

/// <summary>
/// Keeps the desktop from blanking its outputs while the Y70 kiosk is up. The
/// panel is a display that has to stay lit, and the compositor's idle policy
/// (Plasma turns every output off after ten idle minutes) does not know that:
/// the kiosk window, its record and its page were all correct on a Y70 that
/// showed nothing. org.freedesktop.ScreenSaver.Inhibit is the freedesktop
/// contract KDE and GNOME both honour; the inhibition dies with the bus
/// connection, so a crashed daemon never leaves it behind.
/// </summary>
internal static class LinuxScreenInhibit
{
    private static readonly TimeSpan RetryMin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryMax = TimeSpan.FromSeconds(30);

    private static readonly object Gate = new();
    // Non-null from Acquire until Release; cancelled by Release to stop the retry loop.
    private static CancellationTokenSource? _wanted;
    private static uint? _cookie;

    /// <summary>
    /// At cold boot the provider (the compositor's idle daemon) registers on the
    /// session bus after the kiosk spawns, so the first call fails with ServiceUnknown.
    /// Retries with a capped backoff until it lands or <see cref="ReleaseAsync"/> runs.
    /// </summary>
    public static async Task AcquireAsync(DBusConnection dbus)
    {
        CancellationToken ct;
        lock (Gate)
        {
            if (_wanted is not null) return;
            _wanted = new CancellationTokenSource();
            ct = _wanted.Token;
        }
        var delay = RetryMin;
        var logged = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                try { await dbus.StartAsync(); } catch { }
                var reply = await dbus.CallAsync("org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver",
                    "org.freedesktop.ScreenSaver", "Inhibit", "ss",
                    w => { w.WriteString("Nexus"); w.WriteString("Y70 panel is showing"); });
                var cookie = new DBusReader(reply.Body).ReadUInt32();
                bool stale;
                lock (Gate)
                {
                    stale = ct.IsCancellationRequested;
                    if (!stale) _cookie = cookie;
                }
                if (stale)
                {
                    await UnInhibitAsync(dbus, cookie);
                    return;
                }
                Console.Error.WriteLine($"[panel-kiosk] screen blanking inhibited (cookie {cookie})");
                return;
            }
            catch (Exception ex)
            {
                if (!logged)
                {
                    logged = true;
                    Console.Error.WriteLine($"[panel-kiosk] screen inhibit unavailable, retrying: {ex.Message}");
                }
            }
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }
            delay = NextDelay(delay);
        }
    }

    internal static TimeSpan NextDelay(TimeSpan current)
        => TimeSpan.FromTicks(Math.Min(current.Ticks * 2, RetryMax.Ticks));

    public static async Task ReleaseAsync(DBusConnection dbus)
    {
        uint? cookie;
        CancellationTokenSource? wanted;
        lock (Gate)
        {
            wanted = _wanted;
            _wanted = null;
            cookie = _cookie;
            _cookie = null;
        }
        try { wanted?.Cancel(); } catch { }
        if (cookie is { } c)
        {
            if (await UnInhibitAsync(dbus, c))
                Console.Error.WriteLine("[panel-kiosk] screen blanking inhibit released");
        }
    }

    private static async Task<bool> UnInhibitAsync(DBusConnection dbus, uint cookie)
    {
        try
        {
            await dbus.CallAsync("org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver",
                "org.freedesktop.ScreenSaver", "UnInhibit", "u", w => w.WriteUInt32(cookie));
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel-kiosk] screen inhibit release failed: {ex.Message}");
            return false;
        }
    }
}
