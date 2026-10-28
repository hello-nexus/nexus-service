using System;
using System.Collections.Generic;

namespace Nexus.Service.Activity;

/// <summary>
/// Per-window DWM cloak state for the Windows helper's window-set poller.
/// The cloak read is an RPC into dwm.exe, so re-asking for every candidate
/// on every pass cost hundreds of milliseconds and stalled composition.
/// Cloak changes are pushed by WinEvents instead (<see cref="MarkDirty"/>,
/// <see cref="Evict"/>), and a bounded round-robin trickle re-reads old
/// entries as the safety net for a missed event. Platform-neutral: the
/// actual read is injected, mirroring WindowClassification's split.
///
/// An entry is trusted only while its window was a candidate in the
/// immediately preceding pass, so a window that became visible, lost its
/// owner or tool style, or whose handle was reused is re-read, never
/// served stale. A cached cloaked window is also re-read when its rect
/// moved, since a cloaked window only counts through the covers-monitor
/// test. Events arrive on the hook thread; passes run on the poll thread.
/// </summary>
public sealed class WindowCloakCache
{
    private struct Entry
    {
        public bool Cloaked;
        public bool Dirty;
        public long ReadPass;
        public long TouchedPass;
        public int Left, Top, Right, Bottom;
    }

    private readonly Func<long, bool> _read;
    private readonly int _refreshEveryPasses;
    private readonly int _maxTrickle;
    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _entries = new();
    private readonly List<long> _stale = new();
    private long _pass;
    private int _trickleLeft;

    public WindowCloakCache(Func<long, bool> read, int refreshEveryPasses = 12, int maxTrickle = 16)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _refreshEveryPasses = Math.Max(1, refreshEveryPasses);
        _maxTrickle = Math.Max(0, maxTrickle);
    }

    /// <summary>Cloak reads issued in the current pass, for the perf line.</summary>
    public int Reads { get; private set; }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public void BeginPass()
    {
        lock (_gate)
        {
            _pass++;
            Reads = 0;
            // Each entry is refreshed once per window of passes, so the
            // per-pass share is the entry count over that window.
            var share = (_entries.Count + _refreshEveryPasses - 1) / _refreshEveryPasses;
            _trickleLeft = Math.Min(_maxTrickle, share);
        }
    }

    /// <summary>Drops every entry whose window was not asked about this pass.</summary>
    public void EndPass()
    {
        lock (_gate)
        {
            _stale.Clear();
            foreach (var kv in _entries)
            {
                if (kv.Value.TouchedPass != _pass) _stale.Add(kv.Key);
            }
            foreach (var key in _stale) _entries.Remove(key);
        }
    }

    public bool IsCloaked(long hwnd, int left, int top, int right, int bottom)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(hwnd, out var e) && IsTrusted(e, left, top, right, bottom))
            {
                if (!NeedsTrickle(e))
                {
                    e.TouchedPass = _pass;
                    _entries[hwnd] = e;
                    return e.Cloaked;
                }
                _trickleLeft--;
            }
            return ReadLocked(hwnd, left, top, right, bottom);
        }
    }

    /// <summary>Cloak or uncloak event: re-read on the next ask.</summary>
    public void MarkDirty(long hwnd)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(hwnd, out var e))
            {
                e.Dirty = true;
                _entries[hwnd] = e;
            }
        }
    }

    /// <summary>Destroy event: forget the handle so a reuse cannot hit it.</summary>
    public void Evict(long hwnd)
    {
        lock (_gate) _entries.Remove(hwnd);
    }

    private bool IsTrusted(Entry e, int left, int top, int right, int bottom)
    {
        if (e.Dirty || e.TouchedPass != _pass - 1)
        {
            return false;
        }
        return !e.Cloaked || (e.Left == left && e.Top == top && e.Right == right && e.Bottom == bottom);
    }

    private bool NeedsTrickle(Entry e) =>
        _trickleLeft > 0 && _pass - e.ReadPass >= _refreshEveryPasses;

    private bool ReadLocked(long hwnd, int left, int top, int right, int bottom)
    {
        var cloaked = _read(hwnd);
        Reads++;
        _entries[hwnd] = new Entry
        {
            Cloaked = cloaked,
            ReadPass = _pass,
            TouchedPass = _pass,
            Left = left,
            Top = top,
            Right = right,
            Bottom = bottom,
        };
        return cloaked;
    }
}
