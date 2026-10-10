#if LINUX
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Lighting.IdleDim;
using Nexus.Service.Platform.Linux.DBus;

namespace Nexus.Service.Platform.Linux;

/// <summary>KDE: the freedesktop ScreenSaver service reports session idle time in milliseconds.</summary>
internal sealed class KdeIdleTimeSource : IIdleTimeSource
{
    private readonly DBusConnection _dbus;
    public KdeIdleTimeSource(DBusConnection dbus) => _dbus = dbus;
    public string Name => "kde-screensaver";

    public async Task<long?> TryGetIdleMsAsync()
    {
        var reply = await _dbus.CallAsync("org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver",
            "org.freedesktop.ScreenSaver", "GetSessionIdleTime", "", null);
        return new DBusReader(reply.Body).ReadUInt32();
    }
}

/// <summary>GNOME: Mutter's idle monitor reports idle time in milliseconds.</summary>
internal sealed class MutterIdleTimeSource : IIdleTimeSource
{
    private readonly DBusConnection _dbus;
    public MutterIdleTimeSource(DBusConnection dbus) => _dbus = dbus;
    public string Name => "gnome-mutter";

    public async Task<long?> TryGetIdleMsAsync()
    {
        var reply = await _dbus.CallAsync("org.gnome.Mutter.IdleMonitor", "/org/gnome/Mutter/IdleMonitor/Core",
            "org.gnome.Mutter.IdleMonitor", "GetIdletime", "", null);
        return (long)new DBusReader(reply.Body).ReadUInt64();
    }
}

/// <summary>
/// X11 fallback through the MIT-SCREEN-SAVER extension (libXss). Only offered
/// on an X11 session; the libraries are loaded lazily, so a machine without
/// them simply does not answer. The display is opened and closed per query: a
/// held connection to an X server that later goes away makes Xlib's default
/// IO error handler exit the whole process, and at one query a second the
/// open costs nothing.
/// </summary>
internal sealed class X11IdleTimeSource : IIdleTimeSource
{
    public string Name => "x11-screensaver";

    public Task<long?> TryGetIdleMsAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "x11", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return Task.FromResult<long?>(null);
        }
        var display = IntPtr.Zero;
        var info = IntPtr.Zero;
        try
        {
            display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero)
            {
                return Task.FromResult<long?>(null);
            }
            info = XScreenSaverAllocInfo();
            if (info == IntPtr.Zero || XScreenSaverQueryInfo(display, XDefaultRootWindow(display), info) == 0)
            {
                return Task.FromResult<long?>(null);
            }
            // XScreenSaverInfo: window, state, kind, til_or_since, idle (unsigned long, ms).
            return Task.FromResult<long?>(Marshal.ReadIntPtr(info, IdleOffset).ToInt64());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Task.FromResult<long?>(null);
        }
        finally
        {
            try
            {
                if (info != IntPtr.Zero) XFree(info);
                if (display != IntPtr.Zero) XCloseDisplay(display);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        }
    }

    // Window, state, kind, til_or_since: the idle field follows them.
    private static readonly int IdleOffset = IntPtr.Size + 4 + 4 + IntPtr.Size;

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XFree(IntPtr data);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libXss.so.1")]
    private static extern IntPtr XScreenSaverAllocInfo();

    [DllImport("libXss.so.1")]
    private static extern int XScreenSaverQueryInfo(IntPtr display, IntPtr drawable, IntPtr info);
}

/// <summary>
/// Linux source for idle dim: polls session idle time once a second, only while
/// a fixed timeout is armed. It reads the desktop's idle counter and nothing
/// else; there is no /dev/input, evdev or other device read, so nothing here
/// can see which key was pressed. No display-off source exists on Linux.
/// </summary>
internal sealed class LinuxIdleDimWatch : IIdleDimWatch, IHostedService, IDisposable
{
    private const int PollPeriodMs = 1000;

    private readonly IdleDimController _controller;
    private readonly IdleTimeSourceSelector _selector;
    private readonly DBusConnection? _dbus;
    private readonly object _gate = new();
    // The selector and the X11 source are not thread-safe: every access takes this.
    private readonly SemaphoreSlim _selectorGate = new(1, 1);
    private Timer? _timer;
    private int _threshold;
    private bool? _reported;
    private int _polling;
    private readonly ProbeThrottle _reprobeThrottle;

    public LinuxIdleDimWatch(IdleDimController controller, DBusConnection dbus)
        : this(controller, new IdleTimeSourceSelector(new IIdleTimeSource[]
        {
            new KdeIdleTimeSource(dbus),
            new MutterIdleTimeSource(dbus),
            new X11IdleTimeSource(),
        }), dbus)
    {
    }

    internal LinuxIdleDimWatch(IdleDimController controller, IdleTimeSourceSelector selector, DBusConnection? dbus = null, Func<long>? nowMs = null)
    {
        _controller = controller;
        _selector = selector;
        _dbus = dbus;
        _reprobeThrottle = new ProbeThrottle(TimeSpan.FromSeconds(30), nowMs);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Probe before binding, so the first poll cannot race it. Stamps the throttle.
        _reprobeThrottle.TryBegin();
        await ProbeAsync();
        _controller.Reprobe = () => _ = ReprobeAsync();
        _controller.Watch = this;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    // Lets GET /lighting/idle-dim recover from a probe that ran before the bus
    // or the login was up, at most once per interval.
    internal async Task ReprobeAsync()
    {
        if (_reprobeThrottle.TryBegin())
        {
            await ProbeAsync();
        }
    }

    private async Task ProbeAsync()
    {
        try
        {
            await EnsureBusAsync();
            long? ms;
            await _selectorGate.WaitAsync();
            try { ms = await _selector.GetIdleMsAsync(); }
            finally { _selectorGate.Release(); }
            _controller.InputSourceAvailable = ms is not null;
            Console.Error.WriteLine(ms is null
                ? "[idle-dim] no idle-time source answered; idle dim unsupported"
                : $"[idle-dim] idle-time source: {_selector.ActiveName}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[idle-dim] idle-time probe failed: {ex.Message}");
        }
    }

    // Idempotent: reconnects a bus that dropped (logout/login, a KDE restart).
    private async Task EnsureBusAsync()
    {
        if (_dbus is null)
        {
            return;
        }
        try { await _dbus.StartAsync(); }
        catch { /* the sources that need the bus simply will not answer */ }
    }

    public void SetInputWatch(int thresholdSeconds)
    {
        lock (_gate)
        {
            _threshold = thresholdSeconds;
            _reported = null;
            if (thresholdSeconds <= 0)
            {
                _timer?.Dispose();
                _timer = null;
                return;
            }
            _timer ??= new Timer(_ => _ = PollAsync(), null, 0, PollPeriodMs);
        }
    }

    // No display-off source on Linux.
    public void SetDisplayWatch(bool armed) { }

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) != 0)
        {
            return;
        }
        try
        {
            int threshold;
            lock (_gate)
            {
                threshold = _threshold;
            }
            if (threshold <= 0)
            {
                return;
            }
            await EnsureBusAsync();
            long? ms;
            await _selectorGate.WaitAsync();
            try { ms = await _selector.GetIdleMsAsync(); }
            finally { _selectorGate.Release(); }
            _controller.InputSourceAvailable = ms is not null;
            // A source that stops answering must not hold a dim: report active.
            var idle = ms is { } v && v / 1000 >= threshold;
            lock (_gate)
            {
                if (_threshold != threshold || _reported == idle)
                {
                    return;
                }
                _reported = idle;
            }
            _controller.OnInputIdle(idle);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[idle-dim] poll failed: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _threshold = 0;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
#endif
