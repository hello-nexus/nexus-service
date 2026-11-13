using System;
using System.Threading;

namespace Nexus.Service.Peripherals.Nzxt;

/// <summary>Who asked for an NZXT fan duty write, as it appears in the diagnostic log.</summary>
internal static class NzxtFanCallers
{
    public const string CurveEngine = "curve engine";
    public const string Reassert = "reassert";
    public const string Manual = "manual";
    public const string ThermalGuard = "thermal guard";
    public const string Restore = "restore";
    public const string Other = "other";
}

/// <summary>
/// Diagnostic state shared by the fan hub, its cooling provider and the curve engine: a time
/// window in which every duty write and status change is logged, and the caller tag a write
/// carries. The tag flows with the async context, so it survives the composite provider's
/// Task.Run hop.
/// </summary>
internal static class NzxtFanDiagnostics
{
    private const long WindowMs = 120_000;

    private static long _windowUntilMs;
    private static readonly AsyncLocal<string?> CallerTag = new();

    public static bool InWindow => Environment.TickCount64 < Volatile.Read(ref _windowUntilMs);

    public static void OpenWindow() => Volatile.Write(ref _windowUntilMs, Environment.TickCount64 + WindowMs);

    public static string Caller => CallerTag.Value ?? NzxtFanCallers.Other;

    public static CallerScope BeginCaller(string caller)
    {
        var previous = CallerTag.Value;
        CallerTag.Value = caller;
        return new CallerScope(previous);
    }

    public readonly struct CallerScope : IDisposable
    {
        private readonly string? _previous;

        public CallerScope(string? previous) => _previous = previous;

        public void Dispose() => CallerTag.Value = _previous;
    }
}
