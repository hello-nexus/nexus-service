using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Nexus.Service.Cooling;

/// <summary>
/// Marks every ADLX call on disk: amdadlx64.dll can abort the process inside ADLXInitialize or a later call, so a
/// start that finds the mark keeps ADLX off instead of dying on every restart. The first death latches one boot, a second
/// under the same driver key latches until the key changes, and a clean stop of a run that had ADLX enabled clears a first strike.
/// </summary>
internal static class AdlxInitGuard
{
    private const string MarkFile = "adlx-init-crashguard";
    private const string LatchFile = "adlx-init-latched";

    internal enum Latch { None, ThisBoot, UntilDriverUpdate }

    private readonly record struct State(string Key, int Strikes, string Boot);

    /// <summary>
    /// Driver key: the ADLX file version plus the highest AMD DXGI user-mode driver version (the highest, so a ghost or disabled adapter does not
    /// move it), so a driver update changes it even when the ADLX build does not. Null when either part is unavailable, which limits latching to the current boot.
    /// </summary>
    internal static string? BuildKey(string? fileVersion, IEnumerable<long> umdVersions)
    {
        var umd = umdVersions.Where(v => v != 0).DefaultIfEmpty(0).Max();
        if (string.IsNullOrWhiteSpace(fileVersion) || umd == 0) return null;
        return $"{fileVersion}#{umd}";
    }

    /// <summary>
    /// Two strikes. A run that died inside ADLX (its mark is still on disk) is a first strike: ADLX stays off for that
    /// boot only, since power loss or another crash can look the same. A second death under the same driver key keeps ADLX off until
    /// the key changes. A null <paramref name="key"/> never starts a persistent latch, never overwrites a keyed record, and never lifts a persistent one.
    /// </summary>
    public static Latch Latched(string dir, string? key, string boot)
    {
        var mark = Path.Combine(dir, MarkFile);
        var latch = Path.Combine(dir, LatchFile);
        try
        {
            var state = Read(latch, key);
            if (File.Exists(mark))
            {
                var dead = ReadMark(File.ReadAllText(mark), key);
                File.Delete(mark);
                if (dead is { } d && (d.Key.Length > 0 || state is null || state.Value.Key.Length == 0))
                {
                    var strikes = d.Key.Length > 0 && state is { } s && s.Key == d.Key ? s.Strikes + 1 : 1;
                    state = new State(d.Key, strikes, d.Boot);
                    WriteAtomic(latch, $"{state.Value.Key}\t{state.Value.Strikes}\t{state.Value.Boot}");
                }
            }
            if (state is not { } cur) return Clear(latch);
            if (key is null)
            {
                if (cur.Key.Length > 0 && cur.Strikes >= 2) return Latch.UntilDriverUpdate;
                return cur.Boot == boot ? Latch.ThisBoot : Latch.None;
            }
            if (cur.Key.Length > 0 && cur.Key != key) return Clear(latch);
            if (cur.Strikes >= 2) return Latch.UntilDriverUpdate;
            return cur.Boot == boot ? Latch.ThisBoot : Latch.None;
        }
        catch (Exception) { return Latch.None; }
    }

    // Temp file then move, so a death or power loss mid-write never leaves a torn record.
    private static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    private static Latch Clear(string latch)
    {
        File.Delete(latch);
        return Latch.None;
    }

    // Files from before the two-strike format hold "<fileVersion>@<boot>". One whose version still matches is the first strike, recorded on that earlier boot; otherwise the driver moved on.
    private static State? Legacy(string text, string? key)
    {
        var at = text.LastIndexOf('@');
        if (at < 0) return null;
        var version = text[..at];
        if (key is null) return new State("", 1, text[(at + 1)..]);
        return key.Split('#')[0] == version ? new State(key, 1, text[(at + 1)..]) : null;
    }

