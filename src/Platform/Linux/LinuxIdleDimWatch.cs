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
/// them simply does not answer.
/// </summary>
internal sealed class X11IdleTimeSource : IIdleTimeSource
{
    private IntPtr _display;
    private IntPtr _info;
    public string Name => "x11-screensaver";

    public Task<long?> TryGetIdleMsAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "x11", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            return Task.FromResult<long?>(null);
        }
        try
        {
            if (_display == IntPtr.Zero)
            {
                _display = XOpenDisplay(IntPtr.Zero);
                if (_display == IntPtr.Zero)
                {
                    return Task.FromResult<long?>(null);
                }
                _info = XScreenSaverAllocInfo();
            }
            if (_info == IntPtr.Zero || XScreenSaverQueryInfo(_display, XDefaultRootWindow(_display), _info) == 0)
            {
                return Task.FromResult<long?>(null);
            }
            // XScreenSaverInfo: window, state, kind, til_or_since, idle (unsigned long, ms).
            return Task.FromResult<long?>(Marshal.ReadIntPtr(_info, AlignedIdleOffset()).ToInt64());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Task.FromResult<long?>(null);
        }
    }

    // Window (8) + state (4) + kind (4) + til_or_since (8) puts idle at 24 on LP64.
    private static int AlignedIdleOffset() => IntPtr.Size + 4 + 4 + IntPtr.Size;

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr name);

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
/// can see which key was pressed. No display-off source exists on Linux, so
/// "when my screen turns off" never engages.
/// </summary>
internal sealed class LinuxIdleDimWatch : IIdleDimWatch, IHostedService, IDisposable
{
    private const int PollPeriodMs = 1000;

    private readonly IdleDimController _controller;
    private readonly IdleTimeSourceSelector _selector;
    private readonly object _gate = new();
    private Timer? _timer;
    private int _threshold;
    private bool? _reported;
    private int _polling;

    public LinuxIdleDimWatch(IdleDimController controller, DBusConnection dbus)
        : this(controller, new IdleTimeSourceSelector(new IIdleTimeSource[]
        {
            new KdeIdleTimeSource(dbus),
            new MutterIdleTimeSource(dbus),
            new X11IdleTimeSource(),
        }))
    {
    }

    internal LinuxIdleDimWatch(IdleDimController controller, IdleTimeSourceSelector selector)
    {
        _controller = controller;
        _selector = selector;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _controller.Watch = this;
        // One probe so GET /lighting/idle-dim can say whether the fixed times work here.
        try
        {
            var ms = await _selector.GetIdleMsAsync();
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

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
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
            var ms = await _selector.GetIdleMsAsync();
            if (ms is null)
            {
                _controller.InputSourceAvailable = false;
                return;
            }
            _controller.InputSourceAvailable = true;
            var idle = ms.Value / 1000 >= threshold;
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
