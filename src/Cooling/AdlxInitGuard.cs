using System;
using System.IO;

namespace Nexus.Service.Cooling;

/// <summary>
/// Marks ADLX init on disk: amdadlx64.dll can abort the process inside ADLXInitialize, so a
/// start that finds the mark keeps ADLX off for that key instead of dying on every restart.
/// </summary>
internal static class AdlxInitGuard
{
    private const string MarkFile = "adlx-init-crashguard";
    private const string LatchFile = "adlx-init-latched";

    /// <summary>True while ADLX stays off for <paramref name="key"/>. A mark a dead run left becomes the latch; another key clears it.</summary>
    public static bool Latched(string dir, string key)
    {
        var mark = Path.Combine(dir, MarkFile);
        var latch = Path.Combine(dir, LatchFile);
        try
        {
            if (File.Exists(mark)) File.Move(mark, latch, overwrite: true);
            if (!File.Exists(latch)) return false;
            if (File.ReadAllText(latch).Trim() == key) return true;
            File.Delete(latch);
            return false;
        }
        catch (Exception) { return false; }
    }

    private static volatile bool _stopped;

    /// <summary>Service stop: a stop that lands inside ADLX init, or a retry during teardown, must not read as ADLX killing the process.</summary>
    public static void Stop(string dir)
    {
        _stopped = true;
        Disarm(dir);
    }

    public static void Arm(string dir, string key)
    {
        if (_stopped) return;
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkFile), key);
        }
        catch (Exception) { }
    }

    public static void Disarm(string dir)
    {
        try { File.Delete(Path.Combine(dir, MarkFile)); }
        catch (Exception) { }
    }

    private static string? _scopeDir;
    private static string? _scopeKey;
    private static int _scopeDepth;

    public static void Configure(string dir, string key)
    {
        _scopeDir = dir;
        _scopeKey = key;
    }

    /// <summary>
    /// Marks every ADLX call after init the way init is marked: a C++ exception escaping amdadlx64.dll aborts
    /// the process, so the next start finds the mark and keeps ADLX off. Callers hold <c>AdlxGpuFans.Sync</c>;
    /// nested scopes keep the outer mark until the outermost one ends.
    /// </summary>
    public static MarkScope Scope() => new();

    public readonly struct MarkScope : IDisposable
    {
        public MarkScope()
        {
            if (_scopeDir is null || _scopeKey is null) return;
            if (_scopeDepth++ == 0) Arm(_scopeDir, _scopeKey);
        }

        public void Dispose()
        {
            if (_scopeDir is null || _scopeKey is null) return;
            if (--_scopeDepth == 0) Disarm(_scopeDir);
        }
    }

    /// <summary>Test seam: a simulated death leaves the depth raised, which a real death resets with the process.</summary>
    internal static void ResetScopes() => _scopeDepth = 0;
}
