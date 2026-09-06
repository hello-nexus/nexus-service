using System;
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
    private static readonly object Gate = new();
    private static uint? _cookie;

    public static async Task AcquireAsync(DBusConnection dbus)
    {
        lock (Gate)
        {
            if (_cookie is not null) return;
            _cookie = 0; // claimed; the real cookie lands below
        }
        try
        {
            try { await dbus.StartAsync(); } catch { }
            var reply = await dbus.CallAsync("org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver",
                "org.freedesktop.ScreenSaver", "Inhibit", "ss",
                w => { w.WriteString("Nexus"); w.WriteString("Y70 panel is showing"); });
            var cookie = new DBusReader(reply.Body).ReadUInt32();
            lock (Gate) _cookie = cookie;
            Console.Error.WriteLine($"[panel-kiosk] screen blanking inhibited (cookie {cookie})");
        }
        catch (Exception ex)
        {
            lock (Gate) _cookie = null;
            Console.Error.WriteLine($"[panel-kiosk] screen inhibit unavailable: {ex.Message}");
        }
    }

    public static async Task ReleaseAsync(DBusConnection dbus)
    {
        uint cookie;
        lock (Gate)
        {
            if (_cookie is not { } c || c == 0) { _cookie = null; return; }
            cookie = c;
            _cookie = null;
        }
        try
        {
            await dbus.CallAsync("org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver",
                "org.freedesktop.ScreenSaver", "UnInhibit", "u", w => w.WriteUInt32(cookie));
            Console.Error.WriteLine("[panel-kiosk] screen blanking inhibit released");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[panel-kiosk] screen inhibit release failed: {ex.Message}");
        }
    }
}
