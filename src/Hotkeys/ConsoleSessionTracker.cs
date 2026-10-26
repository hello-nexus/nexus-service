namespace Nexus.Service.Hotkeys;

/// <summary>Remembers the active console session id and reports when it differs from the last one seen.</summary>
public sealed class ConsoleSessionTracker
{
    private readonly object _gate = new();
    private int? _last;

    /// <summary>True when <paramref name="current"/> differs from the previous call's (the first call counts as a change).</summary>
    public bool HasChanged(int current)
    {
        lock (_gate)
        {
            if (_last == current)
            {
                return false;
            }
            _last = current;
            return true;
        }
    }
}
