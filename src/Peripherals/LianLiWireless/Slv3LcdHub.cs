using System;
using System.Collections.Generic;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LianLiWireless;

/// <summary>
/// Stateless-per-call manager for the SL-LCD Wireless screens: resolves a
/// serial to its device path via <see cref="ISlv3LcdDiscovery"/>, opens and
/// caches one <see cref="ISlv3LcdTransport"/> per serial, and re-opens it if
/// the cached transport has gone stale. Unlike <see cref="Slv3Hub"/> there is
/// no bind/keepalive state machine here; each screen is addressed directly.
/// All shared state is guarded by one lock.
/// </summary>
public sealed class Slv3LcdHub : IDisposable
{
    private readonly ISlv3LcdDiscovery _discovery;
    private readonly Func<Slv3LcdPortInfo, ISlv3LcdTransport> _transportFactory;
    private readonly object _lock = new();
    // Matches FindPort's OrdinalIgnoreCase lookup so the same physical serial
    // in a different casing cannot cache-miss into a second open transport.
    private readonly Dictionary<string, ISlv3LcdTransport> _open = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private readonly Nexus.Service.Lighting.LcdLockSleepSet? _lockSleep;

    /// <summary>After a failed write drops a screen's connection, it is reopened no sooner than this.</summary>
    private const long ReopenBackoffMs = 1000;

    private readonly Func<long> _nowMs;
    private readonly Dictionary<string, long> _reopenAfter = new(StringComparer.OrdinalIgnoreCase);
    // Screens whose last write failed, so a run of failures logs once.
    private readonly HashSet<string> _failing = new(StringComparer.OrdinalIgnoreCase);
    // Last brightness and rotation asked of each screen, replayed onto a reopened handle: the
    // content loop only re-applies them when the screen leaves discovery, not after a drop.
    private readonly Dictionary<string, byte> _brightness = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte> _rotation = new(StringComparer.OrdinalIgnoreCase);
    // Screens whose handle a failed write dropped; only their next open replays the above.
    private readonly HashSet<string> _dropped = new(StringComparer.OrdinalIgnoreCase);

    public Slv3LcdHub(
        ISlv3LcdDiscovery discovery,
        Func<Slv3LcdPortInfo, ISlv3LcdTransport> transportFactory,
        Nexus.Service.Lighting.LcdLockState? lockState = null,
        Func<long>? nowMs = null)
    {
        _discovery = discovery;
        _transportFactory = transportFactory;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        if (lockState is not null)
        {
            _lockSleep = new Nexus.Service.Lighting.LcdLockSleepSet(lockState);
        }
    }

    /// <summary>
    /// True while the session lock holds this screen dark. Decided once per lock from
    /// <paramref name="sleepWhenLocked"/>, so a screen that attaches mid-lock joins and a
    /// setting flipped mid-lock changes nothing until the next lock.
    /// </summary>
    public bool IsLockSleeping(string serial, Func<bool> sleepWhenLocked) =>
        _lockSleep is not null && _lockSleep.IsSleeping(serial, sleepWhenLocked);

    /// <summary>
    /// Currently discovered screens (serial + fan-position mapping), refreshed
    /// each tick by <see cref="Slv3LcdConnectionWorker"/>. Empty until the
    /// worker's first pass.
    /// </summary>
    public IReadOnlyList<Slv3LcdScreenInfo> Screens { get; private set; } = Array.Empty<Slv3LcdScreenInfo>();

    internal void UpdateScreens(IReadOnlyList<Slv3LcdScreenInfo> screens) => Screens = screens;

    /// <summary>
    /// Pushes the backlight the screen should show now: dark while the lock holds it, else the
    /// stored value. A fresh attach starts with <paramref name="applied"/> at its sentinel, so
    /// a screen that arrives mid-lock gets the dark value first. Content keeps streaming.
    /// </summary>
    internal void SyncBrightness(string serial, Persistence.LianLiWirelessScreenSettings settings, ref int applied, ref bool lockSlept)
    {
        var sleeping = IsLockSleeping(serial, () => settings.SleepWhenLocked);
        if (sleeping != lockSlept)
        {
            lockSlept = sleeping;
            ServiceLog.Info($"[lcd-lock] lianli screen {serial} {(sleeping ? "sleeping" : "waking")}");
        }
        var brightness = sleeping ? (byte)0 : settings.Brightness;
        if (brightness != applied)
        {
            SetBrightness(serial, brightness);
            applied = brightness;
        }
    }

    /// <summary>Lists the currently enumerable LCD screens (serial + device path).</summary>
    public IReadOnlyList<Slv3LcdPortInfo> Discover() => _discovery.Discover();

    public bool PushImageToSerial(string serial, byte[] jpeg)
    {
        try
        {
            return Write(serial, "push", transport => transport.PushImage(jpeg));
        }
        catch (ArgumentException ex)
        {
            // A frame decoded from the media library (GIF/video, not size-checked
            // per frame) can exceed the firmware's transfer-buffer capacity that
            // BuildPushJpgBuffer enforces; treat that as a failed push rather
            // than crash the caller.
            ServiceLog.Warn($"[lianli-wireless-lcd] push rejected for {serial}: {ex.Message}");
            return false;
        }
    }

    public bool SetBrightness(string serial, byte value)
    {
        lock (_lock)
        {
            _brightness[serial] = value;
        }
        return Write(serial, "brightness", transport => transport.SetBrightness(value));
    }

