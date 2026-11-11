using Nexus.Service.Platform;

namespace Nexus.Service.Tests.Common;

/// <summary>A deadline tests fire by hand: it records what the code armed and never touches a real timer.</summary>
internal sealed class FakeOneShot : IOneShot
{
    private readonly Action _fire;

    internal FakeOneShot(Action fire) => _fire = fire;

    internal bool Armed { get; private set; }

    internal TimeSpan Due { get; private set; }

    internal int ArmCount { get; private set; }

    internal bool Disposed { get; private set; }

    public void Arm(TimeSpan due)
    {
        Armed = true;
        Due = due;
        ArmCount++;
    }

    public void Disarm() => Armed = false;

    public void Dispose() => Disposed = true;

    /// <summary>What the timer does at the deadline: it is no longer armed, then the callback runs.</summary>
    internal void Fire()
    {
        Armed = false;
        _fire();
    }

    /// <summary>A factory that hands out one fake per call and remembers each.</summary>
    internal sealed class Factory
    {
        internal List<FakeOneShot> Created { get; } = new();

        internal FakeOneShot? Single => Created.Count == 1 ? Created[0] : null;

        internal IOneShot Create(Action fire)
        {
            var one = new FakeOneShot(fire);
            Created.Add(one);
            return one;
        }
    }
}
