using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Lifecycle;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Platform;
using Nexus.Service.Persistence;

namespace Nexus.Service.Lighting;

/// <summary>
/// Fades lighting out as the host suspends or shuts down, and restores it on
/// resume, when <see cref="LightingSettings.SleepBlackout"/> is on. Also owns
/// the session-lock path (<see cref="LightingSettings.LockBlackout"/>), which
/// is the same hold reached under none of the same constraints - see
/// <see cref="OnSessionLocked"/>.
///
/// Sleep already takes most devices dark for free, because the host cuts their
/// bus. RAM does not: DIMMs stay powered across S3 (and across S5 on a board
/// without ErP) and the SMBus controller holds whatever frame it was last
/// written, so the sticks glow all night. The fix is to write black BEFORE the
/// machine stops - after it stops there is no one left to write.
///
/// That deadline shapes the whole class. The OS gives suspend subscribers a
/// short window and then goes down regardless, so <see cref="OnSuspending"/>
/// runs INLINE on the caller's thread (handing off to the thread pool loses the
/// race) under a hard <see cref="Budget"/>, and every wait is on a real signal
/// - the engine publishing black, the OpenRGB writes completing - rather than a
/// guessed delay.
///
/// The fade is the one part that is time-based rather than signalled, so it is
/// deadline-first: it never eats into <see cref="TerminalReserve"/>, and the run
/// ALWAYS ends by engaging the hard blackout and pushing black, whether the fade
/// completed, was cut short, or never started. A fade that runs out of budget
/// leaves the lights dark, never parked half-lit.
/// </summary>
public sealed class SleepBlackoutCoordinator
{
    /// <summary>
    /// Total inline budget for a suspend. Windows proceeds with the suspend
    /// once handlers have had their turn, so overrunning this does not block
    /// sleep, it just stops being useful; the cap is what keeps a wedged
    /// OpenRGB socket from holding the shared SystemEvents pump thread.
    /// </summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Budget for the OS-shutdown path. Under the 1500ms cap on the fast
    /// teardown that calls it (Program.cs FastServiceShutdown), so the terminal
    /// black frame lands inside the cap instead of being cut off by it.
    /// </summary>
    internal static readonly TimeSpan ShutdownBudget = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// Wall clock the ramp runs over. Short because it races the host tearing
    /// down USB: devices behind it stop receiving frames partway with no error,
    /// since the writes report success into a handle that is going away. Longer
    /// ramps measurably left them lit; only SMBus devices, RAM among them, take
    /// the ramp at any length.
    /// </summary>
    internal static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Tail of the budget the fade may not touch, kept for the terminal black
    /// frame. Sized for the engine publish plus an awaited push to every OpenRGB
    /// device, which is the leg that reaches RAM.
    /// </summary>
    private static readonly TimeSpan TerminalReserve = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// Share of the budget allowed for the engine to publish the final black
    /// frame. Bounded well under one budget so a stalled render loop still
    /// leaves time for the direct OpenRGB push.
    /// </summary>
    internal static readonly TimeSpan EnginePublishBudget = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Ramp down on lock. An order of magnitude longer than
    /// <see cref="FadeDuration"/> because none of what shortens that one
    /// applies: the host stays up, every bus with it, and no deadline is
    /// running. It is only allowed to look good.
    /// </summary>
    internal static readonly TimeSpan LockFadeDuration = TimeSpan.FromMilliseconds(1500);

    /// <summary>Ramp back up on unlock. Shorter than the way down: the user is
    /// already at the machine waiting for it.</summary>
    internal static readonly TimeSpan UnlockFadeDuration = TimeSpan.FromMilliseconds(900);

    /// <summary>
    /// How long the lights stay up after input at the lock screen. Matches the
    /// 30s Windows takes to blank the display at the lock screen (measured on
    /// T1), so the lighting goes out with the screen rather than glowing on in
    /// a dark room; it is also long enough to type a password you had to look
    /// at the keyboard for. Every further input restarts it.
    /// </summary>
    internal static readonly TimeSpan DefaultLockWakeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Two engine ticks of slack: the ramp reaches zero on the first tick past the window.</summary>
    private TimeSpan FrameSlack => TimeSpan.FromMilliseconds(Math.Max(1, _engine.FrameIntervalMs) * 2);

    /// <summary>
    /// Windows only: the timings above were measured there and nowhere else.
    /// Elsewhere this blanks in one write, as it always has.
    /// </summary>
    internal static bool FadeSupported => OperatingSystem.IsWindows();

