namespace Nexus.Service.Conflicts;

/// <summary>
/// Caches a count taken by an expensive walk and repeats the walk only when the bus signature
/// changes or <see cref="Invalidate"/> is called, never on a timer. A null signature (no bus
/// source wired) counts once and then holds until invalidated.
/// </summary>
internal sealed class BusGatedCount
{
    private readonly object _lock = new();
    private string? _signature;
    private int _count;
    private bool _has;

    public int Get(string? signature, System.Func<int> walk)
    {
        lock (_lock)
        {
            if (_has && string.Equals(signature, _signature, System.StringComparison.Ordinal))
            {
                return _count;
            }
            _count = walk();
            _signature = signature;
            _has = true;
            return _count;
        }
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _has = false;
        }
    }
}
