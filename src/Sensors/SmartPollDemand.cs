using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nexus.Service.Lifecycle;
using Nexus.Service.Persistence;

namespace Nexus.Service.Sensors;

/// <summary>
/// What needs SMART reads, captured once so a walk decides every drive from one
/// consistent view. Monitoring and Diagnostics surface drive temperature and
/// health; with both off the only remaining consumer is a fan curve whose input
/// is a drive sensor, which matters while Cooling is on. No LibreHardwareMonitor
/// types, so it compiles on every platform.
/// </summary>
internal sealed class SmartPollDemand
{
    private SmartPollDemand(bool surfaces, bool live, bool cooling, IReadOnlyList<string> curveInputs)
    {
        Surfaces = surfaces || live;
        CoolingOn = cooling;
        CurveInputs = curveInputs;
    }

    /// <summary>Monitoring or Diagnostics is on, or something is showing live sensors.</summary>
    public bool Surfaces { get; }

    public bool CoolingOn { get; }

    /// <summary>Sensor ids of every curve input.</summary>
    public IReadOnlyList<string> CurveInputs { get; }

    /// <summary>
    /// <paramref name="liveConsumers"/> is true while a dashboard, panel, kiosk, overlay or SDK app
    /// is subscribed to live sensor frames, or a poll-style reader (Deck tile, MCP) read SMART data recently.
    /// </summary>
    public static SmartPollDemand Capture(FeatureGates gates, NexusSettings settings, bool liveConsumers = false) =>
        new(
            gates.Monitoring || gates.Diagnostics,
            liveConsumers,
            gates.Cooling,
            Nexus.Service.Cooling.CoolingSnapshots.Curves(settings.Cooling).Select(c => c.Input.Id).ToList());

    /// <summary>True when SMART reads run for this drive (an LHM hardware identifier).</summary>
    public bool IsNeededFor(string hardwareIdentifier) =>
        Surfaces || (CoolingOn && CurveInputs.Any(id => id.StartsWith(hardwareIdentifier + "/", StringComparison.Ordinal)));

    /// <summary>True when SMART reads run for at least one of the drives the walk enumerates.</summary>
    public bool IsActive(IEnumerable<string> driveIdentifiers) =>
        Surfaces || driveIdentifiers.Any(IsNeededFor);

    /// <summary>
    /// A reader that finds drive reads had stopped does its own walk before reading values. When reads were
    /// already running, values are within the user's cadence and nothing is forced.
    /// </summary>
    public static bool ReaderMustWalk(bool windowWasClosed, SmartPollDemand demandWithoutWindow, IEnumerable<string> driveIdentifiers) =>
        windowWasClosed && !demandWithoutWindow.IsActive(driveIdentifiers);

    /// <summary>
    /// The skip count for one drive's walk. A drive nothing needs is never read, Force
    /// included, and forgotten so the first walk after it is needed reads it at once.
    /// A user's Never still wins over the seed and over Force.
    /// </summary>
    public static uint Cycles(bool needed, ISet<string> seeded, string identifier, Func<int> configuredSeconds, bool force)
    {
        if (!needed)
        {
            seeded.Remove(identifier);
            return SmartPollPolicy.ToCycleCount(SmartPollPolicy.NeverSeconds);
        }

        var seconds = configuredSeconds();
        if (seconds <= SmartPollPolicy.NeverSeconds) return SmartPollPolicy.ToCycleCount(seconds);
        if (seeded.Add(identifier) || force) return 1;
        return SmartPollPolicy.ToCycleCount(seconds);
    }

    /// <summary>
    /// With live drive activity off, a drive is updated (and so touched) only when its SMART read is due.
    /// <paramref name="cycles"/> is the value <see cref="Cycles"/> returned; never-read drives are never due.
    /// <paramref name="lastUpdateMs"/> is null for a drive not updated since it last became needed.
    /// </summary>
    public static bool StorageUpdateDue(uint cycles, long? lastUpdateMs, long nowMs, bool force)
    {
        if (cycles == uint.MaxValue) return false;
        if (force || lastUpdateMs is null) return true;
        // The walk floor is just under a second, so a cycle is a hair under 1000 ms.
        return nowMs - lastUpdateMs.Value >= (long)cycles * 1000 - 100;
    }
}

/// <summary>Live-consumer signals for <see cref="SmartPollDemand"/>.</summary>
internal static class SmartLiveConsumers
{
    // The composite monitoring frame and the storage frame are the topics whose broadcast reads SMART sensors.
    // The smart-poll ping topic is deliberately absent: the Settings page's own subscription must not keep the reads alive.
    private static readonly string[] Topics = { "monitoring", "storage" };

    // Readers that poll or call once leave no subscription, so a read keeps demand alive for this long; it outlasts the polling cadences.
    private const long ReaderWindowMs = 15_000;
    private static long _lastReadMs = long.MinValue / 2;

    /// <summary>Called by readers of SMART sensors. Returns true when the window was closed, i.e. drive reads may have stopped and cached values may be old.</summary>
    public static bool NoteRead()
    {
        var now = Environment.TickCount64;
        var seen = Volatile.Read(ref _lastReadMs);
        while (true)
        {
            var wasCold = now - seen >= ReaderWindowMs;
            var actual = Interlocked.CompareExchange(ref _lastReadMs, now, seen);
            if (actual == seen) return wasCold;
            seen = actual;
        }
    }

    internal static void Forget() => Volatile.Write(ref _lastReadMs, long.MinValue / 2);

    /// <summary>Subscribers only, without the reader window: whether live frames alone keep drives reading.</summary>
    public static bool Subscribed(Nexus.Service.Sockets.MultiplexHub? hub) =>
        hub is not null && Topics.Any(hub.TopicHasSubscribers);

    public static bool Any(Nexus.Service.Sockets.MultiplexHub? hub)
    {
        if (Environment.TickCount64 - Volatile.Read(ref _lastReadMs) < ReaderWindowMs) return true;
        return Subscribed(hub);
    }
}

/// <summary>Remembers the last computed <c>active</c> and raises a callback only when it flips; the first observation sets the baseline.</summary>
internal sealed class SmartActiveFlip
{
    private readonly Action _onFlip;
    private int _state = -1;

    public SmartActiveFlip(Action onFlip) => _onFlip = onFlip;

    public void Observe(bool active)
    {
        var next = active ? 1 : 0;
        var previous = Interlocked.Exchange(ref _state, next);
        if (previous != -1 && previous != next)
        {
            _onFlip();
        }
    }
}
