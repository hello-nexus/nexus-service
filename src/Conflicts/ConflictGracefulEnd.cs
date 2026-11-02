using System;
using System.Collections.Generic;
using System.Threading;

namespace Nexus.Service.Conflicts;

/// <summary>What became of a session-end request.</summary>
public enum SessionEndOutcome
{
    /// <summary>No user-session helper answered (not connected yet, or no user session).</summary>
    HelperUnavailable,
    /// <summary>The helper found no top-level window for the processes yet.</summary>
    NoWindows,
    /// <summary>Every window refused the message: the target runs at a higher integrity level than the helper.</summary>
    Refused,
    /// <summary>At least one window accepted the message.</summary>
    Delivered,
}

/// <summary>Sends the session-end messages to the top-level windows of the given processes. Implemented over the user-session helper: the service runs in session 0 and cannot message windows in the user's session.</summary>
public interface IAppSessionEnder
{
    SessionEndOutcome TrySend(IReadOnlyList<int> pids);
}

/// <summary>
/// Ends a catalog app by asking it to close first: session-end messages to its
/// <see cref="ConflictAppDefinition.GracefulProcessNames"/> windows, repeated while it
/// is still starting up, then a hard kill only if it is still running after
/// <see cref="Wait"/>. An app that closes itself releases its hardware cleanly; a hard
/// kill leaves the hardware in whatever state the app left it.
/// </summary>
public sealed class ConflictGracefulEnd
{
    /// <summary>Wait for the app to exit after a request was delivered, before the hard kill.</summary>
    internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound while no request could be delivered yet: at boot the helper and the app's windows come up after the service.</summary>
    internal static readonly TimeSpan NotReachableWait = TimeSpan.FromSeconds(30);

    /// <summary>The messages are sent again this often, so an app that was too early in its startup to hear the first one gets another.</summary>
    internal static readonly TimeSpan Resend = TimeSpan.FromSeconds(3);

    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<ConflictAppDefinition, IReadOnlyList<int>> _findPids;
    private readonly Func<IReadOnlyList<int>, SessionEndOutcome> _send;
    private readonly Func<ConflictAppDefinition, bool> _stillRunning;
    private readonly Action<ConflictAppDefinition> _hardKill;
    private readonly Func<long> _clockMs;
    private readonly Action<int> _sleepMs;
    private readonly Action<string>? _log;

    public ConflictGracefulEnd(IAppSessionEnder? ender, Action<string>? log = null)
        : this(
            def => ConflictKiller.FindGracefulProcessIds(def, ConflictSignerGate.Default),
            pids => ender?.TrySend(pids) ?? SessionEndOutcome.HelperUnavailable,
            ConflictKiller.AnyProcessRunning,
            def => ConflictKiller.Kill(def),
            () => Environment.TickCount64,
            ms => Thread.Sleep(ms),
            log)
    {
    }

    internal ConflictGracefulEnd(
        Func<ConflictAppDefinition, IReadOnlyList<int>> findPids,
        Func<IReadOnlyList<int>, SessionEndOutcome> send,
        Func<ConflictAppDefinition, bool> stillRunning,
        Action<ConflictAppDefinition> hardKill,
        Func<long> clockMs,
        Action<int> sleepMs,
        Action<string>? log = null)
    {
        _findPids = findPids;
        _send = send;
        _stillRunning = stillRunning;
        _hardKill = hardKill;
        _clockMs = clockMs;
        _sleepMs = sleepMs;
        _log = log;
    }

    /// <summary>Blocks until the app exits or its deadline passes, then hard-kills what remains. The deadline is <see cref="NotReachableWait"/> from the start and, once a request is delivered, <see cref="Wait"/> from that moment; a refused request (a higher-integrity app) ends the wait at once.</summary>
    public void End(ConflictAppDefinition def)
    {
        if (def.GracefulProcessNames.Length == 0)
        {
            _hardKill(def);
            return;
        }
        var start = _clockMs();
        var deadline = start + (long)NotReachableWait.TotalMilliseconds;
        var lastSent = long.MinValue / 2;
        var last = SessionEndOutcome.HelperUnavailable;
        var delivered = false;
        while (true)
        {
            if (!_stillRunning(def))
            {
                _log?.Invoke($"{def.DisplayName} closed after {_clockMs() - start} ms");
                return;
            }
            var now = _clockMs();
            if (now >= deadline) break;
            if (now - lastSent >= (long)Resend.TotalMilliseconds)
            {
                lastSent = now;
                var pids = _findPids(def);
                if (pids.Count > 0)
                {
                    last = _send(pids);
                    if (last == SessionEndOutcome.Refused) break;
                    if (last == SessionEndOutcome.Delivered && !delivered)
                    {
                        delivered = true;
                        deadline = now + (long)Wait.TotalMilliseconds;
                    }
                }
            }
            _sleepMs((int)PollInterval.TotalMilliseconds);
        }
        _log?.Invoke($"{def.DisplayName} still running after {_clockMs() - start} ms (close request: {last}); ending it");
        _hardKill(def);
    }
}
