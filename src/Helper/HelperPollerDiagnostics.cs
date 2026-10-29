using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Nexus.Service.Helper;

/// <summary>
/// Field-diagnostic switches for the Windows helper's periodic pollers, so a
/// user reporting a desktop stall can bisect which poller causes it without a
/// build per hypothesis. Every poller runs by default; naming one in
/// NEXUS_HELPER_DISABLE makes its tick a no-op (the object is still
/// constructed, so the request handlers that depend on it keep working).
///
/// Also raises the slow-pass log line: a tick that exceeds
/// <see cref="SlowPassMs"/> reports its duration, which is the measurement
/// that says whether a poller is stalling at all.
/// </summary>
public static class HelperPollerDiagnostics
{
    public const string EnvVarName = "NEXUS_HELPER_DISABLE";

    /// <summary>Argv flag prefix UserHelperBootstrapper forwards in place of
    /// EnvVarName - a machine environment variable change on the LocalSystem
    /// service side does not reach a process schtasks spawns in another
    /// session, but an argv value does. Same hop WindowDiagnostics takes.</summary>
    public const string HelperArgPrefix = "--helper-disable=";

    public const string WindowSet = "windowset";
    public const string ScreenTime = "screentime";
    public const string Audio = "audio";
    public const string Watchdog = "watchdog";
    public const string LockInput = "lockinput";

    public static readonly string[] Known = { WindowSet, ScreenTime, Audio, Watchdog, LockInput };

    // Each sub-switch turns off exactly one piece of the window-set pass while
    // the rest keeps running, so a field bisect can name the costly call.
    public const string WindowSetHooks = "windowset-hooks";
    public const string WindowSetDwm = "windowset-dwm";
    public const string WindowSetFallback = "windowset-fallback";
    public const string WindowSetTrickle = "windowset-trickle";
    public const string WindowSetEnum = "windowset-enum";

    /// <summary>Not a disable: restores the original read order (every cheap
    /// attribute read for every window) so the lazy order can be A/B tested.</summary>
    public const string WindowSetEager = "windowset-eager";

    public static readonly string[] SubSwitches = { WindowSetHooks, WindowSetDwm, WindowSetFallback, WindowSetTrickle, WindowSetEnum, WindowSetEager };

    /// <summary>
    /// A tick slower than this is worth a log line. Set at the rough floor of
    /// human-perceptible hitching: below it there is nothing for a user to
    /// notice, so a line would be noise. A healthy pass is single-digit ms.
    /// </summary>
    public const int SlowPassMs = 100;

    /// <summary>
    /// At most one slow-pass line per poller per minute. A genuinely stuck
    /// poller trips on every tick, and a line every few seconds for hours is
    /// log spam that buries the rest of the file; the suppressed count carries
    /// the frequency instead.
    /// </summary>
    private static readonly TimeSpan SlowPassLogInterval = TimeSpan.FromSeconds(60);
    private static readonly Dictionary<string, (long NextTicks, int Suppressed)> s_slowPassGate =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Live switch file, re-read on a short interval so the reporter can turn a
    /// poller off and back on by saving a text file - no admin, no service
    /// restart, no killing the helper (it holds a session mutex, so a restart
    /// alone would leave the old un-switched helper polling and the test would
    /// silently prove nothing). It sits in the logs directory because that is
    /// the one path the user-session helper is known to write, and it lands
    /// beside the logs the reporter collects anyway.
    /// </summary>
    public const string DisableFileName = "helper-disable.txt";

    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(2);
    private static readonly object s_gate = new();
    private static HashSet<string> s_disabled = new(StringComparer.OrdinalIgnoreCase);
    private static long s_nextCheckTicks;
    private static string? s_filePath;
    private static bool s_fromEnv;

