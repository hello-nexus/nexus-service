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
/// <see cref="ConflictAppDefinition.GracefulProcessNames"/> windows, then a hard kill of
/// what is left. An app that closes itself releases its hardware cleanly; a hard kill
/// leaves the hardware as the app left it. Never runs while a
/// <see cref="ConflictAppDefinition.BackOffOnlyProcessNames"/> process is alive.
/// </summary>
public sealed class ConflictGracefulEnd
{
    /// <summary>Wait for the app to exit after a request was delivered, before the hard kill.</summary>
    internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound while no request could be delivered yet: at boot the helper and the app's windows come up after the service.</summary>
    internal static readonly TimeSpan NotReachableWait = TimeSpan.FromSeconds(30);

    /// <summary>A request is sent again this often until one is delivered, and, while the app is starting up, after that too.</summary>
    internal static readonly TimeSpan Resend = TimeSpan.FromSeconds(3);

    /// <summary>Process age under which the app counts as starting up. A request delivered then is often ignored (measured on iCUE: not reacted to for 60 s), and one sent after it is reliable; a second request into an app already closing can stall its exit, so past this age exactly one is sent.</summary>
    internal static readonly TimeSpan StartupWindow = TimeSpan.FromSeconds(25);

    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Func<ConflictAppDefinition, IReadOnlyList<int>> _findPids;
    private readonly Func<IReadOnlyList<int>, long> _ageMs;
    private readonly Func<IReadOnlyList<int>, SessionEndOutcome> _send;
    private readonly Func<ConflictAppDefinition, bool> _anyRunning;
    private readonly Func<ConflictAppDefinition, bool> _backOffRunning;
    private readonly Action<ConflictAppDefinition> _hardKill;
    private readonly Func<long> _clockMs;
    private readonly Action<int> _sleepMs;
    private readonly Action<string>? _log;
    private readonly bool _canSend;

    public ConflictGracefulEnd(IAppSessionEnder? ender, Action<string>? log = null)
        : this(
            def => ConflictKiller.FindGracefulProcessIds(def, ConflictSignerGate.Default),
            ConflictKiller.YoungestAgeMs,
            pids => ender?.TrySend(pids) ?? SessionEndOutcome.HelperUnavailable,
            ConflictKiller.AnyProcessRunning,
            def => ConflictKiller.AnyBackOffOnlyRunning(def, ConflictSignerGate.Default),
            def => ConflictKiller.Kill(def),
            () => Environment.TickCount64,
            ms => Thread.Sleep(ms),
            log,
            canSend: ender is not null)
    {
    }

    internal ConflictGracefulEnd(
        Func<ConflictAppDefinition, IReadOnlyList<int>> findPids,
        Func<IReadOnlyList<int>, long> ageMs,
        Func<IReadOnlyList<int>, SessionEndOutcome> send,
        Func<ConflictAppDefinition, bool> anyRunning,
        Func<ConflictAppDefinition, bool> backOffRunning,
        Action<ConflictAppDefinition> hardKill,
        Func<long> clockMs,
        Action<int> sleepMs,
        Action<string>? log = null,
        bool canSend = true)
    {
        _findPids = findPids;
        _ageMs = ageMs;
        _send = send;
        _anyRunning = anyRunning;
        _backOffRunning = backOffRunning;
        _hardKill = hardKill;
        _clockMs = clockMs;
        _sleepMs = sleepMs;
        _log = log;
        _canSend = canSend;
    }

    /// <summary>Same outcome shape as <see cref="ConflictKiller.Kill(ConflictAppDefinition)"/>, for the end-task routes.</summary>
    public ConflictKiller.KillOutcome EndWithOutcome(ConflictAppDefinition def)
    {
        if (def.IsSetting) return ConflictKiller.Kill(def);
        var before = _anyRunning(def);
        End(def);
        return new ConflictKiller.KillOutcome(before, _anyRunning(def));
    }

    /// <summary>Blocks until the app exits or its deadline passes. The deadline is <see cref="NotReachableWait"/> from the start and, once a request is delivered, <see cref="Wait"/> from that moment (from the end of <see cref="StartupWindow"/> for an app still starting up); a request every window refused (a higher-integrity app) ends the wait at once. Leftover processes and services are ended as soon as the app itself is gone.</summary>
    public void End(ConflictAppDefinition def)
    {
        if (_backOffRunning(def))
        {
            _log?.Invoke($"{def.DisplayName}: an installer is running; not ending it");
            return;
        }
        if (def.GracefulProcessNames.Length == 0 || !_canSend)
        {
            _hardKill(def);
            return;
        }
        var start = _clockMs();
        var deadline = start + (long)NotReachableWait.TotalMilliseconds;
        var lastSent = long.MinValue / 2;
        var last = SessionEndOutcome.HelperUnavailable;
        var delivered = false;
        var sentPastStartup = false;
        while (true)
        {
            var pids = _findPids(def);
            if (pids.Count == 0)
            {
                _log?.Invoke($"{def.DisplayName} closed after {_clockMs() - start} ms");
                if (_anyRunning(def)) _hardKill(def);
                return;
            }
            var now = _clockMs();
            if (now >= deadline) break;
            if (now - lastSent >= (long)Resend.TotalMilliseconds)
            {
                var age = _ageMs(pids);
                var starting = age < (long)StartupWindow.TotalMilliseconds;
                if (!delivered || starting || !sentPastStartup)
                {
                    lastSent = now;
                    last = _send(pids);
                    _log?.Invoke($"{def.DisplayName}: close request {last} after {now - start} ms (app age {age} ms)");
                    if (last == SessionEndOutcome.Refused) break;
                    if (last == SessionEndOutcome.Delivered)
                    {
                        sentPastStartup |= !starting;
                        if (!delivered)
                        {
                            delivered = true;
                            // An app still starting up is given until the window has passed and one more request had its chance, then the plain wait.
                            var untilPast = starting ? (long)StartupWindow.TotalMilliseconds - age + (long)Resend.TotalMilliseconds : 0;
                            deadline = now + untilPast + (long)Wait.TotalMilliseconds;
                        }
                    }
                }
            }
            _sleepMs((int)PollInterval.TotalMilliseconds);
        }
        _log?.Invoke($"{def.DisplayName} still running after {_clockMs() - start} ms (close request: {last}); ending it");
        _hardKill(def);
    }
}
