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

    public void Reset()
    {
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
