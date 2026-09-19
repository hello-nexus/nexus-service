using System;
using System.Collections.Generic;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

public interface IScreenTimeProvider
{
    FocusSession? GetCurrentSession();
    IReadOnlyList<AppUsage> GetTodayUsage();

    /// <summary>Raised when the focused app changes, from whichever mechanism
    /// the platform already uses (helper envelope, lsappinfo poll, KWin
    /// script). Consumers ride this instead of adding a second cadence.</summary>
    event Action? FocusChanged;
}

/// <summary>Foreground window details beyond FocusSession's web-facing shape
/// (app name, pid, elapsed durations): the exe path, client size, and owning
/// monitor a game session needs to resolve catalog identity and a display
/// snapshot. Windows-only (only WindowsScreenTimeProvider implements it);
/// consumers resolve it optionally via DI.</summary>
public interface IFocusDetailsProvider
{
    FocusDetails? GetCurrentFocusDetails();

    /// <summary>Raised when the current focus session ends (pid change, idle
    /// split, or helper disconnect flush) - the same boundary
    /// IScreenTimeStore.RecordSession persists, so a consumer never needs
    /// parallel session-boundary logic of its own.</summary>
    event Action<FocusSessionEnded>? SessionEnded;
}

/// <summary>Snapshot of the currently focused window. ExePath/MonitorDevice
/// are null when the helper could not resolve them (access denied, no
/// helper connected).</summary>
public sealed record FocusDetails(
    int Pid, string App, long StartedUtcMs, string? ExePath, int WinW, int WinH, string? MonitorDevice);

/// <summary>A focus session that just closed, matching the boundary
/// IScreenTimeStore.RecordSession persists for (App, StartedUtcMs, EndedUtcMs).</summary>
public sealed record FocusSessionEnded(int Pid, string App, long StartedUtcMs, long EndedUtcMs);

public interface IAppDetectionProvider
{
    IReadOnlyList<Detected> GetDetected();
    bool Kill(string id);
}

public interface IMediaProvider
{
    IReadOnlyDictionary<string, MediaSession> GetSessions();
    void Control(string source, string action);
    /// <summary>Jump to an absolute position. No-op when the session does
    /// not advertise IsSeekEnabled - callers gate on that flag.</summary>
    void Seek(string source, long positionMs);
    byte[] GetAlbumArt(string source);
}

/// <summary>
/// Implemented by media providers that learn of session changes from an
/// event source (Windows: the helper's GSMTC pushes). The media topic
/// publisher subscribes so a play/pause reaches the widget on the event
/// instead of at its next poll.
/// </summary>
public interface IMediaChangeSource
{
    event Action? Changed;
}

public interface IVolumeProvider
{
    VolumeState GetState();
    void SetVolume(double volume);
    void SetMuted(bool muted);
}

public interface IShortcutsProvider
{
    IReadOnlyList<Shortcut> GetAll();
    Shortcut? GetById(string targetId);
    byte[] GetIcon(string targetId);
    bool Launch(string targetId);

    /// <summary>Process name (no extension) the shortcut's executable runs
    /// under, for matching against the focused window. Empty when it cannot be
    /// resolved - a UWP entry has no shortcut target, and a Linux .desktop
    /// Exec line is not reliably the window's reported name. Callers fall back
    /// to display-name matching; see <c>AppPresetMatching</c>.</summary>
    string ResolveProcessName(string targetId);
}

/// <summary>Extracts a PNG icon for a running process's executable, keyed by
/// its full path (not a shortcut targetId - a live process rarely has a
/// matching Start-Menu shortcut). Empty bytes when unsupported or
/// unresolvable.</summary>
public interface IProcessIconProvider
{
    /// <summary>Null means extraction could not even be attempted (e.g. the
    /// Windows-only helper is not connected yet, or its RPC round trip
    /// timed out) - a transient condition callers must not cache as a
    /// negative result. Empty bytes means extraction ran and found no
    /// icon.</summary>
    byte[]? GetIcon(string exePath);
}

/// <summary>Kill / reveal-in-file-manager actions for a live process, driven
/// by the monitoring sidebar. Windows routes both through the user-session
/// helper so the OS enforces the console user's own privileges - the
/// LocalSystem service never acts on a process directly. macOS/Linux already
/// run in the user session, so they act directly.</summary>
public interface IProcessActionsProvider
{
    /// <summary>False when there is currently no way to perform an action
    /// (Windows: no console-user helper connected) - the route surfaces this
    /// as a 503. Always true on platforms that act directly.</summary>
    bool IsAvailable { get; }

    /// <summary>Kills every live instance of processName. Killed/Failed count
    /// individual kill attempts (failed covers e.g. access denied); both are
    /// 0 when no matching process was found or IsAvailable is false.</summary>
    Task<(int Killed, int Failed)> KillAsync(string processName);

    /// <summary>Reveals exePath in the OS file manager with the file
    /// selected. False on failure, including IsAvailable being false.</summary>
    Task<bool> OpenLocationAsync(string exePath);

    /// <summary>Brings a live process's top-level window to the foreground
    /// (Recent Apps deck mode: switch to a running app instead of relaunching
    /// it). Windows-only in practice - the helper finds the window and calls
    /// ForegroundNudge.TryForeground; other platforms return false so callers
    /// fall back to their own launch path, which already activates a running
    /// instance (e.g. macOS "open -a").</summary>
    Task<bool> ActivateWindowAsync(int pid);
}

public interface INetworkProvider
{
    /// <summary>allowOnDemandSample false suppresses a provider's on-demand
    /// synchronous scan when its background loop has not sampled yet
    /// (LinuxNetworkProvider) - a caller that polls continuously regardless
    /// of subscriber demand (ProcessAppUsageSource) must pass false, or the
    /// fallback meant for an occasional caller turns into continuous
    /// scanning every poll.</summary>
    IReadOnlyList<NetworkProcessInfo> GetSnapshot(bool allowOnDemandSample = true);
    void SetInterval(int ms);
}

/// <summary>Reports whether a live pid currently owns a visible top-level
/// window (Task-Manager-style App classification) for ProcessMonitor's
/// isApp field. LocalSystem in Session 0 cannot enumerate the interactive
/// desktop's windows, so the only real implementation is Windows-only and
/// backed by the user-session helper; ProcessMonitor treats an unregistered
/// provider (non-Windows, or before any helper connects) as "nothing is
/// windowed" rather than an error.</summary>
public interface IWindowSetProvider
{
    bool IsWindowed(int pid);
}

public interface IBeatsProvider : IDisposable
{
    /// <summary>Start capturing audio and analysing beats. Idempotent.</summary>
    void Start();

    /// <summary>Stop capturing. Idempotent.</summary>
    void Stop();

    /// <summary>Raised each analysis window (~50ms) after AudioState is refreshed.</summary>
    event Action? OnBeat;
}
