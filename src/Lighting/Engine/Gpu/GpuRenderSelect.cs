#if WINDOWS
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Nexus.Service.Lighting.Engine.Gpu;

/// <summary>
/// Windows render-GPU selection for the deferred warmup. The boot decision
/// itself lives in <see cref="GpuSelectPlan"/>; this type supplies the persisted
/// state, the crash guard and the adapter count, and executes what comes back.
///
/// The "does this card hang?" test runs in a throwaway `--gpu-probe` subprocess:
/// a hang there is killed by us on timeout, so it never wedges the service. The
/// child prints GLINIT_ENTER before it touches the GPU, so a child the OS kills
/// can be told apart from one the card killed. Because the DirectX
/// GpuPreference applies at context-creation time, once a probe confirms a card
/// the service sets the preference and creates its own context directly on it.
/// The decision is persisted, so later boots skip probing.
///
/// A crash-guard marker is written before every direct init and cleared after it
/// returns; a boot that finds the marker still present knows the previous boot
/// died initializing that card. There is no CPU fallback: if no card works the
/// GPU is latched off (shader effects don't render; static colours, LED
/// highlight, game sync and firmware-owned lighting are unaffected) and the
/// service stays up. The latch always carries a re-probe time, because the
/// driver behind it can fail context creation intermittently and a permanent
/// latch would turn "sometimes" into "never".
/// </summary>
internal static class GpuRenderSelect
{
    // Parent's backstop wait for a probe child. Must outlast the budget the
    // child pins on itself (see GpuProbe) so a working-but-slow card finishes on
    // its own and the parent only kills a truly wedged child.
    private static TimeSpan ProbeWait =>
        string.Equals(Backend, "glfw", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromSeconds(45)
            : TimeSpan.FromSeconds(15);

    // A card slower than every budget still has to be remembered, or later boots
    // re-pay the probe for a card that works.
    private static readonly TimeSpan LateLandingWait = TimeSpan.FromMinutes(10);

    private const int ClassIntegrated = 0; // clear pref: Windows' default is the iGPU on a hybrid box
    private const int ClassDiscrete = 2;   // high-performance

    // While the GPU is off, how often the adapter set is re-read for a card
    // appearing or a driver changing. A cached DXGI enumeration, no context.
    private static readonly TimeSpan AdapterWatchMin = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan AdapterWatchMax = TimeSpan.FromMinutes(5);

    // Rooted: a Timer nobody holds is collected before it fires.
    private static Timer? _retryTimer;
    private static Timer? _reprobeTimer;
    private static Timer? _watchTimer;
    private static int _rearms;
    // Set while a timer-driven re-select runs: Timer.Dispose does not stop a
    // callback already dispatched, and RearmAfterLatch answers true to a
    // second caller, so without it two probes and two inits could overlap.
    private static int _reselecting;
    private static readonly object TimerGate = new();

    /// <summary>Backend the engine will use, handed to probe children. Set by
    /// the warmup before the first select.</summary>
    public static string Backend { get; set; } = "";

    public static void SelectAndWarm(GpuContext gpu, Stopwatch sw)
    {
        var seam = GpuTestSeam.Describe();
        if (seam.Length > 0)
        {
            GpuContext.Log($"[gpu] select: test seam active: {seam}");
        }

        var crashed = ConsumeCrashGuard();
        var adapters = UsableAdapters();
        var usable = GpuTestSeam.AdapterCount ?? adapters.Count;
        var env = CurrentEnvironment(adapters);
        var now = DateTimeOffset.UtcNow;
        var raw = LoadStateText();
        var state = GpuSelectState.Parse(raw);
        var plan = GpuSelectPlan.NextAction(raw, crashed, usable, now, GpuTestSeam.ReprobeOverride, env);
        GpuContext.Log($"[gpu] select: adapters={usable} env='{env.Fingerprint}@{env.Boot}' state='{state.Format()}' "
            + $"crashguard='{crashed ?? "none"}' -> {plan.Action} ({plan.Reason})");

        switch (plan.Action)
        {
            case GpuSelectAction.DeclineAndLatch:
                // A crash guard alone is not a verdict on the card, so it does
                // not lengthen the wait.
                LatchOff(gpu, state, now, plan.Reason, escalate: false);
                return;
            case GpuSelectAction.DeclineNoProbe:
                HoldLatch(gpu, state, now, plan.Reason, plan.RestampLatch);
                return;
            case GpuSelectAction.WaitForAdapter:
                // The guard was consumed above; the verdict it carries is for
                // the card that comes back, so it is put back for that select.
                if (crashed is not null)
                {
                    WriteCrashGuard(crashed);
                }
                GpuContext.Log($"[gpu] select: {plan.Reason}; GPU off until a display adapter appears");
                gpu.DeclineInit($"GPU rendering off: {plan.Reason}");
                ScheduleAdapterWatch(gpu, AdapterWatchMin, null);
                return;
            case GpuSelectAction.WarmRemembered:
                WarmRemembered(gpu, sw, plan.Card, state);
                return;
            default:
                ProbeThenWarm(gpu, sw, usable, plan.FreshStreak ? GpuSelectState.Fresh : state);
                return;
        }
    }

    private static System.Collections.Generic.IReadOnlyList<Nexus.Service.Sensors.GpuAdapterLuids.Adapter> UsableAdapters() =>
        Nexus.Service.Sensors.GpuAdapterLuids.Enumerate().Where(a => a.VendorId != 0x1414).ToList();

    // Device ids plus driver version, order-free, hashed to one token; the boot
    // id makes a reboot a new environment.
    private static GpuEnvironment CurrentEnvironment(
        System.Collections.Generic.IReadOnlyList<Nexus.Service.Sensors.GpuAdapterLuids.Adapter> adapters)
    {
        // Distinct: an indirect display enumerates as a clone of the physical
        // card, and plugging one must not read as a new GPU.
        var parts = adapters
            .Select(a => $"{a.VendorId:x4}:{a.DeviceId:x4}:{a.SubSysId:x8}:{a.Revision:x2}:{a.UmdVersion}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts)));
        return new(Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant(), BootStamp());
    }

    // The kernel's boot counter; a tick-derived boot minute when it cannot be
    // read, which two reboots inside a minute would tell apart wrong.
    private static string BootStamp()
    {
        try
        {
            if (Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters",
                    "BootId", null) is int id)
            {
                return id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (Exception) { }
        var booted = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return "t" + (booted.ToUnixTimeSeconds() / 60).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ProbeThenWarm(GpuContext gpu, Stopwatch sw, int usable, GpuSelectState state)
    {
        if (usable <= 1)
        {
            if (ProbeCard(ClassIntegrated) == GpuProbeVerdict.Failed)
            {
                LatchOff(gpu, state, DateTimeOffset.UtcNow, "sole GPU probe failed");
                return;
            }
            WarmGuarded(gpu, sw, GpuSelectState.Integrated, ClassIntegrated, "single-gpu");
            AfterWarm(gpu, sw, GpuSelectState.Integrated, ClassIntegrated);
            return;
        }

        GpuContext.Log("[gpu] select: probing GPUs");
        if (ProbeCard(ClassIntegrated) != GpuProbeVerdict.Failed)
        {
            WarmGuarded(gpu, sw, GpuSelectState.Integrated, ClassIntegrated, "probed:integrated");
            AfterWarm(gpu, sw, GpuSelectState.Integrated, ClassIntegrated);
            return;
        }
        GpuContext.Log("[gpu] select: integrated probe failed; trying discrete");
        if (ProbeCard(ClassDiscrete) != GpuProbeVerdict.Failed)
        {
            WarmGuarded(gpu, sw, GpuSelectState.Discrete, ClassDiscrete, "probed:discrete");
            AfterWarm(gpu, sw, GpuSelectState.Discrete, ClassDiscrete);
            return;
        }
        LatchOff(gpu, state, DateTimeOffset.UtcNow, "no GPU produced a working context");
    }

    private static void WarmRemembered(GpuContext gpu, Stopwatch sw, string card, GpuSelectState state)
    {
        var cls = card == GpuSelectState.Discrete ? ClassDiscrete : ClassIntegrated;
        WarmGuarded(gpu, sw, card, cls, $"remembered:{card}");
        if (gpu.Available)
        {
            return;
        }
        if (!gpu.Failed)
        {
            // Not a verdict on the card: it is remembered because it worked
            // before, so the state is kept and the next boot warms it again.
            GpuContext.Log("[gpu] select: remembered card still initializing; keeping it");
            WatchLateInit(gpu, card, state, latchOnMiss: false);
            return;
        }
        SaveState(GpuSelectState.Fresh);
        GpuContext.Log("[gpu] select: remembered card failed init; will re-probe next boot");
        if (!gpu.InitAbandoned)
        {
            TrySwitchToOtherCard(gpu, sw, card);
        }
    }

    // Which follow-up a finished warm attempt earns.
    private static void AfterWarm(GpuContext gpu, Stopwatch sw, string card, int cls)
    {
        if (gpu.Available || gpu.InitSuppressed)
        {
            return;
        }
        if (!gpu.Failed)
        {
            WatchLateInit(gpu, card, GpuSelectState.Parse(LoadStateText()), latchOnMiss: true);
            return;
        }
        if (gpu.InitAbandoned)
        {
            LatchOff(gpu, GpuSelectState.Parse(LoadStateText()), DateTimeOffset.UtcNow, "init never returned");
            return;
        }
        ScheduleRetry(gpu, sw, card, cls, 1);
    }

    // Set the pref, drop a crash-guard, init in-process, clear the guard. A
    // native crash during init leaves the guard for the next boot to demote the
    // card. Persists the state only once the context is actually available.
    private static void WarmGuarded(GpuContext gpu, Stopwatch sw, string stateName, int cls, string why)
    {
        if (gpu.InitSuppressed)
        {
            GpuContext.Log($"[gpu] warmup: declined ({why}); the GPU is off this session");
            return;
        }
        GpuContext.Log($"[gpu] warmup: init ({why})");
        try
        {
            // All three under the lock: the render path calls
            // EnsureInitializedLocked every tick while the context is
            // unavailable, so a gap here lets it start an init that binds the
            // previous card and outruns the crash guard.
            lock (gpu.Lock)
            {
                SetPref(cls);
                WriteCrashGuard(stateName);
                gpu.EnsureInitializedLocked();
            }
            // Outside the lock: the render path takes it every frame.
            gpu.WaitForInit(gpu.InitTimeout);
        }
        catch (Exception ex) { GpuContext.Log($"[gpu] warmup threw: {ex.Message}"); }
        ClearCrashGuard();
        GpuContext.Log(gpu.Available
            ? $"[gpu] warmup: GPU shader engine ready in {sw.ElapsedMilliseconds}ms on '{gpu.Renderer}'"
            : $"[gpu] warmup: no GPU context after {sw.ElapsedMilliseconds}ms "
              + $"({(gpu.Failed ? "init failed" : "still initializing")}; shader effects stay dark until it "
              + "lands, static colours, LED highlight, game sync and firmware-owned lighting are unaffected)");
        if (gpu.Available)
        {
            SaveState(GpuSelectState.Working(stateName));
        }
    }

    // In-process retry for an init that THREW. An abandoned attempt is excluded
    // upstream: its thread is still parked in native GLFW.
    private static void ScheduleRetry(GpuContext gpu, Stopwatch sw, string card, int cls, int attempt)
    {
        var delay = GpuInitRetry.DelayFor(attempt);
        GpuContext.Log($"[gpu] select: init failed; retry {attempt} of {GpuInitRetry.MaxAttempts} in {delay.TotalSeconds:0}s");
        Swap(ref _retryTimer, new Timer(
            _ => new Thread(() => RunRetry(gpu, sw, card, cls, attempt))
            { IsBackground = true, Name = "nexus-gpu-retry" }.Start(),
            null, delay, Timeout.InfiniteTimeSpan));
    }

    private static void RunRetry(GpuContext gpu, Stopwatch sw, string card, int cls, int attempt)
    {
        if (gpu.IsDisposed || gpu.Available || gpu.InitAbandoned || gpu.InitSuppressed)
        {
            return;
        }
        if (!gpu.ResetForRetry())
        {
            LatchOff(gpu, GpuSelectState.Parse(LoadStateText()), DateTimeOffset.UtcNow,
                "no in-process retry budget left");
            return;
        }
        WarmGuarded(gpu, sw, card, cls, $"retry:{attempt}");
        if (gpu.Available)
        {
            GpuContext.Log($"[gpu] select: retry {attempt} landed a context on '{gpu.Renderer}'");
            return;
        }
        if (!gpu.Failed)
        {
            WatchLateInit(gpu, card, GpuSelectState.Parse(LoadStateText()), latchOnMiss: true);
            return;
        }
        if (!gpu.InitAbandoned && attempt < GpuInitRetry.MaxAttempts)
        {
            ScheduleRetry(gpu, sw, card, cls, attempt + 1);
            return;
        }
        LatchOff(gpu, GpuSelectState.Parse(LoadStateText()), DateTimeOffset.UtcNow,
            "every in-process init attempt failed");
    }

    // The warm has already spent the full InitTimeout, so the attempt is written
    // off NOW rather than after a second wait: /lighting/status maps anything but
    // Failed to "initializing", which also withholds the render-GPU shortcut. The
    // write-off is reversible - a context that lands later un-abandons itself -
    // so the watcher keeps waiting and re-persists the card if it arrives.
    private static void WatchLateInit(GpuContext gpu, string card, GpuSelectState state, bool latchOnMiss)
    {
        gpu.AbandonInit();
        if (latchOnMiss)
        {
            LatchOff(gpu, state, DateTimeOffset.UtcNow, "init did not land inside its budget");
        }
        var t = new Thread(() =>
        {
            if (!gpu.WaitForLateLanding(LateLandingWait))
            {
                return;
            }
            SaveState(GpuSelectState.Working(card));
            GpuContext.Log($"[gpu] select: '{card}' landed late on '{gpu.Renderer}'; remembered");
        })
        { IsBackground = true, Name = "nexus-gpu-late-persist" };
        t.Start();
    }

    private static void LatchOff(GpuContext gpu, GpuSelectState state, DateTimeOffset now,
        string reason, bool escalate = true)
    {
        // A shutdown that trips a retry timer must not persist a latch on its way
        // out: the next start would decline a card nothing has judged.
        if (gpu.IsDisposed)
        {
            return;
        }
        var latched = state.LatchedOff(now, escalate, CurrentEnvironment(UsableAdapters()));
        SaveState(latched);
        var wait = GpuSelectState.ReprobeDelay(latched.OffStreak, GpuTestSeam.ReprobeOverride);
        GpuContext.Log($"[gpu] select: {reason}; GPU off; next re-probe in {wait.TotalMinutes:0}m");
        gpu.DeclineInit($"GPU rendering off: {reason}");
        ScheduleReprobe(gpu, wait);
        ScheduleAdapterWatch(gpu, AdapterWatchMax, latched);
    }

    private static void HoldLatch(GpuContext gpu, GpuSelectState state, DateTimeOffset now,
        string reason, bool restamp)
    {
        var held = restamp ? state.Restamped(now, CurrentEnvironment(UsableAdapters())) : state;
        if (restamp)
        {
            SaveState(held);
        }
        var wait = held.ReprobeIn(now, GpuTestSeam.ReprobeOverride);
        GpuContext.Log($"[gpu] select: {reason}; GPU off; next re-probe in {wait.TotalMinutes:0}m "
            + "(picking a render GPU makes the next start re-probe)");
        gpu.DeclineInit($"GPU rendering off: {reason}");
        ScheduleReprobe(gpu, wait);
        ScheduleAdapterWatch(gpu, AdapterWatchMax, held);
    }

    // While off, re-read the adapter set on a slow clock. With no card yet, any
    // card ending the wait; under a latch, a changed environment ending it early.
    // Neither touches the GPU: the re-select that follows still probes first.
    private static void ScheduleAdapterWatch(GpuContext gpu, TimeSpan wait, GpuSelectState? latched)
    {
        if (gpu.IsDisposed || gpu.InitAbandoned)
        {
            return;
        }
        Swap(ref _watchTimer, new Timer(
            _ => new Thread(() => RunAdapterWatch(gpu, wait, latched))
            { IsBackground = true, Name = "nexus-gpu-adapter-watch" }.Start(),
            null, wait, Timeout.InfiniteTimeSpan));
    }

    private static void RunAdapterWatch(GpuContext gpu, TimeSpan wait, GpuSelectState? latched)
    {
        if (gpu.IsDisposed || gpu.Available || gpu.InitAbandoned)
        {
            return;
        }
        var adapters = UsableAdapters();
        var usable = GpuTestSeam.AdapterCount ?? adapters.Count;
        var changed = latched is { } l
            ? usable > 0 && l.LatchStale(CurrentEnvironment(adapters))
            : usable > 0;
        if (!changed)
        {
            var next = wait + AdapterWatchMin;
            ScheduleAdapterWatch(gpu, next < AdapterWatchMax ? next : AdapterWatchMax, latched);
            return;
        }
        if (Volatile.Read(ref _rearms) >= GpuInitRetry.MaxLatchRearms)
        {
            GpuContext.Log("[gpu] select: adapter set changed, but the re-probe budget for this session is spent; the next start re-probes");
            return;
        }
        Reselect(gpu, latched is null
            ? $"a display adapter appeared (adapters={usable})"
            : "the GPU set or driver changed under the off latch", ref _reprobeTimer);
    }

    /// <summary>
    /// A user logged in. The service starts at boot, before any session exists,
    /// and a card that only answers once one does would otherwise stay dark
    /// until the off latch expires - the adapter watch does not fire for a
    /// logon, because neither the adapter set nor the driver changed.
    /// </summary>
    public static void OnSessionLogon(GpuContext gpu)
    {
        // Initializing: an attempt is already running and will answer on its
        // own; rearming under it is refused anyway (GpuContext.RearmAfterLatch).
        if (gpu.Available || gpu.InitAbandoned || gpu.Initializing)
        {
            return;
        }
        // Off the caller's thread on purpose: this arrives on the SCM control
        // handler, which the SCM is waiting on, and a select can cost a probe
        // child plus the init wait.
        new Thread(() => Reselect(gpu, "a user session appeared", ref _reprobeTimer))
        { IsBackground = true, Name = "nexus-gpu-logon-select" }.Start();
    }

    // The one path from a timer back into SelectAndWarm. Cancels the other
    // timer so the outcome's own scheduling starts clean.
    private static void Reselect(GpuContext gpu, string why, ref Timer? other)
    {
        if (Interlocked.CompareExchange(ref _reselecting, 1, 0) != 0)
        {
            return;
        }
        try
        {
            if (!gpu.RearmAfterLatch())
            {
                GpuContext.Log($"[gpu] select: {why}, but the context cannot be rearmed in this process");
                return;
            }
            var round = Interlocked.Increment(ref _rearms);
            Swap(ref other, null);
            GpuContext.Log($"[gpu] select: {why}; re-selecting (session attempt {round})");
            SelectAndWarm(gpu, Stopwatch.StartNew());
        }
        finally
        {
            Volatile.Write(ref _reselecting, 0);
        }
    }

    // The latch expiry has to fire in-session. Before this fix the crash loop
    // supplied the restarts that re-read it; a service that now stays up for a
    // week would otherwise never re-probe, which is the "sometimes becomes never"
    // outcome the expiry exists to prevent.
    private static void ScheduleReprobe(GpuContext gpu, TimeSpan wait)
    {
        if (gpu.IsDisposed || gpu.InitAbandoned)
        {
            return;
        }
        if (Volatile.Read(ref _rearms) >= GpuInitRetry.MaxLatchRearms)
        {
            GpuContext.Log("[gpu] select: re-probe budget spent for this session; the next start re-probes");
            return;
        }
        Swap(ref _reprobeTimer, new Timer(
            _ => new Thread(() => RunReprobe(gpu))
            { IsBackground = true, Name = "nexus-gpu-reprobe" }.Start(),
            null, wait < TimeSpan.Zero ? TimeSpan.Zero : wait, Timeout.InfiniteTimeSpan));
    }

    private static void RunReprobe(GpuContext gpu)
    {
        if (gpu.IsDisposed || gpu.Available || gpu.InitAbandoned)
        {
            return;
        }
        // Through the plan, not straight to the probe: a driver that is absent
        // by now must wait for a card, not fail a probe and latch again.
        Reselect(gpu, "off latch expired", ref _watchTimer);
    }

    private static void Swap(ref Timer? slot, Timer? replacement)
    {
        Timer? previous;
        lock (TimerGate)
        {
            previous = slot;
            slot = replacement;
        }
        previous?.Dispose();
    }

    // Spawn `Nexus.exe --gpu-probe --set-pref <cls>` and wait; kill on timeout (a
    // wedged driver). Both output streams are drained concurrently so a chatty
    // child can't fill a pipe buffer and deadlock. WorkingDirectory is pinned to
    // the exe dir so the bundled GLFW native libs resolve (same loader-search
    // gotcha as the bundled adb).
    private static GpuProbeVerdict ProbeCard(int cls)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return GpuProbeVerdict.Inconclusive;
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--gpu-probe");
            psi.ArgumentList.Add("--set-pref");
            psi.ArgumentList.Add(cls.ToString());
            // The child has no config store, so the engine's backend is handed
            // to it; probing WGL for an engine pinned to GLFW answers nothing.
            if (Backend.Length > 0)
            {
                psi.ArgumentList.Add("--backend");
                psi.ArgumentList.Add(Backend);
            }
            using var p = Process.Start(psi);
            if (p is null) return GpuProbeVerdict.Inconclusive;
            // Drain both pipes concurrently to avoid a buffer-full deadlock.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)ProbeWait.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                GpuContext.Log($"[gpu] select: probe class {cls} timed out ({ProbeWait.TotalSeconds:0}s); killed");
                return GpuProbeVerdict.Inconclusive;
            }
            var output = stdout.Result + " " + stderr.Result;
            var sawEnter = output.Contains(GpuProbeExit.EnterMarker, StringComparison.Ordinal);
            var tail = output.Trim().Replace('\n', ' ').Replace('\r', ' ');
            var verdict = GpuProbeExit.Verdict(p.ExitCode, sawEnter, timedOut: false);
            GpuContext.Log($"[gpu] select: probe class {cls} exit={p.ExitCode} "
                + $"gl-entered={sawEnter} -> {verdict} [{tail}]");
            return verdict;
        }
        catch (Exception ex)
        {
            GpuContext.Log($"[gpu] select: probe class {cls} spawn failed: {ex.Message}");
            return GpuProbeVerdict.Inconclusive;
        }
    }

    /// <summary>Live fallback to the other card; caller guarantees
    /// <paramref name="failedState"/> failed. Runs through WarmGuarded so an
    /// unprobed card still gets the crash guard.</summary>
    private static void TrySwitchToOtherCard(GpuContext gpu, Stopwatch sw, string failedState)
    {
        var other = failedState == GpuSelectState.Discrete ? GpuSelectState.Integrated : GpuSelectState.Discrete;
        if (!gpu.ResetForRetry())
        {
            GpuContext.Log($"[gpu] select: no in-process retry left; '{other}' waits for the next start");
            return;
        }
        GpuContext.Log($"[gpu] select: '{failedState}' failed init; retrying on '{other}' in-process");
        var cls = other == GpuSelectState.Discrete ? ClassDiscrete : ClassIntegrated;
        WarmGuarded(gpu, sw, other, cls, $"switch-from:{failedState}");
        GpuContext.Log(gpu.Available
            ? $"[gpu] select: switched to '{other}' ({gpu.Renderer}) after {sw.ElapsedMilliseconds}ms, no restart needed"
            : $"[gpu] select: '{other}' did not produce a context either");
        AfterWarm(gpu, sw, other, cls);
    }

    private static void SetPref(int cls)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            using var key = Registry.Users.CreateSubKey(
                @"S-1-5-18\Software\Microsoft\DirectX\UserGpuPreferences");
            if (key is null) return;
            if (cls == 1 || cls == 2) key.SetValue(exe, $"GpuPreference={cls};", RegistryValueKind.String);
            else key.DeleteValue(exe, throwOnMissingValue: false);
        }
        catch { }
    }

    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nexus");

    private static string StatePath => Path.Combine(DataDir, "gpu-render-state");
    private static string CrashGuardPath => Path.Combine(DataDir, "gpu-init-crashguard");

    private static string LoadStateText()
    {
        try { return File.Exists(StatePath) ? File.ReadAllText(StatePath).Trim() : GpuSelectState.Unprobed; }
        catch { return GpuSelectState.Unprobed; }
    }

    private static void SaveState(GpuSelectState state)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(StatePath, state.Format());
        }
        catch { }
    }

    /// Drop the crash guard on a graceful stop, so a restart or a user reboot
    /// inside the init window is not read as the card killing the process. An
    /// OS shutdown can cut the process before this runs, which is why a
    /// guard-only latch does not escalate the wait either.
    public static void ClearCrashGuardOnStop() => ClearCrashGuard();

    /// Clears the persisted selection so the next auto boot re-probes. Called
    /// when the user changes the render-GPU choice (so "off" is recoverable).
    public static void ClearState()
    {
        try { if (File.Exists(StatePath)) File.Delete(StatePath); }
        catch { }
    }

    private static void WriteCrashGuard(string card)
    {
        try { Directory.CreateDirectory(DataDir); File.WriteAllText(CrashGuardPath, card); }
        catch { }
    }

    private static void ClearCrashGuard()
    {
        try { if (File.Exists(CrashGuardPath)) File.Delete(CrashGuardPath); }
        catch { }
    }

    // Read + delete the crash-guard: non-null iff the last boot crashed during a
    // direct init (the guard was written before init and never cleared).
    private static string? ConsumeCrashGuard()
    {
        try
        {
            if (!File.Exists(CrashGuardPath)) return null;
            var card = File.ReadAllText(CrashGuardPath).Trim();
            File.Delete(CrashGuardPath);
            return string.IsNullOrEmpty(card) ? null : card;
        }
        catch { return null; }
    }
}
#endif
