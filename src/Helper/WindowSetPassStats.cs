using System;
using System.Globalization;
using System.Text;

namespace Nexus.Service.Helper;

/// <summary>
/// Per-pass phase timing for the window-set poller, accumulated in Stopwatch
/// ticks so the per-window cost stays a timestamp read. Only the poll thread
/// writes; the line is formatted after the pass ends.
/// </summary>
public sealed class WindowSetPassStats
{
    /// <summary>Every pass is logged for this long after helper start and after
    /// each change of the disable switches; after that only slow passes are.</summary>
    public static readonly TimeSpan LogEveryPassWindow = TimeSpan.FromMinutes(10);

    public long EnumTicks;
    public long CheapTicks;
    public long TitleTicks;
    public long FallbackTicks;
    public long DwmTicks;
    public int FallbackCount;
    public int DwmCount;
    public long MaxTicks;
    public string MaxCall = "";
    public long MaxHwnd;

    public const int MaxFallbackTargets = 5;

    /// <summary>The individual Win32 reads folded into the cheap bucket (plus
    /// the pid read for countable windows), so a log names the expensive call.</summary>
    public enum Call { Vis, Owner, Style, Rect, Pid }

    private static readonly string[] CallNames = { "vis", "owner", "style", "rect", "pid" };
    private readonly long[] _callTicks = new long[CallNames.Length];
    private readonly int[] _callCounts = new int[CallNames.Length];

    /// <summary>Books one timed read into its sub-bucket, the cheap total (pid
    /// is outside the cheap phase) and the slowest-call tracker.</summary>
    public void AddCall(Call call, long hwnd, long ticks)
    {
        _callTicks[(int)call] += ticks;
        _callCounts[(int)call]++;
        if (call != Call.Pid) CheapTicks += ticks;
        Note(CallNames[(int)call], hwnd, ticks);
    }

    public int CallCount(Call call) => _callCounts[(int)call];
    public long CallTicks(Call call) => _callTicks[(int)call];

    private void AppendCalls(StringBuilder sb, long frequency)
    {
        var inv = CultureInfo.InvariantCulture;
        sb.Append(" calls=");
        for (var i = 0; i < CallNames.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(inv, $"{CallNames[i]}:{_callCounts[i]}x{ToMs(_callTicks[i], frequency):F1}ms");
        }
    }

    public readonly record struct FallbackTarget(long Hwnd, long Ticks, int Pid, string ClassName);

    private readonly FallbackTarget[] _fallbackTargets = new FallbackTarget[MaxFallbackTargets];
    private int _fallbackTargetCount;

    /// <summary>True when a fallback of this length would make the bounded slowest list.</summary>
    public bool WouldKeepFallback(long ticks)
    {
        if (_fallbackTargetCount < MaxFallbackTargets) return true;
        foreach (var t in _fallbackTargets)
        {
            if (ticks > t.Ticks) return true;
        }
        return false;
    }

    public void AddFallback(long hwnd, long ticks, int pid, string className)
    {
        var entry = new FallbackTarget(hwnd, ticks, pid, className);
        if (_fallbackTargetCount < MaxFallbackTargets)
        {
            _fallbackTargets[_fallbackTargetCount++] = entry;
            return;
        }
        var min = 0;
        for (var i = 1; i < MaxFallbackTargets; i++)
        {
            if (_fallbackTargets[i].Ticks < _fallbackTargets[min].Ticks) min = i;
        }
        if (ticks > _fallbackTargets[min].Ticks) _fallbackTargets[min] = entry;
    }

    /// <summary>The slowest fallback calls of the pass, longest first, with the
    /// owning process named by <paramref name="processName"/>. Empty when none ran.</summary>
    public string FormatFallbackTargets(long frequency, Func<int, string> processName)
    {
        if (_fallbackTargetCount == 0) return "";
        var ordered = new FallbackTarget[_fallbackTargetCount];
        Array.Copy(_fallbackTargets, ordered, _fallbackTargetCount);
        Array.Sort(ordered, static (a, b) => b.Ticks.CompareTo(a.Ticks));
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("fallbackTargets=[");
        for (var i = 0; i < ordered.Length; i++)
        {
            var t = ordered[i];
            if (i > 0) sb.Append("; ");
            sb.Append(inv, $"{ToMs(t.Ticks, frequency):F1}ms {processName(t.Pid)} {t.ClassName} hwnd=0x{t.Hwnd:X}");
        }
        return sb.Append(']').ToString();
    }

    public void Reset()
    {
        _fallbackTargetCount = 0;
        Array.Clear(_callTicks);
        Array.Clear(_callCounts);
        EnumTicks = CheapTicks = TitleTicks = FallbackTicks = DwmTicks = 0;
        FallbackCount = DwmCount = 0;
        MaxTicks = 0;
        MaxCall = "";
        MaxHwnd = 0;
    }

    public void Note(string call, long hwnd, long ticks)
    {
        if (ticks <= MaxTicks) return;
        MaxTicks = ticks;
        MaxCall = call;
        MaxHwnd = hwnd;
    }

    public static bool LogsEveryPass(TimeSpan sinceWindowStart) => sinceWindowStart < LogEveryPassWindow;

    public static double ToMs(long ticks, long frequency) =>
        frequency <= 0 ? 0 : ticks * 1000.0 / frequency;

    /// <summary>
    /// "enum" is the whole EnumWindows call including the per-window callback,
    /// so the phases below it are parts of it and "rest" is what they leave.
    /// </summary>
    public string Format(long frequency, int trickles, long hookDestroy, long hookCloaked, long hookUncloaked, string? maxClass)
    {
        var inv = CultureInfo.InvariantCulture;
        var phases = CheapTicks + TitleTicks + FallbackTicks + DwmTicks;
        var sb = new StringBuilder();
        sb.Append(inv, $"enum={ToMs(EnumTicks, frequency):F1}ms cheap={ToMs(CheapTicks, frequency):F1}ms ")
          .Append(inv, $"title={ToMs(TitleTicks, frequency):F1}ms ")
          .Append(inv, $"fallback={FallbackCount}x{ToMs(FallbackTicks, frequency):F1}ms ")
          .Append(inv, $"dwm={DwmCount}x{ToMs(DwmTicks, frequency):F1}ms ")
          .Append(inv, $"rest={ToMs(Math.Max(0, EnumTicks - phases), frequency):F1}ms trickle={trickles} ")
          .Append(inv, $"hooks=destroy:{hookDestroy},cloaked:{hookCloaked},uncloaked:{hookUncloaked}");
        var anyCalls = false;
        foreach (var c in _callCounts) anyCalls |= c > 0;
        if (anyCalls) AppendCalls(sb, frequency);
        if (MaxTicks > 0)
        {
            sb.Append(inv, $" max={ToMs(MaxTicks, frequency):F1}ms {MaxCall} hwnd=0x{MaxHwnd:X} class={maxClass ?? "?"}");
        }
        return sb.ToString();
    }

    public static string FormatHookRate(double seconds, long destroy, long cloaked, long uncloaked) =>
        string.Create(CultureInfo.InvariantCulture,
            $"[window-cloak] events over {seconds:F0}s destroy={destroy} cloaked={cloaked} uncloaked={uncloaked}");
}