    public bool SetRotation(string serial, byte value)
    {
        lock (_lock)
        {
            _rotation[serial] = value;
        }
        return Write(serial, "rotation", transport => transport.SetRotation(value));
    }

    /// <summary>Serials with an open connection, including any opened since the last published <see cref="Screens"/>.</summary>
    internal IReadOnlyList<string> OpenSerials()
    {
        lock (_lock)
        {
            return new List<string>(_open.Keys);
        }
    }

    /// <summary>Runs <paramref name="use"/> on the screen's open connection; default when it has none (never opens one).</summary>
    internal T? WithOpenTransport<T>(string serial, Func<ISlv3LcdTransport, T?> use)
    {
        ISlv3LcdTransport? transport;
        lock (_lock)
        {
            _open.TryGetValue(serial, out transport);
        }
        return transport is null ? default : use(transport);
    }

    /// <summary>Closes a screen's connection so its next use opens a fresh one.</summary>
    internal void Forget(string serial, string reason)
    {
        ISlv3LcdTransport? transport;
        lock (_lock)
        {
            _open.Remove(serial, out transport);
            _reopenAfter.Remove(serial);
            _failing.Remove(serial);
            _dropped.Remove(serial);
        }
        if (transport is not null)
        {
            ServiceLog.Info($"[lianli-wireless-lcd] closed {serial} ({reason})");
            transport.Dispose();
        }
    }

    private bool Write(string serial, string what, Func<ISlv3LcdTransport, bool> write)
    {
        var transport = Resolve(serial);
        if (transport is null)
        {
            return false;
        }
        if (write(transport))
        {
            lock (_lock)
            {
                _failing.Remove(serial);
            }
            return true;
        }
        Drop(serial, transport, what);
        return false;
    }

    /// <summary>
    /// A handle to an unplugged screen stays valid while every write fails, so a failed write
    /// drops the connection and the next call reopens the screen once the backoff passes.
    /// </summary>
    private void Drop(string serial, ISlv3LcdTransport transport, string what)
    {
        bool dropped;
        lock (_lock)
        {
            // Another thread already replaced or closed it: nothing of ours failed.
            dropped = _open.TryGetValue(serial, out var current) && ReferenceEquals(current, transport) && _open.Remove(serial);
            if (dropped)
            {
                _dropped.Add(serial);
                _reopenAfter[serial] = _nowMs() + ReopenBackoffMs;
                if (_failing.Add(serial))
                {
                    ServiceLog.Warn($"[lianli-wireless-lcd] {what} failed for {serial}; reopening");
                }
            }
        }
        // Outside the hub lock: Dispose waits out a write another thread has in flight.
        if (dropped)
        {
            transport.Dispose();
        }
    }

    /// <summary>Queries GetPosIndex(201) for the fan-position GroupIndex. False if the screen is unresolvable or does not ack.</summary>
    public bool TryGetPosition(string serial, out int position)
    {
        position = -1;
        var transport = Resolve(serial);
        if (transport is null || !transport.TryGetPosition(out var group))
        {
            return false;
        }
        position = group;
        return true;
    }

    private ISlv3LcdTransport? Resolve(string serial)
    {
        if (_disposed || string.IsNullOrWhiteSpace(serial))
        {
            return null;
        }
        ISlv3LcdTransport transport;
        byte? replayBrightness = null;
        byte? replayRotation = null;
        lock (_lock)
        {
            if (_open.TryGetValue(serial, out var existing))
            {
                if (existing.IsOpen)
                {
                    return existing;
                }
                existing.Dispose();
                _open.Remove(serial);
            }

            if (_reopenAfter.TryGetValue(serial, out var reopenAt) && _nowMs() < reopenAt)
            {
                return null;
            }
            var port = FindPort(serial);
            if (port is null)
            {
                // Discovery enumerates the bus, so a gone screen is not looked up again at frame rate.
                _reopenAfter[serial] = _nowMs() + ReopenBackoffMs;
                return null;
            }

            try
            {
                transport = _transportFactory(port);
            }
            catch (Exception ex)
            {
                // A screen still re-enumerating can be listed before it opens; retry on the backoff, not per frame.
                _reopenAfter[serial] = _nowMs() + ReopenBackoffMs;
                ServiceLog.Error($"[lianli-wireless-lcd] open failed for {serial}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            _open[serial] = transport;
            _reopenAfter.Remove(serial);
            ServiceLog.Info($"[lianli-wireless-lcd] opened {serial}");
            if (_dropped.Remove(serial))
            {
                replayBrightness = _brightness.TryGetValue(serial, out var brightness) ? brightness : null;
                replayRotation = _rotation.TryGetValue(serial, out var rotation) ? rotation : null;
            }
        }
        // Outside the hub lock: a write can wait out its timeout, and other screens must not stall behind it.
        if (replayBrightness is { } level)
        {
            transport.SetBrightness(level);
        }
        if (replayRotation is { } turn)
        {
            transport.SetRotation(turn);
        }
        return transport;
    }

    private Slv3LcdPortInfo? FindPort(string serial)
    {
        foreach (var port in _discovery.Discover())
        {
            if (string.Equals(port.Serial, serial, StringComparison.OrdinalIgnoreCase))
            {
                return port;
            }
        }
        return null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (var transport in _open.Values)
            {
                try
                {
                    transport.Dispose();
                }
                catch
                {
                }
            }
            _open.Clear();
        }
    }
}