    /// <summary>
    /// Where a switch-set change is reported. Injected because this class is
    /// platform-neutral while the helper's only working log sink (HelperLog ->
    /// nexus-helper.log) is Windows-only. ServiceLog is NOT usable here: the
    /// helper short-circuits in CommandLineEntry.TryEarlyExit long before
    /// Program.cs calls ServiceLog.Initialize, so its writer is null and every
    /// line is dropped.
    /// </summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Comma- or space-separated poller names, case-insensitive. An unrecognized
    /// name is kept rather than dropped so a typo shows up verbatim in the
    /// startup log line instead of reading as "nothing was disabled". Tokens
    /// carrying anything outside [A-Za-z0-9_-] are dropped entirely: the parsed
    /// list is re-emitted into the scheduled-task XML UserHelperBootstrapper
    /// writes as LocalSystem, so it must not carry markup.
    /// </summary>
    public static HashSet<string> Parse(string? value)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value)) return set;
        foreach (var part in value.Split(new[] { ',', ' ', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part.Trim();
            if (name.Length > 0 && IsSafeToken(name)) set.Add(name);
        }
        return set;
    }

    private static bool IsSafeToken(string token)
    {
        foreach (var c in token)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                  || (c >= '0' && c <= '9') || c == '_' || c == '-';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// Called once from WindowsUserHelper.Run, before any poller is constructed.
    /// A non-empty argv/env value pins the set for the process lifetime; otherwise
    /// the switch file owns it and is re-read as the pollers tick.
    /// </summary>
    public static void Configure(string? value, string? filePath)
    {
        lock (s_gate)
        {
            s_filePath = filePath;
            var pinned = Parse(value);
            s_fromEnv = pinned.Count > 0;
            s_disabled = s_fromEnv ? pinned : ReadFile(filePath);
            s_nextCheckTicks = Environment.TickCount64 + (long)RecheckInterval.TotalMilliseconds;
            Interlocked.Increment(ref s_changeCount);
        }
    }

    private static int s_changeCount;

    /// <summary>Bumped whenever the effective switch set changes (including the
    /// initial Configure), so a consumer can restart its own per-change window.</summary>
    public static int ChangeCount => Volatile.Read(ref s_changeCount);

    public static bool IsDisabled(string poller)
    {
        RefreshIfDue();
        return Volatile.Read(ref s_disabled).Contains(poller);
    }

    /// <summary>
    /// Cheap enough to sit on every poller tick: a File.Exists plus a short read,
    /// at most once per RecheckInterval across all callers.
    /// </summary>
    private static void RefreshIfDue()
    {
        if (s_fromEnv) return;
        if (Environment.TickCount64 < Volatile.Read(ref s_nextCheckTicks)) return;

        lock (s_gate)
        {
            if (s_fromEnv || Environment.TickCount64 < s_nextCheckTicks) return;
            s_nextCheckTicks = Environment.TickCount64 + (long)RecheckInterval.TotalMilliseconds;

            var next = ReadFile(s_filePath);
            if (next.SetEquals(s_disabled)) return;
            s_disabled = next;
            Interlocked.Increment(ref s_changeCount);
            Log?.Invoke(FormatStartupLine(next));
        }
    }

    /// <summary>Missing or unreadable file means every poller runs - the default.</summary>
    private static HashSet<string> ReadFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Mid-save read, or the file locked by the editor: keep the current
            // set rather than flipping every poller back on for one tick.
            return s_disabled;
        }
    }

    /// <summary>
    /// One line at helper start so a collected log is unambiguous about what was
    /// off. Names that match no poller are called out separately - a silent
    /// typo would otherwise read as a poller that was ruled out and was not.
    /// </summary>
    public static string FormatStartupLine(HashSet<string> disabled)
    {
        if (disabled.Count == 0) return "[helper-perf] all pollers enabled";

        var recognized = new List<string>();
        var unrecognized = new List<string>();
        foreach (var name in disabled)
        {
            (Array.Exists(Known, k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                || Array.Exists(SubSwitches, k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                ? recognized
                : unrecognized).Add(name);
        }

        var line = recognized.Count > 0
            ? $"[helper-perf] disabled: {string.Join(",", recognized)}"
            : "[helper-perf] all pollers enabled";
        if (unrecognized.Count > 0)
        {
            line += $" (unrecognized, ignored: {string.Join(",", unrecognized)}; known: {string.Join(",", Known)}; sub-switches: {string.Join(",", SubSwitches)})";
        }
        return line;
    }

    public static string FormatSlowPass(string poller, double elapsedMs, int items, string? detail = null) =>
        $"[helper-perf] {poller} pass={elapsedMs:F1}ms items={items}" + (detail is null ? "" : " " + detail);

    /// <summary>
    /// Rate-limited slow-pass line. Returns false when this poller already
    /// reported inside the last <see cref="SlowPassLogInterval"/>; the
    /// occurrence is counted and reported on the next line that does emit.
    /// Callers on several poller threads, so the gate is locked.
    /// </summary>
    public static bool TryFormatSlowPass(string poller, double elapsedMs, int items, out string line, string? detail = null)
    {
        int suppressed;
        lock (s_gate)
        {
            var now = Environment.TickCount64;
            if (s_slowPassGate.TryGetValue(poller, out var gate) && now < gate.NextTicks)
            {
                s_slowPassGate[poller] = (gate.NextTicks, gate.Suppressed + 1);
                line = "";
                return false;
            }

            suppressed = gate.Suppressed;
            s_slowPassGate[poller] = (now + (long)SlowPassLogInterval.TotalMilliseconds, 0);
        }

        line = FormatSlowPass(poller, elapsedMs, items, detail);
        if (suppressed > 0) line += $" (+{suppressed} more in the last {SlowPassLogInterval.TotalSeconds:F0}s)";
        return true;
    }

    /// <summary>Exposed for the startup line; Configure already stored it.</summary>
    public static HashSet<string> Current => Volatile.Read(ref s_disabled);
}
