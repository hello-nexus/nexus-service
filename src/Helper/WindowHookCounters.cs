using System.Threading;

namespace Nexus.Service.Helper;

/// <summary>Running counts of the WinEvents the cloak watcher receives, by type.
/// Written on the hook thread, read on the poll thread.</summary>
public sealed class WindowHookCounters
{
    public const uint EventObjectDestroy = 0x8001;
    public const uint EventObjectCloaked = 0x8017;
    public const uint EventObjectUncloaked = 0x8018;

    private long _destroy;
    private long _cloaked;
    private long _uncloaked;

    public void Count(uint eventType)
    {
        switch (eventType)
        {
            case EventObjectDestroy: Interlocked.Increment(ref _destroy); break;
            case EventObjectCloaked: Interlocked.Increment(ref _cloaked); break;
            case EventObjectUncloaked: Interlocked.Increment(ref _uncloaked); break;
        }
    }

    public (long Destroy, long Cloaked, long Uncloaked) Totals =>
        (Interlocked.Read(ref _destroy), Interlocked.Read(ref _cloaked), Interlocked.Read(ref _uncloaked));
}
