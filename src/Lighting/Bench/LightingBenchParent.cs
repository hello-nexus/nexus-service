#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Platform;
using Nexus.Service.Sensors;

namespace Nexus.Service.Lighting.Bench;

/// <summary>
/// Developer lighting render benchmark. <c>--lighting-bench &lt;out.json&gt;</c> runs one child
/// process per (backend, adapter) so driver state never carries over, then merges the results
/// and prints a table. Touches no settings or devices. The only state it changes is the per-exe
/// DirectX GpuPreference value, which steers WGL, and that is restored after each child and
/// journaled so a killed run is undone by the next.
/// </summary>
internal static partial class LightingBench
{
    private const string PrefKeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string SystemSid = "S-1-5-18";

    public static int RunParent(string[] args)
    {
        var error = BenchOptions.TryParse(args, child: false, out var o);
        if (error is not null)
        {
            Say(error);
            return 2;
        }
        var logs = ServiceLog.LogsDirectory;
        Directory.CreateDirectory(logs);
        RecoverPendingRestore(logs);

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Say("cannot resolve the executable path");
            return 1;
        }

        var run = new BenchRun
        {
            StartedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ServiceVersion = typeof(LightingBench).Assembly.GetName().Version?.ToString() ?? "",
            Seconds = o.Seconds,
            Machine = new BenchMachine
            {
                Os = Environment.OSVersion.VersionString,
                Host = Environment.MachineName,
                Cores = Environment.ProcessorCount,
                User = Environment.UserName,
                SessionId = CurrentSessionId(),
            },
        };
        var adapters = GpuAdapterLuids.Enumerate();
        Say($"adapters: {string.Join("; ", adapters.Select(static a => $"{a.Description} [{a.Luid}] {a.DedicatedVramMb} MB"))}");

        var combos = new List<BenchComboResult>();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        foreach (var backend in o.Backends)
        {
            foreach (var adapter in o.Adapters)
            {
                var combo = RunCombo(o, exe, logs, stamp, backend, adapter, adapters);
                combos.Add(combo);
                Say($"{backend}/{adapter}: {combo.Status}{(combo.Detail is null ? "" : " - " + combo.Detail)}");
            }
        }

