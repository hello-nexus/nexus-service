using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Activity;
using Nexus.Service.Lighting;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.FocusModes;

/// <summary>One game keeping the game trigger firing; StartTimeUtc disambiguates a reused pid, null when it could not be read.</summary>
public sealed record TrackedGame(
    string GameKey, string Name, int Pid, long StartedUtcMs, DateTime? StartTimeUtc);

/// <summary>
/// Resolves which focus mode, if any, is currently active.
///
/// At most one at a time: a manually picked mode wins, otherwise the first
/// mode in settings order whose trigger is firing, so list order is the user's
/// precedence control between (say) a game and a stream.
///
/// The game trigger tracks the game's PROCESS, not the fps focus session that
/// reports it: that session ends on the first alt-tab, so following it would
/// toggle every effect each time the player switches away.
///
/// The app trigger watches the process list instead: a mode fires while any of
/// its chosen apps is running, foreground or not, after the same activation
/// delay the game trigger uses.
/// </summary>
public sealed class FocusModeState : BackgroundService
{
    /// <summary>How long a candidate process must stay alive before it fires the
    /// game trigger, filtering a sibling exe that only flashes focus. GameCatalog
    /// resolves by install-dir prefix, so a game's launcher, updater or shutdown
    /// handler resolves to the same game and can re-arm the mode after a quit;
    /// a sibling outliving this window is a resolution problem to fix there, not
    /// a reason to make entry lag the game.</summary>
    private static readonly TimeSpan DefaultActivationDelay = TimeSpan.FromSeconds(3);

    /// <summary>Ceiling on one auto activation, so a trigger that never clears
    /// cannot silence notifications and egress forever. Manual is uncapped.</summary>
    private static readonly TimeSpan MaxAutoActive = TimeSpan.FromHours(12);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private readonly IConfigStore _config;
    private readonly TimeSpan _activationDelay;
    private readonly Func<TrackedGame, bool> _isRunning;
    private readonly ProcessMonitor? _processes;
    private readonly Func<IReadOnlyList<ProcessInfo>>? _listProcesses;

    private readonly object _lock = new();
    private readonly Dictionary<int, TrackedGame> _games = new();
    private readonly Dictionary<int, TrackedGame> _pending = new();
    private readonly Dictionary<string, long> _graceUntilUtcMs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _wasFiring = new(StringComparer.Ordinal);
    // App-trigger modes whose apps are running and have outlived the activation delay, and when each first matched.
    private readonly HashSet<string> _appRunning = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _appSeenSinceMs = new(StringComparer.Ordinal);
    private bool _appDemand;

    private const string AppDemandSource = "focus-app";

    private bool _obsLive;
    private string? _manualModeId;
    // Set when the user picks "Off" while a mode was auto-active: that mode
    // stays out until its trigger stops firing, so "not now" does not mean
    // "never again" and does not fight a still-running game.
    private readonly HashSet<string> _suppressedModeIds = new(StringComparer.Ordinal);
    private string? _activeModeId;
    private string _reason = "";
    private long _activatedUtcMs;

    public FocusModeState(IConfigStore config, ProcessMonitor processes)
        : this(config, DefaultActivationDelay, null, () => processes.GetProcesses())
    {
        _processes = processes;
    }

    internal FocusModeState(
        IConfigStore config, TimeSpan activationDelay, Func<TrackedGame, bool>? isRunning = null,
        Func<IReadOnlyList<ProcessInfo>>? listProcesses = null)
    {
        _config = config;
        _activationDelay = activationDelay;
        _isRunning = isRunning ?? IsStillRunning;
        _listProcesses = listProcesses;
    }

    /// <summary>Raised outside the state lock whenever the active mode changes; null means nothing is active.</summary>
    public event Action<FocusModeSettings?>? ActiveChanged;

    public bool IsActive { get { lock (_lock) { return _activeModeId is not null; } } }

    public string? ActiveModeId { get { lock (_lock) { return _activeModeId; } } }

    /// <summary>"auto", "manual", or "" while inactive.</summary>
    public string Reason { get { lock (_lock) { return _reason; } } }

    public long ActivatedUtcMs { get { lock (_lock) { return _activatedUtcMs; } } }

    public IReadOnlyList<TrackedGame> Games
    {
        get { lock (_lock) { return _games.Values.OrderBy(g => g.StartedUtcMs).ToList(); } }
    }

