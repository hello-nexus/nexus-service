using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Deck;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Rendering;
using Nexus.Service.Sensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Nexus.Service.Peripherals.StreamDeck;

/// <summary>
/// Dials, the touch strip, the Neo info screen and touch keys, and the
/// encoder ring LEDs (plans/streamdeck-dials.md). Everything here runs under
/// the worker's _lock, from the input threads, the tick or the animation loop.
/// </summary>
public sealed partial class StreamDeckConnectionWorker
{
    private const int DefaultDialStep = 2;
    private const int DefaultRestoreLevel = 50;
    /// <summary>Refused writes of one light's content before it is left alone until the content changes.</summary>
    private const int MaxLightWriteAttempts = 2;
    /// <summary>Light writes pause while the surface has this many consecutive write failures outstanding, which stays below the surface's drop threshold.</summary>
    private const int LightWriteFailureBudget = 3;
    /// <summary>Custom-turn ticks waiting behind a slow action, per dial; the oldest beyond this are dropped.</summary>
    private const int MaxPendingTurnTicks = 24;
    /// <summary>Turning a dial while it is held moves this many times the step.</summary>
    private const int HeldStepMultiplier = 5;
    /// <summary>Cap on custom key actions fired by one report; a fast spin reports up to +-10 ticks.</summary>
    private const int MaxCustomTicksPerReport = 12;
    /// <summary>deckBrightness never turns the deck fully dark.</summary>
    private const int MinDialDeckBrightness = 5;
    private const int FeedbackMs = 250;
    private const int FeedbackSteps = 5;
    /// <summary>A flick shorter than this (in strip pixels) is not a page swipe.</summary>
    private const int SwipeMinPixels = 20;
    private const string DefaultDialAccentHex = DeckStripRenderer.DefaultAccentHex;
    private const int DialHistoryLength = 40;
    /// <summary>Touch-key backlight when a page exists in that direction, and while pressed.</summary>
    private static readonly (byte R, byte G, byte B) TouchKeyIdle = (60, 60, 60);
    private static readonly (byte R, byte G, byte B) TouchKeyPressed = (255, 255, 255);

    private readonly IDeckDialValues? _dialValues;
    private readonly DeckStripRenderer _strip;

    /// <summary>Custom-turn ticks waiting for one queued drain; mutated under the owning dial's TurnGate.</summary>
    private sealed class TurnSegment
    {
        public int Right;
        public int Left;
        public DeckAction? RightAction;
        public DeckAction? LeftAction;
        /// <summary>The backlog warning is logged once per segment.</summary>
        public bool BacklogWarned;
    }

    private sealed class DialRuntime
    {
        public bool Down;
        public bool TurnedWhileHeld;
        public int StackIndex;
        /// <summary>Restore value for a zero toggle, per controlled target.</summary>
        public readonly Dictionary<string, double> LastNonZero = new();
        public Task Dispatch = Task.CompletedTask;
        /// <summary>Guards the pending custom-turn counters below.</summary>
        public readonly object TurnGate = new();
        /// <summary>The turn segment still accepting ticks; a push or touch closes it so later turns queue behind that action.</summary>
        public TurnSegment? OpenSegment;
        public DateTimeOffset FeedbackStartedAt;
        public bool FeedbackActive;
        public int FeedbackStep;
    }

    private sealed class DialDeckState
    {
        public DialRuntime[] Dials = Array.Empty<DialRuntime>();
        public bool[] LastDialDown = Array.Empty<bool>();
        public bool[] LastTouchKeys = Array.Empty<bool>();
        public string?[] SegmentKeys = Array.Empty<string?>();
        public string? InfoKey;
        public string?[] RingKeys = Array.Empty<string?>();
        public string?[] TouchKeyLights = Array.Empty<string?>();
        /// <summary>Consecutive refused writes of the light content last attempted, per slot.</summary>
        public int[] RingFailures = Array.Empty<int>();
        public string?[] RingFailureKeys = Array.Empty<string?>();
        public int[] TouchKeyFailures = Array.Empty<int>();
        public string?[] TouchKeyFailureKeys = Array.Empty<string?>();
        public bool[] TouchKeyHeld = Array.Empty<bool>();
        public Dictionary<string, List<float>> History = new(StringComparer.Ordinal);
        /// <summary>Cancelled when the deck's dial state is dropped, so queued dial actions stop.</summary>
        public readonly CancellationTokenSource Cancel = new();
    }

    private readonly Dictionary<string, DialDeckState> _dialStates = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _anyDialFeedback;

    private DialDeckState DialStateLocked(IStreamDeckSurface surface)
    {
        if (_dialStates.TryGetValue(surface.Serial, out var state))
        {
            return state;
        }
        var model = surface.Model;
        state = new DialDeckState
        {
            Dials = Enumerable.Range(0, model.Encoders).Select(_ => new DialRuntime()).ToArray(),
            LastDialDown = new bool[model.Encoders],
            LastTouchKeys = new bool[model.TouchKeys],
            SegmentKeys = new string?[model.Encoders],
            RingKeys = new string?[model.Encoders],
            TouchKeyLights = new string?[model.TouchKeys],
            RingFailures = new int[model.Encoders],
            RingFailureKeys = new string?[model.Encoders],
            TouchKeyFailures = new int[model.TouchKeys],
            TouchKeyFailureKeys = new string?[model.TouchKeys],
            TouchKeyHeld = new bool[model.TouchKeys],
        };
        _dialStates[surface.Serial] = state;
        return state;
    }

    private void RemoveDialStateForSerial(string serial)
    {
        if (_dialStates.Remove(serial, out var removed))
        {
            removed.Cancel.Cancel();
            removed.Cancel.Dispose();
        }
        _anyDialFeedback = _dialStates.Values.Any(s => s.Dials.Any(d => d.FeedbackActive));
    }

    private void ClearDialStatesLocked()
    {
        foreach (var state in _dialStates.Values)
        {
            state.Cancel.Cancel();
            state.Cancel.Dispose();
        }
        _dialStates.Clear();
        _anyDialFeedback = false;
    }

    /// <summary>Test seam: the shown stack entry for a dial.</summary>
    internal int DialStackIndexForTests(string serial, int dial)
    {
        lock (_lock)
        {
            return _dialStates.TryGetValue(serial, out var state) && dial < state.Dials.Length ? state.Dials[dial].StackIndex : 0;
        }
    }

