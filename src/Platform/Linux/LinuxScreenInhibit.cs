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
    public static Task AcquireAsync(DBusConnection dbus)
        => AcquireAsync(() => InhibitCallAsync(dbus), c => UnInhibitAsync(dbus, c), RetryMin);

    private static async Task<uint> InhibitCallAsync(DBusConnection dbus)
    {
        try { await dbus.StartAsync(); } catch { }
        var reply = await dbus.CallAsync("org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver",
            "org.freedesktop.ScreenSaver", "Inhibit", "ss",
            w => { w.WriteString("Nexus"); w.WriteString("Y70 panel is showing"); });
        return new DBusReader(reply.Body).ReadUInt32();
    }

    internal static async Task AcquireAsync(Func<Task<uint>> inhibit, Func<uint, Task<bool>> unInhibit, TimeSpan firstDelay)
    {
        CancellationTokenSource mine;
        lock (Gate)
        {
            if (_wanted is not null) return;
            mine = _wanted = new CancellationTokenSource();
        }
        var ct = mine.Token;
        var delay = firstDelay;
        var logged = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cookie = await inhibit();
                bool stale;
                // Identity, not the token: Release clears _wanted under this same lock, so a
                // reply that lands after it can never store a cookie nobody will release.
                lock (Gate)
                {
                    stale = !ReferenceEquals(_wanted, mine);
                    if (!stale) _cookie = cookie;
                }
                if (stale)
                {
                    await unInhibit(cookie);
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

    public static Task ReleaseAsync(DBusConnection dbus)
        => ReleaseAsync(c => UnInhibitAsync(dbus, c));

    internal static async Task ReleaseAsync(Func<uint, Task<bool>> unInhibit)
    {
        uint? cookie;
        lock (Gate)
        {
            try { _wanted?.Cancel(); } catch { }
            _wanted = null;
            cookie = _cookie;
            _cookie = null;
        }
        if (cookie is { } c)
        {
            if (await unInhibit(c))
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
