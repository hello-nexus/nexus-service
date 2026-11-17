using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nexus.Service.Notifications;

/// <summary>
/// Queues user-facing notifications while any reason to hold them is
/// registered, then releases them in order once the last clears.
///
/// Onboarding screens are full-viewport modals that own focus, and an alert
/// raised while a focus mode is active is either unseen or seen by an
/// audience; either way the notice is held rather than dropped, so a staged
/// update still reaches the user.
/// </summary>
public static class NotificationGate
{
    public const string ReasonOnboarding = "onboarding";
    public const string ReasonFocusMode = "focus-mode";

    // Enough for a slow first run or a long session; past that the oldest are
    // dropped rather than burying the user in a backlog.
    private const int MaxHeld = 16;

    private static readonly object Lock = new();
    private static readonly Queue<Func<Task>> Held = new();
    private static readonly HashSet<string> Reasons = new(StringComparer.Ordinal);
    private static bool _releasing;

    /// <summary>True once notifications flow straight through.</summary>
    public static bool IsOpen { get { lock (Lock) { return Reasons.Count == 0; } } }

    /// <summary>Sets the initial state from persisted onboarding status at startup.</summary>
    public static void Initialize(bool onboardingComplete)
    {
        lock (Lock)
        {
            if (onboardingComplete)
            {
                Reasons.Remove(ReasonOnboarding);
                if (Reasons.Count == 0) Held.Clear();
            }
            else
            {
                Reasons.Add(ReasonOnboarding);
            }
        }
    }

    /// <summary>Registers a reason to hold notifications. Idempotent.</summary>
    public static void Hold(string reason)
    {
        lock (Lock) { Reasons.Add(reason); }
    }

    /// <summary>
    /// Holds <paramref name="send"/> for release while any reason is
    /// registered and returns true; returns false when the caller should send
    /// it itself. Never sends: a caller that both reads false and sends would
    /// deliver twice.
    /// </summary>
    public static bool TryHold(Func<Task> send, out Task held, bool urgent = false)
    {
        held = Task.CompletedTask;
        lock (Lock)
        {
            // An urgent notice outranks a focus mode, but never the onboarding modal that owns focus.
            if (Reasons.Count == 0 || (urgent && !Reasons.Contains(ReasonOnboarding)))
            {
                return false;
            }
            if (Held.Count >= MaxHeld) Held.Dequeue();
            Held.Enqueue(send);
            return true;
        }
    }

    /// <summary>Sync wrapper for callers whose native send is not awaitable.</summary>
    public static bool TryHold(Action send, out Task held, bool urgent = false) =>
        TryHold(() => { send(); return Task.CompletedTask; }, out held, urgent);

    /// <summary>Sends now, or queues for release while any reason is held.</summary>
    public static void SendOrHold(Action send, bool urgent = false)
    {
        if (!TryHold(send, out _, urgent)) send();
    }

    /// <summary>Clears one reason and, once none remain, flushes anything held, oldest first.</summary>
    public static async Task ReleaseAsync(string reason)
    {
        List<Func<Task>> pending;
        lock (Lock)
        {
            if (!Reasons.Remove(reason)) return;
            if (Reasons.Count > 0 || _releasing) return;
            _releasing = true;
            pending = new List<Func<Task>>(Held);
            Held.Clear();
        }
        try
        {
            foreach (var send in pending)
            {
                try { await send().ConfigureAwait(false); }
                catch { /* one bad notice must not strand the rest */ }
            }
        }
        finally
        {
            lock (Lock) { _releasing = false; }
        }
    }

    /// <summary>Test hook: returns the gate to its pre-onboarding state.</summary>
    internal static void ResetForTests()
    {
        lock (Lock)
        {
            _releasing = false;
            Held.Clear();
            Reasons.Clear();
            Reasons.Add(ReasonOnboarding);
        }
    }

    internal static int HeldCount { get { lock (Lock) { return Held.Count; } } }
}
