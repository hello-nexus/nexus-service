using System;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>What a forced GPU init failure simulates.</summary>
internal enum GpuForceMode
{
    None,

    /// <summary>Init throws on the GL thread: the shape a WGL failure takes.</summary>
    Fail,

    /// <summary>Init never returns: the shape a wedged driver takes.</summary>
    Hang,

    /// <summary>The process dies during init, after the probe child has printed
    /// its GLINIT_ENTER breadcrumb.</summary>
    Crash,

    /// <summary>The probe child dies BEFORE printing GLINIT_ENTER, standing in
    /// for a crash unrelated to the card (a missing DLL, an AV kill).</summary>
    CrashEarly,

    /// <summary>Asks WGL for a context version no driver can serve, so a real
    /// driver error runs through the real failure path on healthy hardware.</summary>
    ContextError,
}

/// <summary>
/// Env-var seams for exercising the GPU init failure paths on hardware that
/// cannot reproduce them (every lab box has working WGL). Compiled only into a
/// DEV_TOOLS build: a release publish contains none of these names, so a stray
/// machine-wide variable cannot turn a customer's lighting off.
/// </summary>
internal static class GpuTestSeam
{
#if DEV_TOOLS
    private const string ForceFailVar = "NEXUS_GPU_FORCE_FAIL";
    private const string ForceAdaptersVar = "NEXUS_GPU_FORCE_ADAPTERS";
    private const string ReprobeMinutesVar = "NEXUS_GPU_REPROBE_MINUTES";
    private const string FailFirstVar = "NEXUS_GPU_FAIL_FIRST_N";

    // Attempts already failed under FailFirstVar, and the probe child's opt-out.
    private static int _forcedFailures;
    private static bool _intermittentIgnored;

    public static GpuForceMode Mode => ParseMode(Environment.GetEnvironmentVariable(ForceFailVar));

    public static int? AdapterCount => ParseCount(Environment.GetEnvironmentVariable(ForceAdaptersVar));

    public static TimeSpan? ReprobeOverride =>
        ParseCount(Environment.GetEnvironmentVariable(ReprobeMinutesVar)) is int m
            ? TimeSpan.FromMinutes(m)
            : null;

    /// <summary>The probe child answers "does this card work"; the intermittent
    /// seam targets the service's own retry budget, so the child opts out or a
    /// failed probe would latch the GPU off before any retry runs.</summary>
    public static void IgnoreIntermittentFailures() => _intermittentIgnored = true;

    /// <summary>True while the intermittent seam still owes a failure. Each true
    /// answer consumes one, so attempt N+1 lands.</summary>
    public static bool ConsumeIntermittentFailure()
    {
        var budget = ParseCount(Environment.GetEnvironmentVariable(FailFirstVar)) ?? 0;
        if (budget <= 0 || System.Threading.Volatile.Read(ref _intermittentIgnored))
        {
            return false;
        }
        return System.Threading.Interlocked.Increment(ref _forcedFailures) <= budget;
    }

    /// <summary>One line for the startup log so a seam left set on a lab box is
    /// visible in a capture. Empty when nothing is set.</summary>
    public static string Describe()
    {
        var parts = new System.Collections.Generic.List<string>();
        foreach (var name in new[] { ForceFailVar, ForceAdaptersVar, ReprobeMinutesVar, FailFirstVar })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add($"{name}={value}");
            }
        }
        return string.Join(" ", parts);
    }
#else
    public static GpuForceMode Mode => GpuForceMode.None;
    public static int? AdapterCount => null;
    public static TimeSpan? ReprobeOverride => null;
    public static void IgnoreIntermittentFailures() { }
    public static bool ConsumeIntermittentFailure() => false;
    public static string Describe() => string.Empty;
#endif

    public static GpuForceMode ParseMode(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "fail" => GpuForceMode.Fail,
        "hang" => GpuForceMode.Hang,
        "crash" => GpuForceMode.Crash,
        "crash-early" => GpuForceMode.CrashEarly,
        "context-error" => GpuForceMode.ContextError,
        _ => GpuForceMode.None,
    };

    /// <summary>Non-negative integer, or null when unset or unparseable.</summary>
    public static int? ParseCount(string? value) =>
        int.TryParse((value ?? string.Empty).Trim(), out var n) && n >= 0 ? n : null;
}