    private readonly LightingEngine _engine;
    private readonly IConfigStore _store;
    private readonly RgbBridge? _bridge;
    private readonly FeatureGates _gates;

    /// <summary>
    /// True between a lock and its unlock. Load-bearing for exactly one path:
    /// a machine that sleeps while locked wakes to the LOCK SCREEN, and the
    /// resume that follows must not hand the lighting back there. Nothing
    /// re-derives lock state at startup (see <see cref="OnSessionLocked"/>), so
    /// this flag is the only record that the session is still locked.
    /// </summary>
    private volatile bool _lockHold;

    /// <summary>
    /// Serializes clearing <see cref="_lockHold"/> against the idle window
    /// engaging a blackout on it. Without it the timer can pass its check and
    /// then darken a desktop the user has just unlocked - with the hold flag
    /// already false, so nothing is left to release it.
    /// </summary>
    private readonly object _lockHoldGate = new();

    /// <summary>
    /// Idle window after lock-screen input, refreshed by every further input.
    /// Overridable so tests do not wait out the real window for the re-dark.
    /// </summary>
    private readonly TimeSpan _lockWakeTimeout;

    /// <summary>Fires when <see cref="_lockWakeTimeout"/> elapses with no further input.</summary>
    private readonly Timer _lockWakeTimer;

    /// <summary>
    /// Arms and disarms the lock-screen input poll: the Windows helper's via
    /// TrayBootstrap, MacLockInputWatch via MacAppBootstrap; null on Linux,
    /// which means no wake-on-input. A delegate rather than a HelperRegistry
    /// because that type is Windows-only and this class is not.
    /// </summary>
    public Action<bool>? LockInputWatch { get; set; }

