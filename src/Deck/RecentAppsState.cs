using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Deck;

/// <summary>
/// The live Recent Apps ring, excluded-key list, and currently focused
/// process key - single in-memory authority for every reader (the worker,
/// the routes) so a reorder is visible the instant it happens, per the
/// plan's "no dwell, reorders instantly" decision. settings.json trails this
/// by up to RecentAppsService's persist interval; FocusedProcessKey itself
/// is never persisted at all. RecentAppsService seeds the ring from disk
/// once at startup and owns writing it back; the routes mutate through this
/// class too, so a read never race a write against a stale settings.json
/// snapshot.
/// </summary>
public sealed class RecentAppsState
{
    private readonly object _lock = new();
    private List<RecentApp> _ring = new();
    private List<string> _excluded = new();
    private volatile string? _focusedProcessKey;

    public string? FocusedProcessKey => _focusedProcessKey;

    public void SetFocused(string? processKey) => _focusedProcessKey = processKey;

    /// <summary>Replaces the ring and excluded list wholesale, in the given order. RecentAppsService uses this once at startup to import the persisted copy (with every Pid already stripped - see its own doc comment); tests use it to seed a known ring directly.</summary>
    public void Seed(IEnumerable<RecentApp> ring, IEnumerable<string> excluded)
    {
        lock (_lock)
        {
            _ring = new List<RecentApp>(ring);
            _excluded = new List<string>(excluded);
        }
    }

    /// <summary>Applies a focus change to the live ring (RecentAppsTracker.UpdateRing); false when candidate is excluded or empty.</summary>
    public bool UpdateRing(RecentApp candidate)
    {
        lock (_lock)
        {
            return RecentAppsTracker.UpdateRing(_ring, candidate, _excluded);
        }
    }

    public void SetExcluded(IReadOnlyList<string> excluded)
    {
        lock (_lock)
        {
            _excluded = new List<string>(excluded);
            _ring.RemoveAll(a => _excluded.Contains(a.ProcessKey));
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _ring.Clear();
        }
    }

    public List<RecentApp> RingSnapshot()
    {
        lock (_lock)
        {
            return new List<RecentApp>(_ring);
        }
    }

    public List<string> ExcludedSnapshot()
    {
        lock (_lock)
        {
            return new List<string>(_excluded);
        }
    }
}