        BenchReport.Merge(run, combos);
        var json = JsonSerializer.Serialize(run, BenchJsonContext.Default.BenchRun);
        var table = BenchReport.FormatTable(run);
        try
        {
            File.WriteAllText(o.OutPath, json);
            File.WriteAllText(Path.Combine(logs, $"lighting-bench-{stamp}.json"), json);
            File.WriteAllText(Path.Combine(logs, $"lighting-bench-{stamp}.txt"), table);
        }
        catch (Exception ex)
        {
            Say($"could not write results: {ex.Message}");
        }
        Say(table);
        return combos.Any(static c => c.Status is "ok" or "partial") ? 0 : 1;
    }

    private static int CurrentSessionId()
    {
        using var p = Process.GetCurrentProcess();
        return p.SessionId;
    }

    private static void Say(string line)
    {
        try
        {
            Console.WriteLine(line);
        }
        catch (IOException)
        {
            // No console under session 0: the files carry the result.
        }
    }

    private static BenchComboResult RunCombo(
        BenchOptions o, string exe, string logs, string stamp, string backend, string adapter,
        IReadOnlyList<GpuAdapterLuids.Adapter> adapters)
    {
        var combo = new BenchComboResult { Backend = backend, Adapter = adapter };
        string? luid = null;
        string? adapterName = null;
        int? wglPref = null;
        if (adapter == "warp")
        {
            if (backend != "angle")
            {
                combo.Status = "skipped";
                combo.Detail = "only the angle backend can target the software adapter";
                return combo;
            }
        }
        else if (adapter is "integrated" or "dedicated")
        {
            var pick = BenchAdapterPick.Pick(adapters, adapter);
            if (pick is null)
            {
                combo.Status = "skipped";
                combo.Detail = $"no {adapter} adapter on this PC";
                return combo;
            }
            luid = pick.Value.Luid;
            adapterName = pick.Value.Description;
            combo.AdapterLuid = luid;
            wglPref = adapter == "integrated" ? 1 : 2;
        }

        var childOut = Path.Combine(logs, $"lighting-bench-{stamp}-{backend}-{adapter}.child.json");
        var childArgs = new List<string>
        {
            "--lighting-bench-child", childOut, "--backend", backend, "--adapter", adapter,
            "--seconds", o.Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (luid is not null)
        {
            childArgs.Add("--luid");
            childArgs.Add(luid);
        }
        if (backend == "angle" && adapterName is not null)
        {
            childArgs.Add("--adapter-name");
            childArgs.Add(adapterName);
        }
        if (o.Section.Length > 0)
        {
            childArgs.Add("--section");
            childArgs.Add(o.Section);
        }
        if (o.Effects is not null)
        {
            childArgs.Add("--effects");
            childArgs.Add(string.Join(',', o.Effects));
        }

        // Cold start, context init, then per effect: the run itself, a look-ahead window, and probes.
        var effectCount = o.Effects?.Length ?? ShaderLibrary.AllEffectKeys.Count;
        var budget = TimeSpan.FromSeconds(120 + effectCount * (o.Seconds + 12));

        if (backend == "wgl" && s_restoreFailed)
        {
            combo.Status = "failed";
            combo.Detail = "an earlier GPU preference restore failed; wgl combos are stopped until it is undone (see lighting-bench-restore.json)";
            return combo;
        }

        PrefSnapshot? snapshot = null;
        try
        {
            if (backend == "wgl" && wglPref is int pref)
            {
                snapshot = SetPreference(exe, pref, logs, out var refusal);
                if (snapshot is null)
                {
                    combo.Status = "failed";
                    combo.Detail = refusal;
                    return combo;
                }
            }
            return Spawn(exe, childArgs, childOut, budget, combo);
        }
        finally
        {
            if (snapshot is not null)
            {
                RestorePreference(snapshot, logs);
            }
            try
            {
                File.Delete(childOut);
            }
            catch (IOException)
            {
            }
        }
    }

    private static BenchComboResult Spawn(
        string exe, List<string> childArgs, string childOut, TimeSpan budget, BenchComboResult fallback)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in childArgs)
        {
            psi.ArgumentList.Add(a);
        }
        var tail = new Queue<string>();
        void OnLine(object? _, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }
            lock (tail)
            {
                tail.Enqueue(e.Data);
                while (tail.Count > 12)
                {
                    tail.Dequeue();
                }
            }
            Say($"[{fallback.Backend}/{fallback.Adapter}] {e.Data}");
        }

        var timedOut = false;
        int exitCode;
        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("child did not start");
            try
            {
                Nexus.Service.Lifecycle.ChildProcessJob.Assign(p);
            }
            catch
            {
                p.Kill(entireProcessTree: true);
                throw;
            }
            p.OutputDataReceived += OnLine;
            p.ErrorDataReceived += OnLine;
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(budget))
            {
                timedOut = true;
                p.Kill(entireProcessTree: true);
            }
            // The timed wait returns before the redirected streams are drained.
            p.WaitForExit();
            exitCode = p.ExitCode;
        }
        catch (Exception ex)
        {
            fallback.Status = "failed";
            fallback.Detail = $"could not run child: {ex.Message}";
            return fallback;
        }

        BenchComboResult? result = null;
        try
        {
            if (File.Exists(childOut))
            {
                result = JsonSerializer.Deserialize(File.ReadAllText(childOut), BenchJsonContext.Default.BenchComboResult);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            result = null;
        }
        if (result is null)
        {
            fallback.Status = timedOut ? "timeout" : "failed";
            string last;
            lock (tail)
            {
                last = string.Join(" | ", tail.TakeLast(3));
            }
            fallback.Detail = $"child exit {exitCode:X}, no result file. {last}";
            return fallback;
        }
        if (timedOut || (result.Status == "partial" && exitCode != 0))
        {
            result.Status = timedOut ? "timeout" : "failed";
            result.Detail ??= timedOut ? "child exceeded its time budget; partial results kept" : $"child died with exit {exitCode:X}; partial results kept";
        }
        else if (result.Status == "partial")
        {
            result.Detail ??= "child exited before finishing every effect";
        }
        return result;
    }

    private sealed class PrefSnapshot
    {
        public bool System;
        public string ValueName = "";
        public bool HadValue;
        public string? Previous;
        public string Written = "";
    }

    private static readonly object s_prefLock = new();
    private static PrefSnapshot? s_active;
    private static string s_activeLogs = "";
    private static bool s_restoreFailed;
    private static bool s_hooked;

    private static bool RunningAsSystem()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.IsSystem;
    }

    /// <summary>The system hive value belongs to the live service while it runs.</summary>
    private static bool ServiceIsRunning()
    {
        try
        {
            if (System.Threading.Mutex.TryOpenExisting("Global\\NexusServiceMutex", out var m))
            {
                m.Dispose();
                return true;
            }
        }
        catch (Exception)
        {
            return true;
        }
        return false;
    }

    private static RegistryKey? OpenPrefKey(bool system) =>
        system
            ? Registry.Users.CreateSubKey($@"{SystemSid}\{PrefKeyPath}")
            : Registry.CurrentUser.CreateSubKey(PrefKeyPath);

    private static string JournalPath(string logs) => Path.Combine(logs, "lighting-bench-restore.json");

    private static void HookExitRestore()
    {
        if (s_hooked)
        {
            return;
        }
        s_hooked = true;
        Console.CancelKeyPress += (_, _) => RestoreActive();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreActive();
    }

    private static void RestoreActive()
    {
        PrefSnapshot? snap;
        lock (s_prefLock)
        {
            snap = s_active;
        }
        if (snap is not null)
        {
            RestorePreference(snap, s_activeLogs);
        }
    }

    /// <summary>Points WGL at the integrated (1) or high-performance (2) class for this exe. Null,
    /// with the reason, when it is not safe or not possible to write it.</summary>
    private static PrefSnapshot? SetPreference(string exe, int pref, string logs, out string? refusal)
    {
        refusal = null;
        try
        {
            if (File.Exists(JournalPath(logs)))
            {
                refusal = "an unrestored GPU preference journal exists; refusing to overwrite it";
                s_restoreFailed = true;
                return null;
            }
            var system = RunningAsSystem();
            if (system && ServiceIsRunning())
            {
                refusal = "the SYSTEM GPU preference is the running service's; stop the service or run as a user";
                Say(refusal);
                return null;
            }
            using var key = OpenPrefKey(system);
            if (key is null)
            {
                refusal = "could not open the GPU preference key";
                return null;
            }
            var written = $"GpuPreference={pref};";
            var snap = new PrefSnapshot { System = system, ValueName = exe, Written = written };
            snap.Previous = key.GetValue(exe) as string;
            snap.HadValue = snap.Previous is not null;
            var journal = new BenchRestoreJournal
            {
                System = snap.System,
                ValueName = snap.ValueName,
                HadValue = snap.HadValue,
                Previous = snap.Previous,
                Written = written,
            };
            File.WriteAllText(JournalPath(logs), JsonSerializer.Serialize(journal, BenchJsonContext.Default.BenchRestoreJournal));
            lock (s_prefLock)
            {
                s_active = snap;
                s_activeLogs = logs;
            }
            HookExitRestore();
            key.SetValue(exe, written, RegistryValueKind.String);
            return snap;
        }
        catch (Exception ex)
        {
            refusal = $"could not set the GPU preference: {ex.Message}";
            Say(refusal);
            return null;
        }
    }

    private static void RestorePreference(PrefSnapshot snap, string logs)
    {
        lock (s_prefLock)
        {
            if (!ReferenceEquals(s_active, snap))
            {
                return;
            }
            s_active = null;
        }
        try
        {
            ApplyRestore(snap.System, snap.ValueName, snap.HadValue, snap.Previous, snap.Written);
            File.Delete(JournalPath(logs));
        }
        catch (Exception ex)
        {
            s_restoreFailed = true;
            Say($"could not restore the GPU preference: {ex.Message}");
        }
    }

    /// <summary>Restores only while the registry still holds what the bench wrote, so a value changed
    /// meanwhile by the user or the service is left alone.</summary>
    private static void ApplyRestore(bool system, string valueName, bool hadValue, string? previous, string written)
    {
        using var key = OpenPrefKey(system);
        if (key is null)
        {
            return;
        }
        if (!string.Equals(key.GetValue(valueName) as string, written, StringComparison.Ordinal))
        {
            Say("the GPU preference changed since the bench wrote it; leaving it as is");
            return;
        }
        if (hadValue && previous is not null)
        {
            key.SetValue(valueName, previous, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    /// <summary>A previous run killed between set and restore leaves a journal; undo it first.</summary>
    private static void RecoverPendingRestore(string logs)
    {
        try
        {
            var path = JournalPath(logs);
            if (!File.Exists(path))
            {
                return;
            }
            var j = JsonSerializer.Deserialize(File.ReadAllText(path), BenchJsonContext.Default.BenchRestoreJournal);
            if (j is not null)
            {
                ApplyRestore(j.System, j.ValueName, j.HadValue, j.Previous, j.Written);
                Say("restored the GPU preference left by an interrupted bench run");
            }
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Say($"could not recover the previous GPU preference: {ex.Message}");
        }
    }
}
#endif