    public SleepBlackoutCoordinator(LightingEngine engine, IConfigStore store, RgbBridge? bridge = null, FeatureGates? gates = null, TimeSpan? lockWakeTimeout = null)
    {
        _engine = engine;
        _store = store;
        _bridge = bridge;
        _gates = gates ?? FeatureGates.AllEnabled;
        _lockWakeTimeout = lockWakeTimeout ?? DefaultLockWakeTimeout;
        _lockWakeTimer = new Timer(_ => OnLockWakeExpired(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Call inline from the OS suspend notification. No-op when the setting is
    /// off. Swallows everything: a failure here must never abort the host's
    /// suspend handling.
    /// </summary>
    public void OnSuspending() => BlankOut(Budget, "suspend");

    /// <summary>
    /// Call inline from the service's fast teardown, on a real OS shutdown only.
    /// Any other stop (tray quit, /service/stop, an OTA install, a GPU-change
    /// restart) leaves lighting alone - the service comes back and repaints.
    /// </summary>
    public void OnHostShutdown() => BlankOut(ShutdownBudget, "shutdown");

    /// <summary>
    /// Call when the session locks. Unlike every other path here nothing is
    /// tearing down: the machine keeps running, so this returns immediately and
    /// lets the render loop paint the ramp, with no budget, no inline wait and
    /// no terminal push - the loop is still
    /// feeding every writer, including the OpenRGB bridge.
    ///
    /// Deliberately reachable only from a real lock TRANSITION. Lock state is
    /// never seeded at startup: a machine sitting at the login screen after a
    /// cold boot has never been locked by anyone, and blanking there would read
    /// as lighting that does not come on until you sign in.
    /// </summary>
    public void OnSessionLocked()
    {
        try
        {
            if (!_store.Load().Lighting.LockBlackout)
            {
                return;
            }
            _lockHold = true;
            // Armed for as long as the session is locked, so input can bring the
            // lighting back for whoever is standing at the prompt. Armed even
            // when the blackout is already engaged below - the poll is what
            // notices them arriving, not what darkened the room.
            SetLockInputWatch(true);
            if (_engine.Blackout && !_engine.BlackoutReleasing)
            {
                // Already dark or on its way (a second lock notification, or a
                // lock arriving on top of the suspend blackout).
                return;
            }
            EngageLockBlackout();
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[lighting-lock] lock blackout failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Call when the session unlocks. Releases whatever hold is engaged, not
    /// only one this class engaged for the lock, and unconditionally of the
    /// setting - same reason <see cref="OnResumed"/> does: a user who turned it
    /// off mid-lock must get their lighting back, not stay dark.
    /// </summary>
    public void OnSessionUnlocked()
    {
        try
        {
            lock (_lockHoldGate)
            {
                _lockHold = false;
            }
            SetLockInputWatch(false);
            // The session is the user's again, so the idle window has nothing
            // left to re-darken; a pending one would fire into a live desktop.
            StopLockWakeTimer();
            if (!_engine.Blackout || _engine.BlackoutReleasing)
            {
                return;
            }
            _engine.BeginBlackoutRelease(UnlockFadeDuration);
            ServiceLog.Info($"[lighting-lock] fading in over {(int)UnlockFadeDuration.TotalMilliseconds}ms");
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[lighting-lock] unlock release failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Call when the helper reports input at the lock screen. Brings the
    /// lighting back on the same ramp an unlock uses - the point is someone in
    /// a dark room needing to see the keys - and starts (or restarts) the idle
    /// window after which it goes dark again.
    ///
    /// Every input refreshes the window, whether the lights are already up or
    /// not, so a hand resting on the keyboard keeps them up while a single
    /// accidental keypress buys one window and then lets the machine go dark.
    /// </summary>
    public void OnLockScreenInput()
    {
        try
        {
            // Only meaningful while the session is locked. An envelope arriving
            // after an unlock (in flight as the user signed in) must not park a
            // timer that would darken a desktop the user is sitting at.
            if (!_lockHold)
            {
                return;
            }
            if (!_store.Load().Lighting.LockBlackout)
            {
                return;
            }

            _lockWakeTimer.Change(_lockWakeTimeout, Timeout.InfiniteTimeSpan);

            if (_engine.Blackout && !_engine.BlackoutReleasing)
            {
                _engine.BeginBlackoutRelease(UnlockFadeDuration);
                ServiceLog.Info($"[lighting-lock] input at the lock screen - fading in over {(int)UnlockFadeDuration.TotalMilliseconds}ms");
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[lighting-lock] lock-screen input wake failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The idle window elapsed. Re-darkens only if the session is still locked;
    /// an unlock in the meantime already cancelled this, but the check also
    /// covers the race where it fired first.
    /// </summary>
    private void OnLockWakeExpired()
    {
        try
        {
            if (_engine.Blackout && !_engine.BlackoutReleasing)
            {
                return;
            }
            lock (_lockHoldGate)
            {
                // Re-read under the gate: an unlock landing between the check
                // and the engage would leave the desktop dark for good.
                if (!_lockHold)
                {
                    return;
                }
                EngageLockBlackout();
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[lighting-lock] re-blackout after idle failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Ramps to black on the lock ramp, or cuts and pushes directly when no
    /// render loop is painting. Shared by the lock transition and the idle
    /// window expiring, which want identical behaviour.
    /// </summary>
    private void EngageLockBlackout()
    {
        if (_engine.LoopPublishing && _engine.BeginBlackoutFade(LockFadeDuration))
        {
            ServiceLog.Info($"[lighting-lock] fading out over {(int)LockFadeDuration.TotalMilliseconds}ms");
            return;
        }

        // Nothing is painting, so there is no ramp to run and no frames
        // reaching the OpenRGB bridge either - the hold on its own would
        // leave those devices lit. The push is off-thread because it blocks
        // for as long as the bridge takes, and both callers (the lock
        // notification hop and the idle timer) are threads worth returning.
        _engine.SetBlackout(true);
        _ = Task.Run(() =>
        {
            var pushed = PushBridgeBlackout(DateTime.UtcNow + Budget);
            ServiceLog.Info($"[lighting-lock] blanked (no render loop, openrgb={pushed})");
        });
    }

    private void StopLockWakeTimer() => _lockWakeTimer.Change(Timeout.Infinite, Timeout.Infinite);

    private void SetLockInputWatch(bool enabled)
    {
        try { LockInputWatch?.Invoke(enabled); }
        catch (Exception ex)
        {
            ServiceLog.Info($"[lighting-lock] input watch {(enabled ? "arm" : "disarm")} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void BlankOut(TimeSpan budget, string reason)
    {
        try
        {
            if (!_gates.Lighting)
            {
                return;
            }
            if (!_store.Load().Lighting.SleepBlackout)
            {
                return;
            }

            var deadline = DateTime.UtcNow + budget;
            // A hold already engaged (a second suspend notification, or a
            // shutdown that follows one) is dark or on its way there; re-running
            // the fade from full brightness would light the machine back up.
            var fade = _engine.Blackout ? "held" : FadeOut(deadline);

            // The terminal state, reached on every path: the hold at level 0, so
            // every later engine tick republishes black and any device that
            // arrives mid-suspend is blanked too.
            _engine.SetBlackout(true);

            // Every non-OpenRGB writer (NP50, Lian Li, Keeb, the hubs) polls the
            // engine's device frames on its own timer, so publishing black is
            // what hands them the blank frame to push.
            var published = _engine.WaitForBlackout(Clamp(EnginePublishBudget, deadline));

            var pushed = PushBridgeBlackout(deadline);
            ServiceLog.Info(
                $"[lighting-sleep] blanked for {reason} (fade={fade}, engine={(published ? "published" : "timeout")}, openrgb={pushed})");
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[lighting-sleep] blackout on {reason} failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Call from FeatureReconciler on the Lighting ON->OFF transition, before
    /// the settings commit and before Suspend. Unlike OnSuspending/
    /// OnHostShutdown this checks neither the Lighting gate (about to flip)
    /// nor the SleepBlackout setting (a different, unrelated preference) -
    /// calling it while the gate still reads on is what lets every writer's
    /// per-tick gate check pick up and push this frame instead of dropping
    /// it, so the caller must sequence it ahead of the settings write.
    /// </summary>
    public void BlankOutForFeatureOff()
    {
        try
        {
            var deadline = DateTime.UtcNow + Budget;
            _engine.SetBlackout(true);
            var published = _engine.WaitForBlackout(Clamp(EnginePublishBudget, deadline));
            var pushed = PushBridgeBlackout(deadline);
            ServiceLog.Info(
                $"[lighting-sleep] blanked for feature-off (engine={(published ? "published" : "timeout")}, openrgb={pushed})");
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[lighting-sleep] blackout on feature-off failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Call on resume. Safe to call unconditionally - releasing a blackout that
    /// was never engaged is a no-op, which is what keeps a setting toggled off
    /// mid-sleep from stranding the user dark.
    /// </summary>
    public void OnResumed()
    {
        try
        {
            if (!_gates.Lighting)
            {
                return;
            }
            if (!_engine.Blackout)
            {
                return;
            }
            if (_lockHold)
            {
                // Woken to the lock screen: a machine that suspended while
                // locked is still locked now, so handing the lighting back here
                // would light an unattended machine and leave the later unlock
                // with nothing to release.
                ServiceLog.Info("[lighting-lock] resumed while locked - holding the blackout");
                return;
            }
            _engine.SetBlackout(false);
            ServiceLog.Info("[lighting-sleep] released blackout after resume");
        }
        catch (Exception ex)
        {
            ServiceLog.Info($"[lighting-sleep] blackout release failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Ramps every device from what it is showing down to black and waits for
    /// it to land. Only when the render loop is publishing frames: that loop is
    /// what paints the ramp and what feeds the OpenRGB bridge, so with no effect
    /// running there is nothing to ramp and the caller's blackout cuts instead -
    /// which is what shipped before the fade existed.
    ///
    /// Bounded by <paramref name="deadline"/> minus <see cref="TerminalReserve"/>
    /// whatever state it reaches; the caller blacks out unconditionally
    /// afterwards, so a short ramp costs appearance and nothing else.
    /// </summary>
    private string FadeOut(DateTime deadline)
    {
        if (!FadeSupported)
        {
            return "unsupported";
        }
        if (!_engine.LoopPublishing)
        {
            return "no-loop";
        }

        var span = FadeDuration;
        var room = deadline - TerminalReserve - DateTime.UtcNow;
        if (room < span)
        {
            span = room;
        }
        if (span <= TimeSpan.Zero)
        {
            return "no-budget";
        }
        if (!_engine.BeginBlackoutFade(span))
        {
            return "held";
        }

        var reached = _engine.WaitForBlackout(span + FrameSlack);
        return $"{(int)span.TotalMilliseconds}ms{(reached ? "" : "/timeout")}";
    }

    /// <summary>
    /// Drives the awaited OpenRGB blackout to completion or to the deadline.
    /// Returns what happened, for the log line - the caller has no recovery to
    /// perform either way.
    /// </summary>
    private string PushBridgeBlackout(DateTime deadline)
    {
        if (_bridge is null)
        {
            return "n/a";
        }
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return "no-budget";
        }
        using var cts = new CancellationTokenSource(remaining);
        try
        {
            // Blocking on purpose: the machine stops when this returns, so
            // there is no later to continue on.
            _bridge.BlackoutAsync(cts.Token).GetAwaiter().GetResult();
            return "ok";
        }
        catch (OperationCanceledException)
        {
            return "timeout";
        }
        catch (Exception ex)
        {
            return $"failed:{ex.GetType().Name}";
        }
    }

    private static TimeSpan Clamp(TimeSpan wanted, DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }
        return remaining < wanted ? remaining : wanted;
    }
}
