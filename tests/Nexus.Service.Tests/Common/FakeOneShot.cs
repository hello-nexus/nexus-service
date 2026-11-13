using Nexus.Service.Platform;

namespace Nexus.Service.Tests.Common;

/// <summary>A deadline tests fire by hand: it records what the code armed and never touches a real timer.</summary>
internal sealed class FakeOneShot : IOneShot
{
    private readonly Action _fire;
    private readonly object _lock = new();
    private bool _armed;
    private TimeSpan _due;
    private int _armCount;
    private bool _disposed;

    internal FakeOneShot(Action fire) => _fire = fire;

    internal bool Armed { get { lock (_lock) { return _armed; } } }

    internal TimeSpan Due { get { lock (_lock) { return _due; } } }

    internal int ArmCount { get { lock (_lock) { return _armCount; } } }

    internal bool Disposed { get { lock (_lock) { return _disposed; } } }

    public void Arm(TimeSpan due)
    {
        lock (_lock)
        {
            _armed = true;
            _due = due;
            _armCount++;
        }
    }

    public void Disarm()
    {
        lock (_lock) { _armed = false; }
    }

    public void Dispose()
    {
        lock (_lock) { _disposed = true; }
    }

    /// <summary>What the timer does at the deadline: it is no longer armed, then the callback runs.</summary>
    internal void Fire()
    {
        lock (_lock) { _armed = false; }
        _fire();
    }

    /// <summary>A factory that hands out one fake per call and remembers each.</summary>
    internal sealed class Factory
    {
        private readonly object _lock = new();
        private readonly List<FakeOneShot> _created = new();

        internal IReadOnlyList<FakeOneShot> Created { get { lock (_lock) { return _created.ToArray(); } } }

        internal FakeOneShot? Single { get { lock (_lock) { return _created.Count == 1 ? _created[0] : null; } } }

        internal IOneShot Create(Action fire)
        {
            var one = new FakeOneShot(fire);
            lock (_lock) { _created.Add(one); }
            return one;
        }
    }
}
