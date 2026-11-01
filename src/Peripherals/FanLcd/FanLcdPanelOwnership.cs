using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.FanLcd;

/// <summary>
/// Which screens their panel session may draw on. A panel frame is sent under the same per-screen
/// lock that <see cref="Set"/> takes, so once a screen is taken back no panel frame can follow,
/// not even one already in flight.
/// </summary>
public sealed class FanLcdPanelOwnership
{
    private readonly object _locksLock = new();
    private readonly Dictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);
    // The per-screen lock orders one screen's frames against its Set; screens run on different threads, so the
    // shared tables themselves must tolerate concurrent writers.
    private readonly ConcurrentDictionary<string, byte> _owned = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _last = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string serial, bool owned)
    {
        lock (LockFor(serial))
        {
            if (owned)
            {
                _owned[serial] = 0;
            }
            else
            {
                _owned.TryRemove(serial, out _);
                _last.TryRemove(serial, out _);
            }
        }
    }

    /// <summary>Runs <paramref name="push"/> only while the panel owns the screen; false when it does not.</summary>
    public bool Push(string serial, Func<bool> push)
    {
        lock (LockFor(serial))
        {
            return _owned.ContainsKey(serial) && push();
        }
    }

    /// <summary>As <see cref="Push(string, Func{bool})"/>, remembering the frame the screen now shows.</summary>
    public bool Push(string serial, byte[] jpeg, Func<bool> push)
    {
        lock (LockFor(serial))
        {
            if (!_owned.ContainsKey(serial) || !push())
            {
                return false;
            }
            _last[serial] = jpeg;
            return true;
        }
    }

    /// <summary>
    /// Sends the last panel frame again. The screen turns a frame as it arrives, so a rotation change
    /// leaves the picture on the display as it was until the next frame; a panel whose layout is still
    /// sends none.
    /// </summary>
    public bool Repush(string serial, Func<byte[], bool> push)
    {
        lock (LockFor(serial))
        {
            return _owned.ContainsKey(serial) && _last.TryGetValue(serial, out var jpeg) && push(jpeg);
        }
    }

    private object LockFor(string serial)
    {
        lock (_locksLock)
        {
            if (!_locks.TryGetValue(serial, out var gate))
            {
                gate = new object();
                _locks[serial] = gate;
            }
            return gate;
        }
    }
}
