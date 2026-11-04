namespace Nexus.Service.Platform;

/// <summary>
/// Decides which repeats of a log line are written. A line is emitted when its state differs from the
/// last one emitted under the same key (so the first occurrence and every transition always land),
/// and otherwise at most once per interval, carrying the count of identical lines withheld since the
/// previous emission. Used for both "log on change plus a periodic summary" and, with a constant
/// state, plain rate limiting of a repeated warning.
/// </summary>
internal sealed class LogGate
{
    private sealed class Entry
    {
        public string State = string.Empty;
        public long LastEmitMs;
        public int Suppressed;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly long _intervalMs;
    private readonly Func<long> _nowMs;
    private readonly int _maxKeys;

    public LogGate(TimeSpan interval, Func<long>? nowMs = null, int maxKeys = 512)
    {
        _intervalMs = (long)interval.TotalMilliseconds;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _maxKeys = maxKeys;
    }

    /// <summary>
    /// True when the line should be written. <paramref name="suppressed"/> is the number of identical
    /// lines withheld since the previous emission for this key; after a state change it counts repeats
    /// of the previous state.
    /// </summary>
    public bool Check(string key, string state, out int suppressed)
    {
        var now = _nowMs();
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var e))
            {
                // Dropping everything is safe: the worst case is one repeated line per key.
                if (_entries.Count >= _maxKeys)
                    _entries.Clear();
                _entries[key] = new Entry { State = state, LastEmitMs = now };
                suppressed = 0;
                return true;
            }

            if (!string.Equals(e.State, state, StringComparison.Ordinal) || now - e.LastEmitMs >= _intervalMs)
            {
                suppressed = e.Suppressed;
                e.State = state;
                e.LastEmitMs = now;
                e.Suppressed = 0;
                return true;
            }

            e.Suppressed++;
            suppressed = 0;
            return false;
        }
    }

    /// <summary>Forgets a key so its next line is a first occurrence; true (with the withheld count) when the key had state, i.e. a transition out of it is worth a line.</summary>
    public bool Reset(string key, out int suppressed)
    {
        lock (_lock)
        {
            if (_entries.Remove(key, out var e))
            {
                suppressed = e.Suppressed;
                return true;
            }
        }
        suppressed = 0;
        return false;
    }

    /// <summary>Message suffix naming the withheld count, empty when none.</summary>
    public static string Suffix(int suppressed) => suppressed > 0 ? $" suppressed={suppressed}" : string.Empty;
}