    private static State? Read(string latch, string? key)
    {
        if (!File.Exists(latch)) return null;
        var text = File.ReadAllText(latch).TrimEnd();
        var parts = text.Split('\t');
        if (parts.Length == 3 && int.TryParse(parts[1], out var strikes)) return new State(parts[0], strikes, parts[2]);
        return Legacy(text, key);
    }

    private static State? ReadMark(string text, string? key)
    {
        text = text.TrimEnd();
        var parts = text.Split('\t');
        if (parts.Length == 2) return new State(parts[0], 1, parts[1]);
        return Legacy(text, key);
    }

    private static volatile bool _stopped;

    /// <summary>Service stop: a stop that lands inside ADLX init, or a retry during teardown, must not read as ADLX killing the process.</summary>
    public static void Stop(string dir)
    {
        _stopped = true;
        Disarm(dir);
        // A run that had ADLX enabled and stopped cleanly proves a first strike was not ADLX. A run latched off this boot never enabled it, so it clears nothing.
        if (!_enabled) return;
        try
        {
            var latch = Path.Combine(dir, LatchFile);
            if (Read(latch, _scopeKey) is { Strikes: < 2 }) File.Delete(latch);
        }
        catch (Exception) { }
    }

    private static bool _enabled;

    private static string? _dirReady;

    public static void Arm(string dir, string? key, string boot)
    {
        if (_stopped) return;
        try
        {
            // Armed per ADLX call, so the directory is created once, not on every write.
            if (_dirReady != dir)
            {
                Directory.CreateDirectory(dir);
                _dirReady = dir;
            }
            WriteAtomic(Path.Combine(dir, MarkFile), $"{key}\t{boot}");
        }
        catch (Exception)
        {
            _dirReady = null;
        }
    }

    public static void Disarm(string dir)
    {
        try { File.Delete(Path.Combine(dir, MarkFile)); }
        catch (Exception) { }
    }

    private static string? _scopeDir;
    private static string? _scopeKey;
    private static string _scopeBoot = "";
    private static int _scopeDepth;

    private static Latch? _latchedOnce;

    /// <summary>
    /// <see cref="Latched"/> once per process. A later call would find the live mark of a running scope and
    /// turn it into a latch, so rediscovery reuses the first answer.
    /// </summary>
    public static Latch LatchedOnce(string dir, string? key, string boot) => _latchedOnce ??= Latched(dir, key, boot);

    /// <summary>Callers hold <c>AdlxGpuFans.Sync</c>, like every <see cref="Scope"/>.</summary>
    public static void Configure(string dir, string? key, string boot)
    {
        _scopeDir = dir;
        _scopeKey = key ?? "";
        _scopeBoot = boot;
        _enabled = true;
    }

    /// <summary>
    /// Marks every ADLX call after init the way init is marked: a C++ exception escaping amdadlx64.dll aborts
    /// the process, so the next start finds the mark and keeps ADLX off. Every caller holds <c>AdlxGpuFans.Sync</c>, which
    /// guards the depth counter and the configuration; nested scopes keep the outer mark until the outermost one ends.
    /// </summary>
    public static MarkScope Scope() => new();

    public readonly struct MarkScope : IDisposable
    {
        public MarkScope()
        {
            if (_scopeDir is null || _scopeKey is null) return;
            if (_scopeDepth++ == 0) Arm(_scopeDir, _scopeKey, _scopeBoot);
        }

        public void Dispose()
        {
            if (_scopeDir is null || _scopeKey is null) return;
            if (--_scopeDepth == 0) Disarm(_scopeDir);
        }
    }

    /// <summary>Test seam: a simulated death leaves static state behind, which a real death resets with the process.</summary>
    internal static void ResetScopes()
    {
        _scopeDepth = 0;
        _scopeDir = null;
        _scopeKey = null;
        _scopeBoot = "";
        _enabled = false;
        _stopped = false;
        _latchedOnce = null;
        _dirReady = null;
    }
}