    /// <summary>Drains the simulated deck's queued reports now instead of on the next tick, so a dev-route input is felt at once.</summary>
    public void DrainSimulatedInput()
    {
        lock (_lock)
        {
            PumpSimulatedInput();
        }
    }

#if DEV_TOOLS
    /// <summary>Bench hook: decodes a raw input report for a connected deck and dispatches it as if its reader had read it. False when the serial is unknown or the report does not decode.</summary>
    public bool InjectReport(string serial, ReadOnlySpan<byte> report)
    {
        lock (_lock)
        {
            foreach (var (key, surface) in _surfaces)
            {
                if (!string.Equals(surface.Serial, serial, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var input = StreamDeckProtocol.DecodeInput(report, surface.Model);
                if (input is null)
                {
                    return false;
                }
                DispatchInput(key, surface, input);
                return true;
            }
            return false;
        }
    }
#endif

    // ── Input ──

    /// <summary>Routes one decoded report by kind. A failure is logged and dropped so it cannot tear down the reader thread or leave edge state half-updated. Caller holds _lock.</summary>
    private void DispatchInput(string key, IStreamDeckSurface surface, StreamDeckInput input)
    {
        try
        {
            DispatchInputCore(key, surface, input);
        }
        catch (Exception ex)
        {
            ServiceLog.Error($"[streamdeck] input dispatch failed serial={surface.Serial} kind={input.Kind}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DispatchInputCore(string key, IStreamDeckSurface surface, StreamDeckInput input)
    {
        switch (input.Kind)
        {
            case StreamDeckInputKind.Keys:
                ProcessKeyStates(key, surface, input.Keys);
                ProcessTouchKeys(surface, input.TouchKeys);
                break;
            case StreamDeckInputKind.DialPress:
                ProcessDialPress(surface, input.DialDown);
                break;
            case StreamDeckInputKind.DialRotate:
                ProcessDialRotate(surface, input.DialTicks);
                break;
            case StreamDeckInputKind.Touch:
                ProcessTouch(surface, input);
                break;
        }
    }

    private void ProcessDialPress(IStreamDeckSurface surface, bool[] down)
    {
        var state = DialStateLocked(surface);
        _lastInputAt[surface.Serial] = _clock.GetUtcNow();
        for (var i = 0; i < state.Dials.Length && i < down.Length; i++)
        {
            if (down[i] == state.LastDialDown[i])
            {
                continue;
            }
            state.LastDialDown[i] = down[i];
            var runtime = state.Dials[i];
            ServiceLog.Info($"[streamdeck] dial {(down[i] ? "down" : "up")} serial={surface.Serial} index={i}");
            if (down[i])
            {
                WakeIfAsleep(surface);
                runtime.Down = true;
                runtime.TurnedWhileHeld = false;
                StartDialHold(surface, i);
                continue;
            }
            runtime.Down = false;
            if (TryEndHoldEdit(surface.Serial, DialHoldKey(i)))
            {
                RestoreDialVisuals(surface, i);
            }
            if (!runtime.TurnedWhileHeld)
            {
                ActivateDial(surface, i, fromTouch: false);
            }
        }
    }

    private void ProcessDialRotate(IStreamDeckSurface surface, int[] ticks)
    {
        var state = DialStateLocked(surface);
        _lastInputAt[surface.Serial] = _clock.GetUtcNow();
        var woke = false;
        for (var i = 0; i < state.Dials.Length && i < ticks.Length; i++)
        {
            if (ticks[i] == 0)
            {
                continue;
            }
            if (!woke)
            {
                WakeIfAsleep(surface);
                woke = true;
            }
            var runtime = state.Dials[i];
            if (runtime.Down)
            {
                runtime.TurnedWhileHeld = true;
            }
            if (TryEndHoldEdit(surface.Serial, DialHoldKey(i)))
            {
                RestoreDialVisuals(surface, i);
            }
            TurnDial(surface, i, ticks[i], runtime);
        }
    }

    private void TurnDial(IStreamDeckSurface surface, int dialIndex, int ticks, DialRuntime runtime)
    {
        var dials = VisibleDialsLocked(surface, out var config, out _, out _);
        var effective = EffectiveDial(dials[dialIndex], runtime);
        var action = effective?.Action;
        if (action is null)
        {
            return;
        }
        switch (action.Type)
        {
            case DeckDialTypes.Page:
                HandlePageAction(surface.Serial, config, new DeckAction { Type = "page", Op = ticks > 0 ? "next" : "prev" });
                return;
            case DeckDialTypes.Custom:
            {
                var run = ticks > 0 ? action.TurnRight : action.TurnLeft;
                RunDialTurnAction(surface.Serial, dialIndex, runtime, run, ticks > 0, Math.Min(Math.Abs(ticks), MaxCustomTicksPerReport));
                BeginFeedback(surface, dialIndex, runtime);
                return;
            }
            case DeckDialTypes.DeckBrightness:
            {
                var delta = ticks * DialStep(action) * (runtime.Down ? HeldStepMultiplier : 1);
                ApplyDeckBrightnessLocked(surface.Serial, (int)Math.Clamp(PersistedBrightness(surface.Serial) + delta, MinDialDeckBrightness, 100));
                BeginFeedback(surface, dialIndex, runtime);
                return;
            }
        }
        if (!DeckDialTypes.IsServiceValueType(action.Type) || _dialValues is null)
        {
            return;
        }
        var reading = _dialValues.Read(action);
        if (!reading.Supported || reading.Pending)
        {
            return;
        }
        var step = ticks * DialStep(action) * (runtime.Down ? HeldStepMultiplier : 1);
        var target = Math.Clamp(reading.Percent + step, 0, 100);
        if (target > 0)
        {
            runtime.LastNonZero[DeckDialValueService.TargetKey(action)] = target;
        }
        _dialValues.Write(action, target);
        BeginFeedback(surface, dialIndex, runtime);
    }

    private static double DialStep(DeckDialAction action) =>
        action.Step is { } step and > 0 && double.IsFinite(step) ? step : DefaultDialStep;

    /// <summary>The release of a dial press or a tap on its segment. A stacked dial advances on a press and acts on the shown entry for a tap.</summary>
    private void ActivateDial(IStreamDeckSurface surface, int dialIndex, bool fromTouch)
    {
        var state = DialStateLocked(surface);
        var runtime = state.Dials[dialIndex];
        var dials = VisibleDialsLocked(surface, out var config, out _, out _);
        var dial = dials[dialIndex];
        if (!fromTouch && dial?.Stack is { Count: >= 2 } stack)
        {
            runtime.StackIndex = (runtime.StackIndex + 1) % stack.Count;
            BeginFeedback(surface, dialIndex, runtime);
            return;
        }
        var action = EffectiveDial(dial, runtime)?.Action;
        if (action is null)
        {
            return;
        }
        switch (action.Type)
        {
            case DeckDialTypes.Page:
                HandlePageAction(surface.Serial, config, new DeckAction { Type = "page", Op = "goto", Target = 0 });
                return;
            case DeckDialTypes.Monitoring:
                if (action.Press is "taskManager" or "monitoringPage")
                {
                    RunDialKeyAction(surface.Serial, dialIndex, new DeckAction { Type = "monitoring", Press = action.Press }, "press");
                }
                break;
            case DeckDialTypes.Custom:
                RunDialKeyAction(surface.Serial, dialIndex, fromTouch ? action.Touch ?? action.Push : action.Push, fromTouch ? "touch" : "push");
                break;
            default:
                if (_dialValues is not null && DeckDialTypes.IsServiceValueType(action.Type))
                {
                    ToggleDialValue(action, runtime);
                }
                break;
        }
        BeginFeedback(surface, dialIndex, runtime);
    }

    private void ToggleDialValue(DeckDialAction action, DialRuntime runtime)
    {
        var reading = _dialValues!.Read(action);
        if (!reading.Supported || reading.Pending)
        {
            return;
        }
        if (DeckDialTypes.IsMuteType(action.Type))
        {
            _dialValues.SetMuted(action, !reading.Muted);
            return;
        }
        if (reading.Percent > 0)
        {
            runtime.LastNonZero[DeckDialValueService.TargetKey(action)] = reading.Percent;
            _dialValues.Write(action, 0);
        }
        else
        {
            _dialValues.Write(action, runtime.LastNonZero.TryGetValue(DeckDialValueService.TargetKey(action), out var last) && last > 0 ? last : DefaultRestoreLevel);
        }
    }

    /// <summary>Runs a custom dial's push, touch or press action the way a key press would; a page action navigates here, anything else queues behind the dial's earlier actions.</summary>
    private void RunDialKeyAction(string serial, int dialIndex, DeckAction? action, string kind)
    {
        if (action is null || string.IsNullOrEmpty(action.Type))
        {
            return;
        }
        if (action.Type == "page")
        {
            HandlePageAction(serial, LoadConfig(serial), action);
            return;
        }
        if (action.Type == "pageIndicator")
        {
            return;
        }
        var token = DialToken(serial);
        var runtime = DialStateForSerialLocked(serial)?.Dials[dialIndex];
        if (runtime is not null)
        {
            lock (runtime.TurnGate)
            {
                runtime.OpenSegment = null;
            }
        }
        QueueDialAction(serial, dialIndex, () => ExecuteDialActionAsync(serial, dialIndex, action, kind, 1, token));
    }

    /// <summary>
    /// A custom turn. Ticks wait in per-direction counters while an earlier
    /// action runs: a reverse turn cancels waiting ticks of the other
    /// direction, and beyond the cap the oldest are dropped, so a slow action
    /// holds at most one capped segment per push made behind it and never
    /// delays a push or touch.
    /// </summary>
    private void RunDialTurnAction(string serial, int dialIndex, DialRuntime runtime, DeckAction? action, bool right, int ticks)
    {
        if (action is null || string.IsNullOrEmpty(action.Type) || action.Type == "pageIndicator")
        {
            return;
        }
        if (action.Type == "page")
        {
            for (var n = 0; n < ticks; n++)
            {
                HandlePageAction(serial, LoadConfig(serial), action);
            }
            return;
        }
        TurnSegment? newSegment = null;
        lock (runtime.TurnGate)
        {
            if (runtime.OpenSegment is null)
            {
                newSegment = runtime.OpenSegment = new TurnSegment();
            }
            var segment = runtime.OpenSegment;
            if (right)
            {
                var cancelled = Math.Min(segment.Left, ticks);
                segment.Left -= cancelled;
                segment.Right += ticks - cancelled;
                segment.RightAction = action;
            }
            else
            {
                var cancelled = Math.Min(segment.Right, ticks);
                segment.Right -= cancelled;
                segment.Left += ticks - cancelled;
                segment.LeftAction = action;
            }
            if (segment.Right > MaxPendingTurnTicks || segment.Left > MaxPendingTurnTicks)
            {
                segment.Right = Math.Min(segment.Right, MaxPendingTurnTicks);
                segment.Left = Math.Min(segment.Left, MaxPendingTurnTicks);
                if (!segment.BacklogWarned)
                {
                    segment.BacklogWarned = true;
                    ServiceLog.Warn($"[streamdeck] dial {dialIndex} turn actions are backing up, dropping the oldest serial={serial}");
                }
            }
        }
        if (newSegment is not null)
        {
            var token = DialToken(serial);
            QueueDialAction(serial, dialIndex, () => DrainTurnsAsync(serial, dialIndex, runtime, newSegment, token));
        }
    }

    private CancellationToken DialToken(string serial) => DialStateForSerialLocked(serial)?.Cancel.Token ?? CancellationToken.None;

    /// <summary>Runs one segment's waiting ticks; ticks made after a push belong to a later segment and drain behind it.</summary>
    private async Task DrainTurnsAsync(string serial, int dialIndex, DialRuntime runtime, TurnSegment segment, CancellationToken token)
    {
        try
        {
            while (true)
            {
                DeckAction? action;
                int count;
                bool right;
                lock (runtime.TurnGate)
                {
                    if (token.IsCancellationRequested)
                    {
                        segment.Right = 0;
                        segment.Left = 0;
                    }
                    right = segment.Right > 0;
                    count = right ? segment.Right : segment.Left;
                    action = right ? segment.RightAction : segment.LeftAction;
                    if (count == 0 || action is null)
                    {
                        if (ReferenceEquals(runtime.OpenSegment, segment))
                        {
                            runtime.OpenSegment = null;
                        }
                        return;
                    }
                    if (right)
                    {
                        segment.Right = 0;
                    }
                    else
                    {
                        segment.Left = 0;
                    }
                }
                await ExecuteDialActionAsync(serial, dialIndex, action, right ? "right" : "left", count, token).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (runtime.TurnGate)
            {
                if (ReferenceEquals(runtime.OpenSegment, segment))
                {
                    runtime.OpenSegment = null;
                }
            }
        }
    }

    private async Task ExecuteDialActionAsync(string serial, int dialIndex, DeckAction action, string kind, int repeat, CancellationToken token)
    {
        var latchKey = $"{serial}:dial{dialIndex}:{kind}";
        for (var n = 0; n < repeat && !token.IsCancellationRequested; n++)
        {
            try
            {
                await _executor.ExecuteAsync(action, serial, -(dialIndex + 1), latchKey, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ServiceLog.Error($"[streamdeck] dial dispatch crashed serial={serial} dial={dialIndex}: {ex.Message}");
            }
        }
    }

    /// <summary>Appends work to the dial's chain so its actions run in the order they were made, never interleaved.</summary>
    private void QueueDialAction(string serial, int dialIndex, Func<Task> work)
    {
        var runtime = DialStateForSerialLocked(serial)?.Dials[dialIndex];
        if (runtime is null)
        {
            LastDispatchTask = Task.Run(work);
            return;
        }
        LastDispatchTask = runtime.Dispatch = runtime.Dispatch.ContinueWith(_ => work(), TaskScheduler.Default).Unwrap();
    }

    private DialDeckState? DialStateForSerialLocked(string serial) =>
        _dialStates.TryGetValue(serial, out var state) ? state : null;

    private void ApplyDeckBrightnessLocked(string serial, int percent)
    {
        _store.Update(s =>
        {
            if (!s.StreamDeck.Decks.TryGetValue(serial, out var deck))
            {
                deck = new PhysicalDeckSettings();
                s.StreamDeck.Decks[serial] = deck;
            }
            deck.Brightness = percent;
        });
        SetBrightnessIfAwake(serial, percent);
        BroadcastDecksChanged(serial);
    }

    private void ProcessTouchKeys(IStreamDeckSurface surface, bool[] touchKeys)
    {
        if (surface.Model.TouchKeys == 0 || touchKeys.Length == 0)
        {
            return;
        }
        var state = DialStateLocked(surface);
        for (var i = 0; i < state.LastTouchKeys.Length && i < touchKeys.Length; i++)
        {
            if (touchKeys[i] == state.LastTouchKeys[i])
            {
                continue;
            }
            state.LastTouchKeys[i] = touchKeys[i];
            state.TouchKeyHeld[i] = touchKeys[i];
            _lastInputAt[surface.Serial] = _clock.GetUtcNow();
            ServiceLog.Info($"[streamdeck] touch key {(touchKeys[i] ? "down" : "up")} serial={surface.Serial} index={i}");
            if (touchKeys[i])
            {
                WakeIfAsleep(surface);
                // Left key pages back, right key pages forward.
                SwipePage(surface, next: i == 1);
            }
            else
            {
                SyncAuxLightsLocked(surface);
            }
        }
    }

    private void ProcessTouch(IStreamDeckSurface surface, StreamDeckInput input)
    {
        var screen = surface.Model.Screen;
        _lastInputAt[surface.Serial] = _clock.GetUtcNow();
        if (screen is null || screen.Kind == StreamDeckScreenKind.InfoScreen || surface.Model.Encoders == 0)
        {
            return;
        }
        WakeIfAsleep(surface);
        var segment = Math.Clamp(input.X * surface.Model.Encoders / Math.Max(screen.Width, 1), 0, surface.Model.Encoders - 1);
        switch (input.TouchKind)
        {
            case StreamDeckTouchKind.Tap:
                ActivateDial(surface, segment, fromTouch: true);
                break;
            case StreamDeckTouchKind.Long:
            {
                var dials = VisibleDialsLocked(surface, out _, out _, out _);
                if (IsDialEmpty(dials[segment]))
                {
                    StartDialEdit(surface, segment);
                }
                else
                {
                    ActivateDial(surface, segment, fromTouch: true);
                }
                break;
            }
            case StreamDeckTouchKind.Flick:
            {
                var dx = input.X2 - input.X;
                var dy = input.Y2 - input.Y;
                if (Math.Abs(dx) >= SwipeMinPixels && Math.Abs(dx) > Math.Abs(dy))
                {
                    // Swipe left turns to the next page (Elgato's "swipe left to turn page").
                    SwipePage(surface, next: dx < 0);
                }
                break;
            }
        }
    }

    /// <summary>One page step for a swipe or a Neo touch key, in either custom or Recent Apps mode.</summary>
    private void SwipePage(IStreamDeckSurface surface, bool next)
    {
        var serial = surface.Serial;
        if (IsRecentAppsMode(serial))
        {
            var pageCount = RecentAppsPageCountLocked(serial);
            _currentPageBySerial[serial] = Math.Clamp(GetCurrentPageLocked(serial) + (next ? 1 : -1), 0, pageCount - 1);
            PushRecentAppsView(surface, viewChanged: true);
            BroadcastNav(serial, _currentPageBySerial[serial], new List<int>());
            return;
        }
        HandlePageAction(serial, LoadConfig(serial), new DeckAction { Type = "page", Op = next ? "next" : "prev" });
    }

    /// <summary>Long touch on an empty segment: records the pending edit and asks the editor to select that dial.</summary>
    private void StartDialEdit(IStreamDeckSurface surface, int dialIndex)
    {
        var serial = surface.Serial;
        if (IsRecentAppsMode(serial))
        {
            return;
        }
        var page = GetCurrentPageLocked(serial);
        var folderPath = _folderPathsBySerial.TryGetValue(serial, out var fp) ? new List<int>(fp) : new List<int>();
        var now = _clock.GetUtcNow();
        var token = now.ToUnixTimeMilliseconds();
        _pendingEdit = new DeckPendingEdit(serial, page, folderPath, 0, token, now, dialIndex);
        BroadcastEditRequest(serial, page, folderPath, 0, token, dialIndex);
        ServiceLog.Info($"[streamdeck] dial hold-to-edit fired serial={serial} page={page} dial={dialIndex}");
        LastHoldFireTask = Task.Run(() =>
        {
            try { _executor.OpenApp(); }
            catch (Exception ex) { ServiceLog.Warn($"[streamdeck] dial hold-to-edit open-app failed serial={serial}: {ex.Message}"); }
        });
    }

    // ── Hold to edit on an empty dial ──

    // Dial holds share _activeHolds with key holds; dial i is stored under the negative key -(i + 1).
    private static int DialHoldKey(int dialIndex) => -(dialIndex + 1);

    private static int DialIndexOfHoldKey(int key) => -key - 1;

    /// <summary>Test seam: true if an empty-dial hold is animating on this dial.</summary>
    internal bool HasActiveDialHold(string serial, int dialIndex)
    {
        lock (_lock)
        {
            return _activeHolds.TryGetValue(serial, out var holds) && holds.ContainsKey(DialHoldKey(dialIndex));
        }
    }

    /// <summary>The deck can show a dial hold: on the dial's own segment, or as a progress ring on a screenless deck with LED rings.</summary>
    private static bool ShowsDialHold(StreamDeckModel model) =>
        model.Encoders > 0 && (model.Screen is { Kind: not StreamDeckScreenKind.InfoScreen } || model.Screen is null && model.RingKind == StreamDeckRingKind.StudioReport);

    /// <summary>Begins the hold spinner on an empty dial pressed down; bound dials, Recent Apps mode and decks with nothing to show are left alone. Caller holds _lock.</summary>
    private void StartDialHold(IStreamDeckSurface surface, int dialIndex)
    {
        if (!ShowsDialHold(surface.Model) || IsRecentAppsMode(surface.Serial))
        {
            return;
        }
        var state = DialStateLocked(surface);
        var dials = VisibleDialsLocked(surface, out _, out _, out _);
        if (!IsDialEmpty(EffectiveDial(dials[dialIndex], state.Dials[dialIndex])))
        {
            return;
        }
        var serial = surface.Serial;
        if (!_activeHolds.TryGetValue(serial, out var holds))
        {
            holds = new Dictionary<int, HoldEditState>();
            _activeHolds[serial] = holds;
        }
        holds[DialHoldKey(dialIndex)] = new HoldEditState { StartedAt = _clock.GetUtcNow(), LastFrameIndex = 0 };
        _anyHoldActive = true;
        PushDialHoldFrame(surface, dialIndex, 0);
    }

    /// <summary>One animation frame of a dial hold. True when the hold completed and fired the editor intent.</summary>
    private bool AdvanceDialHold(IStreamDeckSurface surface, int dialIndex, HoldEditState hold, float fraction)
    {
        if (fraction >= 1f)
        {
            // The hold took over the press: nothing else fires on release.
            DialStateLocked(surface).Dials[dialIndex].TurnedWhileHeld = true;
            StartDialEdit(surface, dialIndex);
            RestoreDialVisuals(surface, dialIndex);
            return true;
        }
        var frameIndex = (int)(fraction * HoldRingSteps);
        if (frameIndex != hold.LastFrameIndex)
        {
            PushDialHoldFrame(surface, dialIndex, frameIndex);
            hold.LastFrameIndex = frameIndex;
        }
        return false;
    }

    private void PushDialHoldFrame(IStreamDeckSurface surface, int dialIndex, int frameIndex)
    {
        var model = surface.Model;
        var fraction = (float)frameIndex / HoldRingSteps;
        if (model.Screen is { Kind: not StreamDeckScreenKind.InfoScreen } screen)
        {
            var segmentWidth = screen.Width / model.Encoders;
            using var image = _strip.RenderHoldPrompt(fraction, segmentWidth, screen.Height);
            surface.SetScreenRegion(dialIndex * segmentWidth, 0, segmentWidth, screen.Height, DeckWireImageEncoder.EncodeScreen(image, model));
            return;
        }
        var ring = new byte[model.EncoderRingLeds * 3];
        var accent = ParseRgb(DefaultDialAccentHex);
        var lit = Math.Clamp((int)Math.Round(fraction * model.EncoderRingLeds), 0, model.EncoderRingLeds);
        for (var i = 0; i < model.EncoderRingLeds; i++)
        {
            var (r, g, b) = i < lit ? accent : Scale(accent, 0.08);
            ring[i * 3] = r;
            ring[i * 3 + 1] = g;
            ring[i * 3 + 2] = b;
        }
        surface.SetRing(dialIndex, ring);
    }

    /// <summary>Puts the dial's normal (empty) segment or ring back after a hold ended without a view repaint.</summary>
    private void RestoreDialVisuals(IStreamDeckSurface surface, int dialIndex)
    {
        var model = surface.Model;
        var state = DialStateLocked(surface);
        var frames = BuildDialFramesLocked(surface, sampleHistory: false);
        if (model.Screen is { Kind: not StreamDeckScreenKind.InfoScreen })
        {
            state.SegmentKeys[dialIndex] = frames[dialIndex].Input.StateKey();
            PushDialSegmentLocked(surface, dialIndex, frames[dialIndex].Input, 0f);
            return;
        }
        state.RingKeys[dialIndex] = null;
        SyncAuxLightsLocked(surface, frames);
    }

    /// <summary>Drops every dial hold of a deck whose whole view is being repainted (page change, swipe); the ring of a screenless deck is rewritten.</summary>
    private void CancelDialHolds(IStreamDeckSurface surface)
    {
        if (!_activeHolds.TryGetValue(surface.Serial, out var holds))
        {
            return;
        }
        var state = DialStateLocked(surface);
        foreach (var key in holds.Keys.Where(k => k < 0).ToList())
        {
            holds.Remove(key);
            state.RingKeys[DialIndexOfHoldKey(key)] = null;
        }
        if (holds.Count == 0)
        {
            _activeHolds.Remove(surface.Serial);
        }
        _anyHoldActive = _activeHolds.Count > 0;
    }

    // ── Dial resolution ──

    /// <summary>
    /// The dials shown for the deck's current view, one entry per encoder
    /// (null = empty). The innermost folder in the path that carries its own
    /// Dials wins; otherwise the page's. Recent Apps mode shows none.
    /// </summary>
    private DeckDial?[] VisibleDialsLocked(IStreamDeckSurface surface, out DeckConfig config, out int page, out List<int> folderPath)
    {
        var result = new DeckDial?[surface.Model.Encoders];
        folderPath = _folderPathsBySerial.TryGetValue(surface.Serial, out var fp) ? fp : new List<int>();
        if (IsRecentAppsMode(surface.Serial))
        {
            config = new DeckConfig();
            page = GetCurrentPageLocked(surface.Serial);
            return result;
        }
        config = LoadConfig(surface.Serial);
        page = ClampCurrentPageLocked(surface.Serial, config);
        var dials = config.Pages[page].Dials;
        var slots = config.Pages[page].Slots;
        foreach (var index in folderPath)
        {
            if (index < 0 || index >= slots.Count || slots[index].Folder is not { } folder)
            {
                break;
            }
            dials = folder.Dials ?? dials;
            slots = folder.Slots;
        }
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = dials is not null && i < dials.Count ? dials[i] : null;
        }
        return result;
    }

    /// <summary>The dial entry that currently acts: the shown stack entry for a stack of two or more, else the dial itself.</summary>
    private static DeckDial? EffectiveDial(DeckDial? dial, DialRuntime runtime)
    {
        if (dial?.Stack is { Count: >= 2 } stack)
        {
            runtime.StackIndex = Math.Clamp(runtime.StackIndex, 0, stack.Count - 1);
            return stack[runtime.StackIndex];
        }
        return dial;
    }

    private static bool IsDialEmpty(DeckDial? dial) =>
        dial is null || (dial.Stack is not { Count: >= 2 } && dial.Action is null);

    // ── Feedback ──

    private void BeginFeedback(IStreamDeckSurface surface, int dialIndex, DialRuntime runtime)
    {
        if (surface.Model.Screen is null || surface.Model.Encoders == 0)
        {
            RefreshDialLightsNow(surface);
            return;
        }
        runtime.FeedbackStartedAt = _clock.GetUtcNow();
        runtime.FeedbackActive = true;
        runtime.FeedbackStep = FeedbackSteps;
        _anyDialFeedback = true;
        var frames = BuildDialFramesLocked(surface, sampleHistory: false);
        PushDialSegmentLocked(surface, dialIndex, frames[dialIndex].Input, FeedbackSteps / (float)FeedbackSteps);
        SyncAuxLightsLocked(surface, frames);
    }

    private void RefreshDialLightsNow(IStreamDeckSurface surface)
    {
        if (surface.Model.RingKind != StreamDeckRingKind.None)
        {
            SyncAuxLightsLocked(surface);
        }
    }

    /// <summary>
    /// One frame of every dial's feedback fade: re-renders the segment at the
    /// intensity the clock says it is at whenever that rounds to a new step,
    /// and settles on the plain segment. Public so tests step it with a
    /// manual clock, like AnimateHolds.
    /// </summary>
    public void AnimateDialFeedback()
    {
        lock (_lock)
        {
            if (_dialStates.Count == 0)
            {
                _anyDialFeedback = false;
                return;
            }
            var now = _clock.GetUtcNow();
            foreach (var (serial, state) in _dialStates.ToList())
            {
                var surface = FindBySerialLocked(serial);
                if (surface is null || !surface.IsConnected)
                {
                    continue;
                }
                for (var i = 0; i < state.Dials.Length; i++)
                {
                    var runtime = state.Dials[i];
                    if (!runtime.FeedbackActive)
                    {
                        continue;
                    }
                    var remaining = 1.0 - (now - runtime.FeedbackStartedAt).TotalMilliseconds / FeedbackMs;
                    var step = remaining <= 0 ? 0 : (int)Math.Ceiling(remaining * FeedbackSteps);
                    if (step == runtime.FeedbackStep)
                    {
                        continue;
                    }
                    runtime.FeedbackStep = step;
                    var frames = BuildDialFramesLocked(surface, sampleHistory: false);
                    PushDialSegmentLocked(surface, i, frames[i].Input, step / (float)FeedbackSteps);
                    if (step == 0)
                    {
                        runtime.FeedbackActive = false;
                        state.SegmentKeys[i] = frames[i].Input.StateKey();
                    }
                }
            }
            _anyDialFeedback = _dialStates.Values.Any(s => s.Dials.Any(d => d.FeedbackActive));
        }
    }

    // ── Frames ──

    /// <summary>One dial's resolved look and ring state.</summary>
    private readonly record struct DialFrame(DialSegmentInput Input, double? RingFraction, bool Muted, string AccentHex, bool Bound);

    private static string DefaultDialTitle(DeckDialAction action) => action.Type switch
    {
        DeckDialTypes.Volume => "Volume",
        DeckDialTypes.MicVolume => "Microphone",
        DeckDialTypes.AppVolume => string.IsNullOrEmpty(action.AppName) ? action.AppId ?? "App" : action.AppName,
        DeckDialTypes.DisplayBrightness => "Display",
        DeckDialTypes.DeckBrightness => "Deck",
        DeckDialTypes.LightingBrightness => "Lighting",
        DeckDialTypes.Y70Brightness => "Y70",
        DeckDialTypes.Page => "Page",
        _ => "",
    };

    private static string DefaultDialIcon(DeckDialAction action, bool muted) => action.Type switch
    {
        DeckDialTypes.Volume or DeckDialTypes.AppVolume => muted ? "VolumeX" : "Volume2",
        DeckDialTypes.MicVolume => muted ? "MicOff" : "Mic",
        DeckDialTypes.DisplayBrightness or DeckDialTypes.DeckBrightness => "Sun",
        DeckDialTypes.LightingBrightness => "Lightbulb",
        DeckDialTypes.Y70Brightness => "Monitor",
        DeckDialTypes.Page => "Layers",
        DeckDialTypes.Monitoring => "Activity",
        _ => "Sliders",
    };

    /// <summary>Builds every dial's frame for the current view. sampleHistory appends one monitoring sample per monitoring dial (the tick and view pushes do; a turn does not).</summary>
    private List<DialFrame> BuildDialFramesLocked(IStreamDeckSurface surface, bool sampleHistory)
    {
        var state = DialStateLocked(surface);
        var dials = VisibleDialsLocked(surface, out var config, out var page, out _);
        var snapshot = _store.Load();
        var monitoringSources = GatherDialMonitoringSources(dials, state);
        var frames = new List<DialFrame>(dials.Length);
        for (var i = 0; i < dials.Length; i++)
        {
            var runtime = state.Dials[i];
            var dial = dials[i];
            var effective = EffectiveDial(dial, runtime);
            var stackCount = dial?.Stack is { Count: >= 2 } stack ? stack.Count : 0;
            frames.Add(BuildDialFrame(surface, i, effective, stackCount, runtime, state, config, page, snapshot, monitoringSources, sampleHistory));
        }
        return frames;
    }

    private SensorSnapshotSources? GatherDialMonitoringSources(DeckDial?[] dials, DialDeckState state)
    {
        List<string>? categories = null;
        for (var i = 0; i < dials.Length; i++)
        {
            var action = EffectiveDial(dials[i], state.Dials[i])?.Action;
            if (action is { Type: DeckDialTypes.Monitoring })
            {
                (categories ??= new List<string>()).Add(action.Category ?? "");
            }
        }
        return categories is null ? null : GatherMonitoringSources(categories);
    }

    private DialFrame BuildDialFrame(
        IStreamDeckSurface surface, int index, DeckDial? effective, int stackCount, DialRuntime runtime, DialDeckState state,
        DeckConfig config, int page, NexusSettings snapshot, SensorSnapshotSources? sources, bool sampleHistory)
    {
        var action = effective?.Action;
        if (action is null)
        {
            var emptyWithStack = stackCount >= 2;
            return new DialFrame(
                new DialSegmentInput { Kind = DialSegmentKind.Empty, StackCount = emptyWithStack ? stackCount : 0, StackIndex = runtime.StackIndex },
                null, false, DefaultDialAccentHex, Bound: emptyWithStack);
        }

        var accent = string.IsNullOrWhiteSpace(effective!.Color) ? DefaultDialAccentHex : effective.Color!;
        string Title(string fallback) => string.IsNullOrEmpty(effective.Label) ? fallback : effective.Label!;

        switch (action.Type)
        {
            case DeckDialTypes.Page:
            {
                var pageCount = IsRecentAppsMode(surface.Serial) ? RecentAppsPageCountLocked(surface.Serial) : Math.Max(config.Pages.Count, 1);
                return new DialFrame(new DialSegmentInput
                {
                    Kind = DialSegmentKind.Page,
                    Title = Title(DefaultDialTitle(action)),
                    Icon = effective.Icon,
                    IconName = DefaultDialIcon(action, false),
                    AccentHex = accent,
                    ValueText = $"{page + 1} / {pageCount}",
                    StackCount = stackCount,
                    StackIndex = runtime.StackIndex,
                }, null, false, accent, Bound: true);
            }
            case DeckDialTypes.Monitoring:
                return BuildMonitoringDialFrame(surface, index, effective, action, stackCount, runtime, state, page, snapshot, sources, sampleHistory, accent);
            case DeckDialTypes.Custom:
                return new DialFrame(new DialSegmentInput
                {
                    Kind = DialSegmentKind.Custom,
                    Title = Title(""),
                    Icon = effective.Icon,
                    IconName = DefaultDialIcon(action, false),
                    AccentHex = accent,
                    StackCount = stackCount,
                    StackIndex = runtime.StackIndex,
                }, null, false, accent, Bound: true);
        }

        DialReading reading;
        if (action.Type == DeckDialTypes.DeckBrightness)
        {
            reading = new DialReading(true, false, PersistedBrightness(surface.Serial), false);
        }
        else if (DeckDialTypes.IsServiceValueType(action.Type) && _dialValues is not null)
        {
            reading = _dialValues.Read(action);
        }
        else
        {
            reading = DialReading.Unsupported;
        }

        var usable = reading.Supported && !reading.Pending;
        var valueText = !usable
            ? "--"
            : reading.Muted && DeckDialTypes.IsMuteType(action.Type)
                ? "Muted"
                : $"{Math.Round(reading.Percent).ToString(CultureInfo.InvariantCulture)}%";
        var muted = usable && reading.Muted && DeckDialTypes.IsMuteType(action.Type);
        return new DialFrame(new DialSegmentInput
        {
            Kind = DialSegmentKind.Value,
            Title = Title(DefaultDialTitle(action)),
            Icon = effective.Icon,
            IconName = DefaultDialIcon(action, muted),
            AccentHex = accent,
            ValueText = valueText,
            Fraction = usable ? reading.Percent / 100.0 : 0,
            Muted = muted,
            StackCount = stackCount,
            StackIndex = runtime.StackIndex,
        }, usable ? reading.Percent / 100.0 : null, muted, accent, Bound: true);
    }

    private DialFrame BuildMonitoringDialFrame(
        IStreamDeckSurface surface, int index, DeckDial effective, DeckDialAction action, int stackCount, DialRuntime runtime,
        DialDeckState state, int page, NexusSettings snapshot, SensorSnapshotSources? sources, bool sampleHistory, string accent)
    {
        var sensor = SensorSnapshotResolver.ResolveOrDefault(_sensors, action.Category ?? "", action.Sensor ?? "", sources ?? default);
        var folderPath = _folderPathsBySerial.TryGetValue(surface.Serial, out var fp) ? string.Join('.', fp) : "";
        var historyKey = $"{page}:{folderPath}:{index}:{runtime.StackIndex}";
        if (!state.History.TryGetValue(historyKey, out var history))
        {
            history = new List<float>(DialHistoryLength);
            state.History[historyKey] = history;
        }
        if (sensor is not null && sampleHistory)
        {
            history.Add(sensor.Value);
            if (history.Count > DialHistoryLength)
            {
                history.RemoveAt(0);
            }
        }
        var title = !string.IsNullOrEmpty(effective.Label)
            ? effective.Label!
            : !string.IsNullOrEmpty(action.LabelText)
                ? action.LabelText!
                : sensor is null ? "" : DeckMonitoringFormat.ResolveLabel(action.Category, sensor.Name);
        var valueText = sensor is null
            ? UnresolvedSensorValueText
            : DeckMonitoringFormat.ResolveValueText(sensor, snapshot.Units.MonitoringTempUnit, snapshot.Units.NumberFormat);
        return new DialFrame(new DialSegmentInput
        {
            Kind = DialSegmentKind.Monitoring,
            Title = title,
            Icon = effective.Icon,
            IconName = DefaultDialIcon(action, false),
            AccentHex = accent,
            ValueText = valueText,
            History = history.Select(h => MathF.Round(h, 1)).ToList(),
            StackCount = stackCount,
            StackIndex = runtime.StackIndex,
        }, null, false, accent, Bound: true);
    }

    // ── Pushing ──

    /// <summary>Renders one dial's segment and writes just that region. Caller holds _lock.</summary>
    private void PushDialSegmentLocked(IStreamDeckSurface surface, int dialIndex, DialSegmentInput input, float feedback)
    {
        var screen = surface.Model.Screen;
        if (screen is null || surface.Model.Encoders == 0)
        {
            return;
        }
        var segmentWidth = screen.Width / surface.Model.Encoders;
        using var image = _strip.RenderSegment(input, segmentWidth, screen.Height, feedback);
        var wire = DeckWireImageEncoder.EncodeScreen(image, surface.Model);
        surface.SetScreenRegion(dialIndex * segmentWidth, 0, segmentWidth, screen.Height, wire);
        if (feedback <= 0f)
        {
            BroadcastDialTile(surface, dialIndex, image);
        }
    }

    private void BroadcastDialTile(IStreamDeckSurface surface, int dialIndex, Image<Rgba32> upright)
    {
        if (!_hub.TopicHasSubscribers(Sockets.PanelTopics.StreamDeckTiles))
        {
            return;
        }
        BroadcastPreviewTile(surface.Serial, GetCurrentPageLocked(surface.Serial), DialSlotPath(surface, dialIndex), RenderKit.EncodeJpeg(upright));
    }

    private string DialSlotPath(IStreamDeckSurface surface, int dialIndex)
    {
        var folderPath = _folderPathsBySerial.TryGetValue(surface.Serial, out var fp) ? fp : new List<int>();
        return folderPath.Count == 0 ? $"dial:{dialIndex}" : $"{string.Join('.', folderPath)}.dial:{dialIndex}";
    }

    /// <summary>True when an editor is listening and this tile was not broadcast since the hashes were last invalidated.</summary>
    private bool TileReplayNeeded(IStreamDeckSurface surface, string slotPath) =>
        _hub.TopicHasSubscribers(Sockets.PanelTopics.StreamDeckTiles)
        && !_tileBroadcastHash.ContainsKey($"{surface.Serial}:{GetCurrentPageLocked(surface.Serial)}:{slotPath}");

    /// <summary>
    /// A view push for the screens: the whole strip as one region write (the
    /// Plus capture's order), the Neo info screen, and the lights. Caller
    /// holds _lock; runs after the keys of the same view.
    /// </summary>
    private void PushScreens(IStreamDeckSurface surface, bool viewChanged)
    {
        var model = surface.Model;
        if (!model.HasExpandedInput || model.Screen is null && model.RingKind == StreamDeckRingKind.None && model.TouchKeys == 0)
        {
            return;
        }
        var state = DialStateLocked(surface);
        if (viewChanged)
        {
            CancelDialHolds(surface);
        }
        var frames = BuildDialFramesLocked(surface, sampleHistory: viewChanged);
        var hasSegments = model.Screen is { Kind: not StreamDeckScreenKind.InfoScreen } && model.Encoders > 0;
        if (hasSegments && viewChanged)
        {
            var screen = model.Screen!;
            using var strip = _strip.RenderStrip(frames.Select(f => f.Input).ToList(), screen.Width, screen.Height);
            surface.SetScreenRegion(0, 0, screen.Width, screen.Height, DeckWireImageEncoder.EncodeScreen(strip, model));
            var segmentWidth = screen.Width / model.Encoders;
            for (var i = 0; i < frames.Count; i++)
            {
                state.SegmentKeys[i] = frames[i].Input.StateKey();
                state.Dials[i].FeedbackActive = false;
                using var tile = strip.Clone(c => c.Crop(new Rectangle(i * segmentWidth, 0, segmentWidth, screen.Height)));
                BroadcastDialTile(surface, i, tile);
            }
        }
        else if (hasSegments)
        {
            // Same view: write only segments whose content changed; the editor still gets every tile.
            var screen = model.Screen!;
            var segmentWidth = screen.Width / model.Encoders;
            for (var i = 0; i < frames.Count; i++)
            {
                if (state.Dials[i].FeedbackActive)
                {
                    continue;
                }
                var key = frames[i].Input.StateKey();
                if (key != state.SegmentKeys[i])
                {
                    state.SegmentKeys[i] = key;
                    PushDialSegmentLocked(surface, i, frames[i].Input, 0f);
                }
                else if (TileReplayNeeded(surface, DialSlotPath(surface, i)))
                {
                    using var tile = _strip.RenderSegment(frames[i].Input, segmentWidth, screen.Height);
                    BroadcastDialTile(surface, i, tile);
                }
            }
        }
        PushInfoScreenLocked(surface, state, force: viewChanged);
        SyncAuxLightsLocked(surface, frames);
    }

    private InfoScreenInput BuildInfoScreenInput(IStreamDeckSurface surface)
    {
        var mode = _store.Load().StreamDeck.Decks.TryGetValue(surface.Serial, out var deck) && deck.InfoScreen is "clock" or "page" or "off"
            ? deck.InfoScreen
            : "clock";
        var pageCount = IsRecentAppsMode(surface.Serial) ? RecentAppsPageCountLocked(surface.Serial) : Math.Max(LoadConfig(surface.Serial).Pages.Count, 1);
        return new InfoScreenInput
        {
            Mode = mode,
            LocalTime = _clock.GetLocalNow().DateTime,
            Page = GetCurrentPageLocked(surface.Serial),
            PageCount = pageCount,
        };
    }

    private void PushInfoScreenLocked(IStreamDeckSurface surface, DialDeckState state, bool force)
    {
        if (surface.Model.Screen is not { Kind: StreamDeckScreenKind.InfoScreen } screen)
        {
            return;
        }
        var input = BuildInfoScreenInput(surface);
        var key = input.StateKey();
        var changed = force || key != state.InfoKey;
        var watched = changed ? _hub.TopicHasSubscribers(Sockets.PanelTopics.StreamDeckTiles) : TileReplayNeeded(surface, "info");
        if (!changed && !watched)
        {
            return;
        }
        state.InfoKey = key;
        using var image = _strip.RenderInfoScreen(input, screen.Width, screen.Height);
        if (changed)
        {
            surface.SetInfoScreen(DeckWireImageEncoder.EncodeScreen(image, surface.Model));
        }
        if (watched)
        {
            BroadcastPreviewTile(surface.Serial, GetCurrentPageLocked(surface.Serial), "info", RenderKit.EncodeJpeg(image));
        }
    }

    /// <summary>
    /// Once per tick: repaints a segment whose content changed (an outside
    /// volume change, a sensor sample), the info screen when its text did,
    /// and the lights. A dial mid-feedback belongs to the animation loop.
    /// </summary>
    private void RefreshDialSegments()
    {
        foreach (var surface in _surfaces.Values)
        {
            var model = surface.Model;
            if (!surface.IsConnected || !model.HasExpandedInput)
            {
                continue;
            }
            if (_asleep.TryGetValue(surface.Serial, out var asleep) && asleep)
            {
                continue;
            }
            var state = DialStateLocked(surface);
            var frames = BuildDialFramesLocked(surface, sampleHistory: true);
            if (model.Screen is { Kind: not StreamDeckScreenKind.InfoScreen } && model.Encoders > 0)
            {
                for (var i = 0; i < frames.Count; i++)
                {
                    if (state.Dials[i].FeedbackActive)
                    {
                        continue;
                    }
                    var key = frames[i].Input.StateKey();
                    if (key == state.SegmentKeys[i])
                    {
                        continue;
                    }
                    state.SegmentKeys[i] = key;
                    PushDialSegmentLocked(surface, i, frames[i].Input, 0f);
                }
            }
            PushInfoScreenLocked(surface, state, force: false);
            SyncAuxLightsLocked(surface, frames);
        }
    }

    // ── Lights: encoder rings and Neo touch-key backlights ──

    private void SyncAuxLightsLocked(IStreamDeckSurface surface) =>
        SyncAuxLightsLocked(surface, surface.Model.RingKind == StreamDeckRingKind.None ? null : BuildDialFramesLocked(surface, sampleHistory: false));

    /// <summary>Pushes ring colours and touch-key backlights when they changed; everything goes dark while the deck is asleep.</summary>
    private void SyncAuxLightsLocked(IStreamDeckSurface surface, List<DialFrame>? frames)
    {
        var model = surface.Model;
        if (model.RingKind == StreamDeckRingKind.None && model.TouchKeys == 0)
        {
            return;
        }
        var state = DialStateLocked(surface);
        var asleep = _asleep.TryGetValue(surface.Serial, out var a) && a;

        if (model.RingKind != StreamDeckRingKind.None && frames is not null)
        {
            for (var i = 0; i < model.Encoders && i < frames.Count; i++)
            {
                var ring = asleep ? new byte[model.EncoderRingLeds * 3] : BuildRing(model, frames[i]);
                var key = Convert.ToHexString(ring) + (asleep || !frames[i].Bound ? "-" : "c");
                if (key == state.RingKeys[i])
                {
                    continue;
                }
                if (surface.ConsecutiveWriteFailures >= LightWriteFailureBudget)
                {
                    continue;
                }
                // Readiness is read before the write: the settle timer may flip it in between.
                var ready = surface.IsReady;
                var written = surface.SetRing(i, ring);
                if (written && model.RingKind == StreamDeckRingKind.StudioReport)
                {
                    var (r, g, b) = asleep || !frames[i].Bound ? ((byte)0, (byte)0, (byte)0) : Scale(ParseRgb(frames[i].AccentHex), frames[i].Muted ? 0.15 : 1.0);
                    written = surface.SetCenterLed(i, r, g, b);
                }
                if (LightSettled(written, ready, key, ref state.RingFailures[i], ref state.RingFailureKeys[i]))
                {
                    state.RingKeys[i] = key;
                }
            }
        }

        if (model.TouchKeys > 0)
        {
            var page = GetCurrentPageLocked(surface.Serial);
            var pageCount = IsRecentAppsMode(surface.Serial) ? RecentAppsPageCountLocked(surface.Serial) : Math.Max(LoadConfig(surface.Serial).Pages.Count, 1);
            for (var k = 0; k < model.TouchKeys; k++)
            {
                var available = k == 0 ? page > 0 : page < pageCount - 1;
                var color = asleep || !available ? (R: (byte)0, G: (byte)0, B: (byte)0) : state.TouchKeyHeld[k] ? TouchKeyPressed : TouchKeyIdle;
                var key = $"{color.R},{color.G},{color.B}";
                if (key == state.TouchKeyLights[k])
                {
                    continue;
                }
                if (surface.ConsecutiveWriteFailures >= LightWriteFailureBudget)
                {
                    continue;
                }
                var ready = surface.IsReady;
                var written = surface.FillKey(model.KeyCount + k, color.R, color.G, color.B);
                if (LightSettled(written, ready, key, ref state.TouchKeyFailures[k], ref state.TouchKeyFailureKeys[k]))
                {
                    state.TouchKeyLights[k] = key;
                }
            }
        }
    }

    /// <summary>
    /// Whether a light's content should be recorded as handled. A write
    /// refused by a deck that was not ready is retried; a refusal by a ready
    /// deck is retried until it repeats, then left alone until the content
    /// changes. Callers also pause light writes while the surface has
    /// <see cref="LightWriteFailureBudget"/> failures outstanding, so lights
    /// alone cannot reach the surface's consecutive-failure drop.
    /// </summary>
    private static bool LightSettled(bool written, bool readyBeforeWrite, string key, ref int failures, ref string? failureKey)
    {
        if (written)
        {
            failures = 0;
            failureKey = null;
            return true;
        }
        if (!readyBeforeWrite)
        {
            return false;
        }
        if (failureKey != key)
        {
            failureKey = key;
            failures = 0;
        }
        failures++;
        return failures >= MaxLightWriteAttempts;
    }

    /// <summary>Studio: the lit fraction of the 24 LEDs in the accent over a dim track. Galleon: lit LEDs of four. Dials with no value stay dark.</summary>
    private static byte[] BuildRing(StreamDeckModel model, DialFrame frame)
    {
        var leds = model.EncoderRingLeds;
        var ring = new byte[leds * 3];
        if (!frame.Bound || frame.RingFraction is not { } fraction)
        {
            return ring;
        }
        var accent = ParseRgb(frame.AccentHex);
        var lit = frame.Muted
            ? 0
            : model.RingKind == StreamDeckRingKind.GalleonFeature
                ? (int)Math.Ceiling(fraction * leds - 0.01)
                : (int)Math.Round(fraction * leds);
        lit = Math.Clamp(lit, 0, leds);
        for (var i = 0; i < leds; i++)
        {
            var (r, g, b) = i < lit ? accent : Scale(accent, 0.08);
            ring[i * 3] = r;
            ring[i * 3 + 1] = g;
            ring[i * 3 + 2] = b;
        }
        return ring;
    }

    private static (byte R, byte G, byte B) ParseRgb(string hex)
    {
        var color = RenderKit.ParseColor(hex, SixLabors.ImageSharp.Color.ParseHex(DefaultDialAccentHex.TrimStart('#'))).ToPixel<Rgba32>();
        return (color.R, color.G, color.B);
    }

    private static (byte R, byte G, byte B) Scale((byte R, byte G, byte B) color, double factor) =>
        ((byte)(color.R * factor), (byte)(color.G * factor), (byte)(color.B * factor));

    /// <summary>Darkens every ring and touch-key backlight before the firmware logo or a disconnect. Caller holds _lock.</summary>
    private static void BlankAuxLights(IStreamDeckSurface surface)
    {
        var model = surface.Model;
        if (model.RingKind != StreamDeckRingKind.None)
        {
            var black = new byte[model.EncoderRingLeds * 3];
            for (var i = 0; i < model.Encoders; i++)
            {
                surface.SetRing(i, black);
                if (model.RingKind == StreamDeckRingKind.StudioReport)
                {
                    surface.SetCenterLed(i, 0, 0, 0);
                }
            }
        }
        for (var k = 0; k < model.TouchKeys; k++)
        {
            surface.FillKey(model.KeyCount + k, 0, 0, 0);
        }
    }
}
