using System;

namespace Nexus.Service.Lighting.IdleDim;

/// <summary>One notification per actual change of the OS screen-off timeout; most hints (registration replays, a plan or AC/DC switch) leave it unchanged.</summary>
public sealed class ScreenOffTimeoutWatch
{
    private readonly Func<int?> _read;
    private readonly Action<int?> _changed;
    private readonly object _gate = new();
    private int? _last;

    public ScreenOffTimeoutWatch(Func<int?> read, Action<int?> changed)
    {
        _read = read;
        _changed = changed;
        _last = read();
    }

    public void OnHint()
    {
        int? now;
        lock (_gate)
        {
            now = _read();
            if (now == _last)
            {
                return;
            }
            _last = now;
        }
        _changed(now);
    }
}
