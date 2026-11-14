using System;
using System.Threading;

namespace Nexus.Service.Platform;

/// <summary>A deadline that fires its callback once per <see cref="Arm"/>. Nothing runs between arming and the deadline, and a disarmed or unarmed one costs nothing.</summary>
internal interface IOneShot : IDisposable
{
    /// <summary>Replaces any pending deadline.</summary>
    void Arm(TimeSpan due);

    void Disarm();
}

/// <summary><see cref="IOneShot"/> over a system timer with no period.</summary>
internal sealed class TimerOneShot : IOneShot
{
    private readonly Timer _timer;

    internal TimerOneShot(Action fire)
    {
        _timer = new Timer(_ => fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Arm(TimeSpan due)
    {
        try { _timer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    public void Disarm()
    {
        try { _timer.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose() => _timer.Dispose();
}