    /// <summary>Records a running game; re-noting a tracked pid keeps the original stamp, since a session opens again on every alt-tab back.</summary>
    public void NoteGameStarted(string gameKey, string name, int pid)
    {
        if (pid <= 0) return;

        var startTime = TryReadProcessStartUtc(pid);
        lock (_lock)
        {
            if (_games.ContainsKey(pid) || _pending.ContainsKey(pid)) return;
            _pending[pid] = new TrackedGame(
                gameKey, name, pid, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), startTime);
        }
    }

    /// <summary>Reports whether OBS is streaming or recording; drives the obs trigger.</summary>
    public void SetObsLive(bool live)
    {
        lock (_lock)
        {
            if (_obsLive == live) return;
            _obsLive = live;
        }
        Reevaluate();
    }

    /// <summary>Activates one mode by id until the user turns it off.</summary>
    public void ActivateManually(string modeId)
    {
        lock (_lock)
        {
            _manualModeId = modeId;
            _suppressedModeIds.Clear();
        }
        Reevaluate();
    }

    /// <summary>The top bar's "Off": drops a manual pick, and holds an auto-active mode out until its trigger stops firing.</summary>
    public void TurnOff()
    {
        lock (_lock)
        {
            _manualModeId = null;
            // Suppress whatever is active however it got there: a manual pick
            // sitting on top of a firing trigger would otherwise re-activate on
            // this same pass, so Off would read as a no-op. A set, not one slot,
            // or turning a second mode off would un-suppress the first.
            if (_activeModeId is not null) _suppressedModeIds.Add(_activeModeId);
        }
        Reevaluate();
    }

    /// <summary>Re-runs the effect handlers against the current mode, so an effect toggled mid-session takes hold now; handlers apply a desired state rather than acting on the edge.</summary>
    public void ReapplyEffects()
    {
        Reevaluate();
        var mode = ActiveMode();
        if (mode is not null) ActiveChanged?.Invoke(mode);
    }

    /// <summary>The active mode's settings, or null when nothing is active.</summary>
    public FocusModeSettings? ActiveMode()
    {
        string? id;
        lock (_lock) { id = _activeModeId; }
        if (id is null) return null;
        return LoadSettings().Modes.FirstOrDefault(m => m.Id == id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { Sweep(); }
                catch (Exception ex) { ServiceLog.Warn($"[focus] sweep failed: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void Sweep()
    {
        var settings = LoadSettings();
        var appModes = settings.Modes.Where(m => m.Trigger == FocusTriggers.App && m.Apps.Count > 0).ToList();
        SetAppDemand(appModes.Count > 0);
        var processes = appModes.Count > 0 ? ReadProcesses() : null;

        lock (_lock)
        {
            PromotePendingLocked();
            if (processes is not null || _appRunning.Count > 0 || _appSeenSinceMs.Count > 0)
            {
                UpdateAppTriggersLocked(appModes, processes);
            }

            List<int>? dead = null;
            foreach (var game in _games.Values)
            {
                if (!_isRunning(game)) (dead ??= new List<int>()).Add(game.Pid);
            }
            if (dead is not null)
            {
                foreach (var pid in dead) _games.Remove(pid);
            }
        }

        Reevaluate();
    }

    private void SetAppDemand(bool wanted)
    {
        if (_appDemand == wanted) return;
        _appDemand = wanted;
        _processes?.SetDemand(AppDemandSource, wanted);
    }

    private IReadOnlyList<ProcessInfo>? ReadProcesses()
    {
        try { return _listProcesses?.Invoke(); }
        catch { return null; }
    }

    /// <summary>
    /// Running, not focused: a mode's app trigger is raw-true once any of its apps
    /// has been in the process list for the activation delay, and stays true until
    /// none is. A null list (no source, or the read failed) keeps the last answer
    /// rather than reading as "everything quit".
    /// </summary>
    private void UpdateAppTriggersLocked(List<FocusModeSettings> appModes, IReadOnlyList<ProcessInfo>? processes)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mode in appModes) live.Add(mode.Id);

        foreach (var id in _appSeenSinceMs.Keys.Where(id => !live.Contains(id)).ToList()) _appSeenSinceMs.Remove(id);
        _appRunning.RemoveWhere(id => !live.Contains(id));
        if (processes is null) return;

        foreach (var mode in appModes)
        {
            if (!AnyAppRunning(mode, processes))
            {
                _appSeenSinceMs.Remove(mode.Id);
                _appRunning.Remove(mode.Id);
                continue;
            }
            if (!_appSeenSinceMs.TryGetValue(mode.Id, out var since))
            {
                since = nowMs;
                _appSeenSinceMs[mode.Id] = since;
            }
            if (nowMs - since >= (long)_activationDelay.TotalMilliseconds) _appRunning.Add(mode.Id);
        }
    }

    private static bool AnyAppRunning(FocusModeSettings mode, IReadOnlyList<ProcessInfo> processes)
    {
        // Exact process key only. The display-name fallback of the lighting
        // matcher is built for a single focused window; against the whole
        // running list a background helper would match and hold the mode on.
        // PATCH never stores a binding without a processName, so nothing needs the fallback.
        foreach (var app in mode.Apps)
        {
            var key = AppPresetMatching.ProcessKey(app.ProcessName);
            if (key.Length == 0) continue;
            foreach (var proc in processes)
            {
                if (AppPresetMatching.ProcessKey(proc.Name) == key) return true;
            }
        }
        return false;
    }

    /// <summary>Moves candidates that have outlived the activation delay into the tracked set and drops the ones that died first.</summary>
    private void PromotePendingLocked()
    {
        if (_pending.Count == 0) return;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        List<int>? settled = null;
        foreach (var candidate in _pending.Values)
        {
            if (!_isRunning(candidate))
            {
                (settled ??= new List<int>()).Add(candidate.Pid);
                continue;
            }
            if (nowMs - candidate.StartedUtcMs < (long)_activationDelay.TotalMilliseconds) continue;

            (settled ??= new List<int>()).Add(candidate.Pid);
            _games[candidate.Pid] = candidate;
        }

        if (settled is not null)
        {
            foreach (var pid in settled) _pending.Remove(pid);
        }
    }

    /// <summary>Picks the active mode and fires <see cref="ActiveChanged"/> outside the lock; only a change raises it.</summary>
    private void Reevaluate()
    {
        var settings = LoadSettings();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        FocusModeSettings? became = null;
        var changed = false;

        lock (_lock)
        {
            UpdateGraceWindowsLocked(settings, nowMs);

            FocusModeSettings? chosen = null;
            var reason = "";

            if (_manualModeId is not null)
            {
                chosen = settings.Modes.FirstOrDefault(m => m.Id == _manualModeId);
                if (chosen is null) _manualModeId = null;
                else reason = "manual";
            }

            if (chosen is null)
            {
                foreach (var mode in settings.Modes)
                {
                    if (_suppressedModeIds.Contains(mode.Id)) continue;
                    if (!IsFiringLocked(mode, nowMs)) continue;
                    chosen = mode;
                    reason = "auto";
                    break;
                }
            }

            if (chosen is not null && reason == "auto" && chosen.Id == _activeModeId
                && _activatedUtcMs > 0 && nowMs - _activatedUtcMs > (long)MaxAutoActive.TotalMilliseconds)
            {
                ServiceLog.Warn($"[focus] '{chosen.Name}' exceeded {MaxAutoActive.TotalHours:F0}h, forcing exit");
                // Suppress rather than clear the tracked set: clearing makes the
                // trigger read as gone, which opens a grace window and lets the
                // next promotion restart the ceiling. Suppression holds until the
                // trigger really stops firing.
                _suppressedModeIds.Add(chosen.Id);
                chosen = null;
                reason = "";
            }

            if (chosen?.Id != _activeModeId)
            {
                _activeModeId = chosen?.Id;
                _activatedUtcMs = chosen is null ? 0 : nowMs;
                became = chosen;
                changed = true;
            }
            _reason = chosen is null ? "" : reason;
        }

        if (changed)
        {
            ServiceLog.Info(became is null
                ? "[focus] inactive"
                : $"[focus] active: {became.Name} ({Reason}){FormatGames()}");
            ActiveChanged?.Invoke(became);
        }
    }

    /// <summary>Opens a mode's grace window on the tick its raw trigger stops firing, and clears a suppression once the trigger it was hiding from is gone.</summary>
    private void UpdateGraceWindowsLocked(FocusSettings settings, long nowMs)
    {
        foreach (var mode in settings.Modes)
        {
            var raw = IsTriggerRawLocked(mode);
            if (raw)
            {
                _wasFiring.Add(mode.Id);
                _graceUntilUtcMs.Remove(mode.Id);
            }
            else if (_wasFiring.Remove(mode.Id))
            {
                _graceUntilUtcMs[mode.Id] = nowMs + Math.Max(0, mode.ExitGraceSeconds) * 1000L;
            }

            if (!raw) _suppressedModeIds.Remove(mode.Id);
        }
    }

    private bool IsFiringLocked(FocusModeSettings mode, long nowMs)
    {
        if (IsTriggerRawLocked(mode)) return true;
        return _graceUntilUtcMs.TryGetValue(mode.Id, out var until) && nowMs < until;
    }

    private bool IsTriggerRawLocked(FocusModeSettings mode) => mode.Trigger switch
    {
        FocusTriggers.Game => _games.Count > 0,
        FocusTriggers.Obs => _obsLive,
        FocusTriggers.App => _appRunning.Contains(mode.Id),
        _ => false,
    };

    private string FormatGames()
    {
        var games = Games;
        return games.Count == 0 ? "" : ": " + string.Join(", ", games.Select(g => $"{g.Name} (pid {g.Pid})"));
    }

    private static bool IsStillRunning(TrackedGame game)
    {
        try
        {
            using var proc = Process.GetProcessById(game.Pid);
            if (proc.HasExited) return false;
            if (game.StartTimeUtc is null) return true;
            // A recycled pid is a different process with a later start time.
            return Math.Abs((proc.StartTime.ToUniversalTime() - game.StartTimeUtc.Value).TotalSeconds) < 1;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            // Only ArgumentException means "no such pid"; a process we cannot open is still alive.
            return true;
        }
    }

    private static DateTime? TryReadProcessStartUtc(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private FocusSettings LoadSettings()
    {
        try { return _config.Load().Focus ?? new FocusSettings(); }
        catch { return new FocusSettings(); }
    }

    /// <summary>Test hook: drives one sweep without waiting for the timer.</summary>
    internal void SweepForTests() => Sweep();
}
