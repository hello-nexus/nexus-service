using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Monitoring.History;
using Nexus.Service.Sockets;
using Microsoft.Extensions.Hosting;

namespace Nexus.Service.Activity;

/// <summary>
/// Background service that samples running processes about once a second
/// (configurable via SetInterval, minimum 200 ms) and exposes the latest
/// snapshot via GetProcesses(). Only samples when at least one WebSocket
/// client is subscribed to process or monitoring topics.
/// </summary>
public sealed class ProcessMonitor : BackgroundService
{
    private static readonly int ProcessorCount = Environment.ProcessorCount;
    private volatile IReadOnlyList<ProcessInfo> _latest = Array.Empty<ProcessInfo>();
    private int _intervalMs = 1000;
    private readonly MultiplexHub _hub;
    private readonly IWindowSetProvider? _windowSet;
    private readonly object _demandGate = new();
    private readonly HashSet<string> _demands = new(StringComparer.Ordinal);

    // Wakes ExecuteAsync's delay early when a real subscriber arrives while
    // the loop is sleeping at the slower demand-only cadence. Each call to
    // WaitForNextSampleAsync installs its own TaskCompletionSource here
    // rather than sharing one wait primitive across calls: a SemaphoreSlim's
    // Release hands off to whichever waiter registered first, so a call
    // whose delay wins unpulsed would leave a registered waiter that a later,
    // unrelated pulse could complete instead of the current call's wait.
    // Giving each call its own TaskCompletionSource, matched by reference
    // under _wakeGate, has no shared queue for an old call's waiter to
    // linger in.
    private readonly object _wakeGate = new();
    private TaskCompletionSource<bool>? _pendingWake;

    // Samples that refreshed _latest, so a caller can tell one taken after a given moment.
    private long _sampleSeq;

    // Bounds growth over a long-running service: every distinct name ever
    // seen (installers, temp tools, updaters) would otherwise accumulate a
    // permanent entry with no eviction. _pathCacheOrder tracks insertion
    // order for eviction - Dictionary enumeration order is not reliable
    // insertion order once removals have happened (a freed slot is reused
    // by the next insert and can enumerate first, evicting on every insert).
    //
    // Each entry also carries the pid its path was resolved from. A name is
    // an aggregate key, not a stable identity - the pid it once resolved to
    // can exit and an unrelated process can later reuse the same name, which
    // must not keep serving the exited process's path. PruneDeadPathCacheEntries
    // evicts an entry once its pid is no longer among the live set a sampling
    // tick observed, so the next lookup re-resolves against whichever process
    // (if any) currently owns the name.
    private const int PathCacheMaxEntries = 500;
    private readonly object _pathCacheLock = new();
    private readonly Dictionary<string, (string Path, int Pid)> _pathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _pathCacheOrder = new();

    // Delta tracking for CPU time (Windows uses TimeSpan, macOS uses mach
    // absolute-time ticks, converted via MacProcInfo.MachTicksToNs at read time).
    private readonly Dictionary<int, (TimeSpan cpuTime, DateTime when)> _winPrev = new();
    private readonly Dictionary<int, (ulong cpuTicks, DateTime when)> _macPrev = new();

    // Delta tracking for cumulative storage I/O bytes, read and write kept
    // separate so a reset in one direction does not suppress the other;
    // the combined rate is still derived from both at read time below,
    // pruned on the same seen-pid pass as _winPrev/_macPrev.
    private readonly Dictionary<int, (long readBytes, long writeBytes, DateTime when)> _winStoragePrev = new();
    private readonly Dictionary<int, (ulong readBytes, ulong writeBytes, DateTime when)> _macStoragePrev = new();
    private readonly Dictionary<int, (long readBytes, long writeBytes, DateTime when)> _linuxStoragePrev = new();

    // Anchors a stable fallback timestamp per pid for when the real process
    // start time is unreadable (elevated/protected process under
    // LocalSystem), so such a process still sorts by recency instead of
    // always ranking as the oldest possible entry. Pruned on the same
    // seen-pid pass as _winPrev, so a stale anchor never lingers to
    // misattribute a pid Windows later reuses for a different process.
    private readonly Dictionary<int, long> _firstSeenAtMs = new();

    /// <summary>windowSet is null on non-Windows and whenever no Windows
    /// build registers one (no user-session helper connected yet) - HasWindow
    /// then stays false for every process, matching "nothing is windowed"
    /// rather than treating the absence as an error.</summary>
    public ProcessMonitor(MultiplexHub hub, IWindowSetProvider? windowSet = null)
    {
        _hub = hub;
        _windowSet = windowSet;
        _hub.OnTopicFirstSubscriber += OnTopicFirstSubscriber;
    }

    public override void Dispose()
    {
        _hub.OnTopicFirstSubscriber -= OnTopicFirstSubscriber;
        base.Dispose();
    }

    private void OnTopicFirstSubscriber(string topic)
    {
        if (string.Equals(topic, "processes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(topic, "monitoring", StringComparison.OrdinalIgnoreCase))
        {
            TaskCompletionSource<bool>? pending;
            lock (_wakeGate)
            {
                pending = _pendingWake;
            }
            pending?.TrySetResult(true);
        }
    }

    public IReadOnlyList<ProcessInfo> GetProcesses() => _latest;

    /// <summary>Test-only seam: the sampling loop never runs synchronously
    /// under a unit test, so this stands in for a completed sample.</summary>
    internal void SetProcessesForTest(IReadOnlyList<ProcessInfo> processes)
    {
        _latest = processes;
        MarkSampled();
    }

    /// <summary>Number of samples that refreshed the process list so far.</summary>
    internal long SampleSeq => Interlocked.Read(ref _sampleSeq);

    /// <summary>Raised on the sampling thread after each sample that refreshed the process list.</summary>
    internal event Action? Sampled;

    private void MarkSampled()
    {
        Interlocked.Increment(ref _sampleSeq);
        Sampled?.Invoke();
    }

    public void SetInterval(int ms) => _intervalMs = Math.Max(200, ms);

    /// <summary>Resolves name (a live process's aggregate name) to the exe
    /// path of its newest matching pid, for icon extraction. Resolved
    /// on-demand (not every sampling tick - MainModule enumeration is far
    /// pricier than the CPU/memory reads every process already pays) and
    /// cached by name; access-denied on a specific pid is tolerated and
    /// simply yields no path rather than failing the request. Null when no
    /// live process matches or the path can't be read.</summary>
    public string? ResolveExecutablePath(string name)
    {
        lock (_pathCacheLock)
        {
            if (_pathCache.TryGetValue(name, out var cached))
            {
                return cached.Path;
            }
        }

        var candidates = _latest.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        var match = candidates
            .OrderByDescending(p => p.StartedAtMs ?? 0)
            .FirstOrDefault();
        // The sampling loop only runs while something subscribes, so an empty
        // _latest means "not sampled", not "not running" - callers off the
        // monitoring topic (the volume mixer) resolve against live processes.
        var pid = match?.Pid ?? ResolveLivePidByName(name);
        if (pid is null)
        {
            return null;
        }

        string? path = null;
        try
        {
            using var proc = Process.GetProcessById(pid.Value);
            path = proc.MainModule?.FileName;
        }
        catch { }

        if (!string.IsNullOrEmpty(path))
        {
            InsertPathCache(name, path, pid.Value);
        }
        return path;
    }

    private static int? ResolveLivePidByName(string name)
    {
        try
        {
            var live = Process.GetProcessesByName(name);
            try
            {
                return live.Length == 0 ? null : live[0].Id;
            }
            finally
            {
                foreach (var p in live) p.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    private void InsertPathCache(string name, string path, int pid)
    {
        lock (_pathCacheLock)
        {
            if (!_pathCache.ContainsKey(name))
            {
                _pathCacheOrder.Enqueue(name);
            }
            _pathCache[name] = (path, pid);
            if (_pathCache.Count > PathCacheMaxEntries)
            {
                _pathCache.Remove(_pathCacheOrder.Dequeue());
            }
        }
    }

    /// <summary>Evicts every path-cache entry whose resolved pid is not in
    /// livePids - called once per sampling tick with that tick's live pid
    /// set (SampleWindows/SampleMacOs), the same pass that already prunes
    /// _winPrev/_macPrev, so a name a since-exited process resolved stops
    /// answering with that process's path the moment it is gone, rather than
    /// only when a fresh resolve happens to be triggered some other way.
    /// Internal so a test can drive it directly without depending on the
    /// real OS process enumeration those methods perform.</summary>
    internal void PruneDeadPathCacheEntries(HashSet<int> livePids)
    {
        lock (_pathCacheLock)
        {
            if (_pathCache.Count == 0)
            {
                return;
            }
            List<string>? stale = null;
            foreach (var kv in _pathCache)
            {
                if (!livePids.Contains(kv.Value.Pid))
                {
                    (stale ??= new List<string>()).Add(kv.Key);
                }
            }
            if (stale is null)
            {
                return;
            }
            foreach (var name in stale)
            {
                _pathCache.Remove(name);
            }
        }
    }

    /// <summary>Test-only seam: seeds the name-&gt;path cache directly,
    /// bypassing real MainModule resolution. pid defaults to 0 (never a
    /// live pid in these tests) for callers that only exercise cache
    /// consumption, not the pid-based eviction PruneDeadPathCacheEntries
    /// drives.</summary>
    internal void SeedResolvedPathForTest(string name, string path, int pid = 0) => InsertPathCache(name, path, pid);

    /// <summary>Returns a stable epoch-ms anchor for pid, set to observedAt
    /// the first time this pid is seen and unchanged on every later call -
    /// the fallback SampleWindows uses in place of StartTime when reading
    /// the real value throws.</summary>
    internal long AnchorFirstSeenMs(int pid, DateTime observedAt)
    {
        if (!_firstSeenAtMs.TryGetValue(pid, out var anchored))
        {
            anchored = new DateTimeOffset(observedAt.ToUniversalTime()).ToUnixTimeMilliseconds();
            _firstSeenAtMs[pid] = anchored;
        }
        return anchored;
    }

    // Bounded like _pathCache, keyed by exe path (not name) so once resolved,
    // two names sharing one binary read the same entry. The in-flight dedup
    // (_metaPending) is keyed by name, not path: two names first seen in the
    // same tick that share a path can each start one redundant background
    // resolve before either finishes - harmless, InsertMetaCache is an
    // idempotent overwrite. Populated only by the background resolver below -
    // GetProcessMeta only ever reads it.
    private const int MetaCacheMaxEntries = 500;
    private const int MetaResolveMaxConcurrency = 4;
    private readonly object _metaCacheLock = new();
    private readonly Dictionary<string, ProcessMeta> _metaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _metaCacheOrder = new();
    private readonly Dictionary<string, Task> _metaPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _metaResolveGate = new(MetaResolveMaxConcurrency, MetaResolveMaxConcurrency);

    // A name whose exe path never resolves (protected/system processes,
    // common under LocalSystem) would otherwise re-enqueue a Task.Run every
    // tick forever, each throwing a MainModule access-denied. Cache the
    // failure per name with a cooldown - long enough to stop the churn,
    // short enough that a transient resolve failure (a process caught mid
    // launch) recovers within a session. Bounded the same way as the other
    // caches in this class.
    private static readonly TimeSpan MetaResolveFailureCooldown = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, DateTime> _metaUnresolvedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _metaUnresolvedOrder = new();

    // Up to MetaResolveMaxConcurrency instances of ResolveMetaAsync run
    // concurrently on the thread pool, so the test counter below needs an
    // interlocked increment, not a plain one.
    private int _metaResolveAttemptsForTest;

    /// <summary>Test-only counter: total ResolveMetaAsync invocations that
    /// actually ran (excludes attempts EnqueueMetaResolve suppressed via the
    /// failure cooldown), so a test can confirm the cooldown skipped a
    /// re-attempt rather than inferring it from timing.</summary>
    internal int MetaResolveAttemptsForTest => Volatile.Read(ref _metaResolveAttemptsForTest);

    /// <summary>Publisher and signature status for name's exe. Never blocks:
    /// a cache miss queues a background resolve (FileVersionInfo company +
    /// ProcessSignatureChecker, off the calling thread) and returns null
    /// immediately, so the broadcast tick reads whatever has landed so far.
    /// Null until the background resolve for that path completes, or when
    /// the path can never be resolved.</summary>
    public ProcessMeta? GetProcessMeta(string name)
    {
        string? path;
        lock (_pathCacheLock)
        {
            path = _pathCache.TryGetValue(name, out var cached) ? cached.Path : null;
        }

        if (path is not null)
        {
            lock (_metaCacheLock)
            {
                if (_metaCache.TryGetValue(path, out var cached))
                {
                    return cached;
                }
            }
        }

        _ = EnqueueMetaResolve(name);
        return null;
    }

    /// <summary>Test-only seam: awaits the real background resolve queued by
    /// GetProcessMeta instead of polling, so a resolve-then-assert test is
    /// deterministic without a sleep.</summary>
    internal Task ResolveMetaForTestAsync(string name) => EnqueueMetaResolve(name);

    /// <summary>Test-only seam: seeds the path-keyed meta cache directly,
    /// through the same bounded-insert path production resolves use.</summary>
    internal void SeedProcessMetaForTest(string exePath, ProcessMeta meta) => InsertMetaCache(exePath, meta);

    /// <summary>Test-only seam: checks the meta cache by path directly,
    /// without going through a name lookup.</summary>
    internal bool HasCachedMetaForTest(string exePath)
    {
        lock (_metaCacheLock)
        {
            return _metaCache.ContainsKey(exePath);
        }
    }

    private Task EnqueueMetaResolve(string name)
    {
        lock (_metaCacheLock)
        {
            if (_metaPending.TryGetValue(name, out var existing))
            {
                return existing;
            }
            if (_metaUnresolvedAt.TryGetValue(name, out var failedAt) &&
                DateTime.UtcNow - failedAt < MetaResolveFailureCooldown)
            {
                return Task.CompletedTask;
            }
            // Task.Run guarantees the resolve (including ResolveExecutablePath's
            // blocking MainModule read) runs on a pool thread, never inline on
            // the caller - a bare async call would run synchronously up to its
            // first genuine await, which SemaphoreSlim.WaitAsync is not when a
            // slot is free.
            var task = Task.Run(() => ResolveMetaAsync(name));
            _metaPending[name] = task;
            return task;
        }
    }

    private async Task ResolveMetaAsync(string name)
    {
        Interlocked.Increment(ref _metaResolveAttemptsForTest);
        await _metaResolveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = ResolveExecutablePath(name);
            if (string.IsNullOrEmpty(path))
            {
                InsertUnresolvedName(name);
                return;
            }

            lock (_metaCacheLock)
            {
                if (_metaCache.ContainsKey(path))
                {
                    return;
                }
            }

            string? company = null;
            try
            {
                company = FileVersionInfo.GetVersionInfo(path).CompanyName;
                if (string.IsNullOrWhiteSpace(company))
                {
                    company = null;
                }
            }
            catch { /* no VERSIONINFO resource, or unreadable */ }

            var (signed, signerCn) = ProcessSignatureChecker.Check(path);
            InsertMetaCache(path, new ProcessMeta(company ?? signerCn, signed));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[process-monitor] meta resolve failed for {name}: {ex.Message}");
        }
        finally
        {
            lock (_metaCacheLock) { _metaPending.Remove(name); }
            _metaResolveGate.Release();
        }
    }

    // Never remove a name from _metaUnresolvedAt on a later successful
    // resolve: GetProcessMeta short-circuits on a path+meta cache hit before
    // it ever reaches EnqueueMetaResolve again, so a stale entry here is
    // inert, not wrong. Removing it here would let a subsequent failure
    // re-enqueue the same name into _metaUnresolvedOrder, desyncing the
    // queue from the dict it is meant to bound.
    private void InsertUnresolvedName(string name)
    {
        lock (_metaCacheLock)
        {
            if (!_metaUnresolvedAt.ContainsKey(name))
            {
                _metaUnresolvedOrder.Enqueue(name);
            }
            _metaUnresolvedAt[name] = DateTime.UtcNow;
            if (_metaUnresolvedAt.Count > MetaCacheMaxEntries)
            {
                _metaUnresolvedAt.Remove(_metaUnresolvedOrder.Dequeue());
            }
        }
    }

    private void InsertMetaCache(string path, ProcessMeta meta)
    {
        lock (_metaCacheLock)
        {
            if (!_metaCache.ContainsKey(path))
            {
                _metaCacheOrder.Enqueue(path);
            }
            _metaCache[path] = meta;
            if (_metaCache.Count > MetaCacheMaxEntries)
            {
                _metaCache.Remove(_metaCacheOrder.Dequeue());
            }
        }
    }

    /// <summary>Adds or removes source from the demand set that keeps
    /// sampling running even with no WebSocket subscriber (the metrics
    /// history sampler's always-on per-app recording). Idempotent per
    /// source id, same shape as IFpsProvider.SetDemand.</summary>
    public void SetDemand(string source, bool wanted)
    {
        lock (_demandGate)
        {
            if (wanted) _demands.Add(source);
            else _demands.Remove(source);
        }
    }

    private bool HasRealSubscribers =>
        _hub.TopicHasSubscribers("processes") || _hub.TopicHasSubscribers("monitoring");

    private bool HasDemand
    {
        get { lock (_demandGate) { return _demands.Count > 0; } }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Demand with no real WS subscriber runs at app-history's own
            // sub-cadence (MetricsHistory.AppSampleIntervalSeconds) rather
            // than the full 1Hz broadcast rate - a demand-only consumer
            // reads at that slower cadence already, so sampling faster
            // gains it nothing.
            var hasReal = HasRealSubscribers;
            try
            {
                if (hasReal || HasDemand)
                {
#if MACOS
                    SampleMacOs();
#else
                    SampleWindows();
#endif
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[process-monitor] sample failed: {ex.Message}");
            }

            // WaitForNextSampleAsync lets a real subscriber that arrives
            // mid-sleep cut this delay short instead of waiting out the
            // full demand-only interval.
            var delayMs = hasReal ? _intervalMs : MetricsHistory.AppSampleIntervalSeconds * 1000;
            await WaitForNextSampleAsync(delayMs, stoppingToken);
        }
    }

    // Returns true when a pulse resolved the wait before delayMs elapsed,
    // false when the delay won. Task.WhenAny does not throw on
    // stoppingToken cancellation; the caller's while condition exits.
    internal async Task<bool> WaitForNextSampleAsync(int delayMs, CancellationToken stoppingToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_wakeGate)
        {
            _pendingWake = tcs;
        }
        var winner = await Task.WhenAny(Task.Delay(delayMs, stoppingToken), tcs.Task);
        lock (_wakeGate)
        {
            if (ReferenceEquals(_pendingWake, tcs))
            {
                _pendingWake = null;
            }
        }
        return winner == tcs.Task;
    }

#if MACOS
    /// <summary>
    /// macOS: proc_pidinfo gives per-process CPU time and RSS via direct
    /// kernel syscalls - no subprocess spawn. Delta-based CPU% normalized
    /// by elapsed time and core count, same approach as the Windows path.
    /// </summary>
    private void SampleMacOs()
    {
        var pids = Platform.Mac.MacProcInfo.ListPids();
        if (pids.Length == 0)
            return;

        var now = DateTime.UtcNow;
        var result = new List<ProcessInfo>();
        var seen = new HashSet<int>();
        var pathBuf = new byte[4096];

        foreach (var pid in pids)
        {
            if (pid <= 0)
                continue;
            seen.Add(pid);

            if (!Platform.Mac.MacProcInfo.TryGetTaskInfo(pid, out var ti))
                continue;

            var name = Platform.Mac.MacProcInfo.GetProcessName(pid, pathBuf);
            if (string.IsNullOrEmpty(name))
                continue;

            var cpuTicks = ti.TotalUser + ti.TotalSystem;
            double cpuPercent = 0;
            if (_macPrev.TryGetValue(pid, out var prev))
            {
                var elapsedMs = (now - prev.when).TotalMilliseconds;
                if (elapsedMs > 50)
                {
                    var deltaTicks = cpuTicks > prev.cpuTicks ? cpuTicks - prev.cpuTicks : 0;
                    var deltaMs = Platform.Mac.MacProcInfo.MachTicksToNs(deltaTicks) / 1_000_000.0;
                    cpuPercent = Math.Clamp(deltaMs / elapsedMs / ProcessorCount * 100.0, 0, 100);
                }
            }
            _macPrev[pid] = (cpuTicks, now);

            double storageBytesPerSec = 0;
            double storageReadBytesPerSec = 0;
            double storageWriteBytesPerSec = 0;
            if (Platform.Mac.MacProcInfo.TryGetDiskIoBytes(pid, out var readBytes, out var writeBytes))
            {
                if (_macStoragePrev.TryGetValue(pid, out var prevStorage))
                {
                    var elapsedMs = (now - prevStorage.when).TotalMilliseconds;
                    if (elapsedMs > 50)
                    {
                        var storageBytes = readBytes + writeBytes;
                        var prevStorageBytes = prevStorage.readBytes + prevStorage.writeBytes;
                        if (storageBytes >= prevStorageBytes)
                        {
                            storageBytesPerSec = (storageBytes - prevStorageBytes) / (elapsedMs / 1000.0);
                        }
                        if (readBytes >= prevStorage.readBytes)
                        {
                            storageReadBytesPerSec = (readBytes - prevStorage.readBytes) / (elapsedMs / 1000.0);
                        }
                        if (writeBytes >= prevStorage.writeBytes)
                        {
                            storageWriteBytesPerSec = (writeBytes - prevStorage.writeBytes) / (elapsedMs / 1000.0);
                        }
                    }
                }
                _macStoragePrev[pid] = (readBytes, writeBytes, now);
            }

            result.Add(new ProcessInfo
            {
                Pid = pid,
                Name = name,
                CpuPercent = Math.Round(cpuPercent, 1),
                MemoryMb = Math.Round(ti.ResidentSize / (1024.0 * 1024.0), 1),
                CpuTimeSeconds = Math.Round(Platform.Mac.MacProcInfo.MachTicksToNs(cpuTicks) / 1_000_000_000.0, 1),
                StorageBytesPerSec = Math.Round(storageBytesPerSec, 1),
                StorageReadBytesPerSec = Math.Round(storageReadBytesPerSec, 1),
                StorageWriteBytesPerSec = Math.Round(storageWriteBytesPerSec, 1),
            });
        }

        // Prune stale PID entries.
        if (_macPrev.Count > seen.Count)
        {
            var toRemove = new List<int>(_macPrev.Count - seen.Count);
            foreach (var k in _macPrev.Keys)
            {
                if (!seen.Contains(k))
                {
                    toRemove.Add(k);
                }
            }
            foreach (var k in toRemove)
            {
                _macPrev.Remove(k);
            }
        }
        if (_macStoragePrev.Count > seen.Count)
        {
            var toRemove = new List<int>(_macStoragePrev.Count - seen.Count);
            foreach (var k in _macStoragePrev.Keys)
            {
                if (!seen.Contains(k))
                {
                    toRemove.Add(k);
                }
            }
            foreach (var k in toRemove)
            {
                _macStoragePrev.Remove(k);
            }
        }
        PruneDeadPathCacheEntries(seen);

        // Group by name: the first instance per name accumulates the rest.
        var grouped = new Dictionary<string, ProcessInfo>(result.Count);
        foreach (var p in result)
        {
            if (grouped.TryGetValue(p.Name, out var acc))
            {
                acc.CpuPercent = Math.Round(acc.CpuPercent + p.CpuPercent, 1);
                acc.MemoryMb = Math.Round(acc.MemoryMb + p.MemoryMb, 1);
                acc.CpuTimeSeconds = Math.Round(acc.CpuTimeSeconds + p.CpuTimeSeconds, 1);
                acc.StorageBytesPerSec = Math.Round(acc.StorageBytesPerSec + p.StorageBytesPerSec, 1);
                acc.StorageReadBytesPerSec = Math.Round(acc.StorageReadBytesPerSec + p.StorageReadBytesPerSec, 1);
                acc.StorageWriteBytesPerSec = Math.Round(acc.StorageWriteBytesPerSec + p.StorageWriteBytesPerSec, 1);
            }
            else
            {
                grouped[p.Name] = p;
            }
        }
        var sorted = new List<ProcessInfo>(grouped.Count);
        foreach (var v in grouped.Values)
        {
            sorted.Add(v);
        }
        sorted.Sort(static (a, b) =>
        {
            var c = b.CpuPercent.CompareTo(a.CpuPercent);
            return c != 0 ? c : b.MemoryMb.CompareTo(a.MemoryMb);
        });
        _latest = sorted;
        MarkSampled();
    }
#endif

    /// <summary>
    /// Windows: Process.TotalProcessorTime works reliably via perf counters.
    /// Delta-based CPU% normalized by elapsed time and core count. Internal
    /// (not private) so tests can drive the real sampling path directly,
    /// same seam as WaitForNextSampleAsync.
    /// </summary>
    internal void SampleWindows()
    {
        var now = DateTime.UtcNow;
        var processes = Process.GetProcesses();
        var result = new List<ProcessInfo>();
        var seen = new HashSet<int>();

        foreach (var proc in processes)
        {
            try
            {
                var pid = proc.Id;
                seen.Add(pid);

                TimeSpan cpuTime;
                long memBytes;
                string name;
                try
                {
                    cpuTime = proc.TotalProcessorTime;
                    memBytes = proc.WorkingSet64;
                    name = proc.ProcessName;
                }
                catch { continue; }

                double cpuPercent = 0;
                if (_winPrev.TryGetValue(pid, out var prev))
                {
                    var elapsed = (now - prev.when).TotalMilliseconds;
                    if (elapsed > 50)
                    {
                        var delta = (cpuTime - prev.cpuTime).TotalMilliseconds;
                        cpuPercent = Math.Clamp(delta / elapsed / ProcessorCount * 100.0, 0, 100);
                    }
                }
                _winPrev[pid] = (cpuTime, now);

                // Reuses the handle proc.TotalProcessorTime/WorkingSet64 already
                // opened above rather than a second OpenProcess call. A pid whose
                // handle lacks the access GetProcessIoCounters needs (rare given
                // the reads above already succeeded) simply reports 0 this tick,
                // not an exception.
                double storageBytesPerSec = 0;
                double storageReadBytesPerSec = 0;
                double storageWriteBytesPerSec = 0;
                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        if (GetProcessIoCounters(proc.Handle, out var io))
                        {
                            var readBytes = (long)io.ReadTransferCount;
                            var writeBytes = (long)io.WriteTransferCount;
                            if (_winStoragePrev.TryGetValue(pid, out var prevStorage))
                            {
                                var elapsedStorage = (now - prevStorage.when).TotalMilliseconds;
                                if (elapsedStorage > 50)
                                {
                                    var storageBytes = readBytes + writeBytes;
                                    var prevStorageBytes = prevStorage.readBytes + prevStorage.writeBytes;
                                    if (storageBytes >= prevStorageBytes)
                                    {
                                        storageBytesPerSec = (storageBytes - prevStorageBytes) / (elapsedStorage / 1000.0);
                                    }
                                    if (readBytes >= prevStorage.readBytes)
                                    {
                                        storageReadBytesPerSec = (readBytes - prevStorage.readBytes) / (elapsedStorage / 1000.0);
                                    }
                                    if (writeBytes >= prevStorage.writeBytes)
                                    {
                                        storageWriteBytesPerSec = (writeBytes - prevStorage.writeBytes) / (elapsedStorage / 1000.0);
                                    }
                                }
                            }
                            _winStoragePrev[pid] = (readBytes, writeBytes, now);
                        }
                    }
                    catch { }
                }
                else if (OperatingSystem.IsLinux())
                {
                    try
                    {
                        if (TryReadLinuxIoBytes(pid, out var readBytes, out var writeBytes))
                        {
                            if (_linuxStoragePrev.TryGetValue(pid, out var prevStorage))
                            {
                                var elapsedStorage = (now - prevStorage.when).TotalMilliseconds;
                                if (elapsedStorage > 50)
                                {
                                    var storageBytes = readBytes + writeBytes;
                                    var prevStorageBytes = prevStorage.readBytes + prevStorage.writeBytes;
                                    if (storageBytes >= prevStorageBytes)
                                    {
                                        storageBytesPerSec = (storageBytes - prevStorageBytes) / (elapsedStorage / 1000.0);
                                    }
                                    if (readBytes >= prevStorage.readBytes)
                                    {
                                        storageReadBytesPerSec = (readBytes - prevStorage.readBytes) / (elapsedStorage / 1000.0);
                                    }
                                    if (writeBytes >= prevStorage.writeBytes)
                                    {
                                        storageWriteBytesPerSec = (writeBytes - prevStorage.writeBytes) / (elapsedStorage / 1000.0);
                                    }
                                }
                            }
                            _linuxStoragePrev[pid] = (readBytes, writeBytes, now);
                        }
                    }
                    catch { }
                }

                // Running as LocalSystem denies StartTime for some processes
                // (elevated/protected system processes); fall back to a
                // stable first-seen anchor rather than leaving it null, so
                // the entry still sorts by recency instead of the bottom.
                long? startedAtMs;
                try
                {
                    startedAtMs = new DateTimeOffset(proc.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
                }
                catch
                {
                    startedAtMs = AnchorFirstSeenMs(pid, now);
                }

                result.Add(new ProcessInfo
                {
                    Pid = pid,
                    Name = name,
                    CpuPercent = Math.Round(cpuPercent, 1),
                    MemoryMb = Math.Round(memBytes / (1024.0 * 1024.0), 1),
                    CpuTimeSeconds = Math.Round(cpuTime.TotalSeconds, 1),
                    StartedAtMs = startedAtMs,
                    HasWindow = _windowSet?.IsWindowed(pid) ?? false,
                    StorageBytesPerSec = Math.Round(storageBytesPerSec, 1),
                    StorageReadBytesPerSec = Math.Round(storageReadBytesPerSec, 1),
                    StorageWriteBytesPerSec = Math.Round(storageWriteBytesPerSec, 1),
                });
            }
            catch { }
            finally { proc.Dispose(); }
        }

        // Prune stale PID entries.
        if (_winPrev.Count > seen.Count)
        {
            var toRemove = new List<int>(_winPrev.Count - seen.Count);
            foreach (var k in _winPrev.Keys)
            {
                if (!seen.Contains(k))
                {
                    toRemove.Add(k);
                }
            }
            foreach (var k in toRemove)
            {
                _winPrev.Remove(k);
            }
        }
        if (_winStoragePrev.Count > seen.Count)
        {
            var toRemove = new List<int>(_winStoragePrev.Count - seen.Count);
            foreach (var k in _winStoragePrev.Keys)
            {
                if (!seen.Contains(k))
                {
                    toRemove.Add(k);
                }
            }
            foreach (var k in toRemove)
            {
                _winStoragePrev.Remove(k);
            }
        }
        if (_linuxStoragePrev.Count > seen.Count)
        {
            var toRemove = new List<int>(_linuxStoragePrev.Count - seen.Count);
            foreach (var k in _linuxStoragePrev.Keys)
            {
                if (!seen.Contains(k))
                {
                    toRemove.Add(k);
                }
            }
            foreach (var k in toRemove)
            {
                _linuxStoragePrev.Remove(k);
            }
        }
        // Unlike _winPrev/_macPrev (an entry per live process every tick, so
        // Count tracks seen.Count closely), _firstSeenAtMs only ever holds
        // the sparse subset whose StartTime read failed - Count can stay far
        // below seen.Count forever, so pruning cannot gate on that
        // comparison or a stale entry never clears and can later re-anchor
        // an unrelated process that reuses the same pid.
        if (_firstSeenAtMs.Count > 0)
        {
            var toRemove = new List<int>();
            foreach (var k in _firstSeenAtMs.Keys)
            {
                if (!seen.Contains(k))
                {
                    toRemove.Add(k);
                }
            }
            foreach (var k in toRemove)
            {
                _firstSeenAtMs.Remove(k);
            }
        }
        PruneDeadPathCacheEntries(seen);

        result.Sort(static (a, b) =>
        {
            var c = b.CpuPercent.CompareTo(a.CpuPercent);
            return c != 0 ? c : b.MemoryMb.CompareTo(a.MemoryMb);
        });
        _latest = result;
        MarkSampled();
    }

    // /proc/pid/io read_bytes/write_bytes are actual block IO (unlike
    // rchar/wchar, which also count page-cache hits and tty/pipe traffic -
    // see LinuxNetworkProvider.ReadIoCounters for that pair).
    private static bool TryReadLinuxIoBytes(int pid, out long readBytes, out long writeBytes)
    {
        readBytes = 0;
        writeBytes = 0;
        try
        {
            return TryParseLinuxIoBytes(File.ReadAllText($"/proc/{pid}/io"), out readBytes, out writeBytes);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Parses read_bytes/write_bytes out of /proc/pid/io text; a
    /// field absent from the text leaves that output at zero. Internal (not
    /// private) and OS-independent text parsing so it compiles and unit-tests
    /// on any host, matching WaitForNextSampleAsync's test seam.</summary>
    internal static bool TryParseLinuxIoBytes(string ioText, out long readBytes, out long writeBytes)
    {
        readBytes = 0;
        writeBytes = 0;
        var found = false;
        foreach (var line in ioText.Split('\n'))
        {
            if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
            {
                if (long.TryParse(line.AsSpan(11).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out readBytes))
                {
                    found = true;
                }
            }
            else if (line.StartsWith("write_bytes:", StringComparison.Ordinal))
            {
                if (long.TryParse(line.AsSpan(12).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out writeBytes))
                {
                    found = true;
                }
                break; // read_bytes comes before write_bytes; safe to bail after write_bytes.
            }
        }
        return found;
    }

    // Win32 P/Invoke for cumulative per-process I/O byte counters, same
    // struct/import shape as WindowsNetworkProvider's copy of this call.
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IO_COUNTERS lpIoCounters);
}

public class ProcessInfo
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public double CpuPercent { get; set; }
    public double MemoryMb { get; set; }
    public double CpuTimeSeconds { get; set; }
    /// <summary>Process creation time, UTC epoch ms. Falls back to a stable
    /// first-seen anchor when the real value is denied (elevated/protected
    /// process under LocalSystem); null only on a platform that never
    /// populates it.</summary>
    public long? StartedAtMs { get; set; }
    /// <summary>True when this process owns a visible top-level window
    /// (Task-Manager-style App vs Background classification), from
    /// IWindowSetProvider. LocalSystem in Session 0 cannot enumerate the
    /// interactive desktop's windows, so this can never be read via a
    /// service-side Win32 call - Windows sources it from the user-session
    /// helper; macOS and an unconnected helper both read false.</summary>
    public bool HasWindow { get; set; }
    /// <summary>Combined disk read+write rate, bytes/sec, delta-computed the
    /// same way as CpuPercent. Zero when the platform call fails for this
    /// pid or on the process's first observed tick.</summary>
    public double StorageBytesPerSec { get; set; }
    /// <summary>Disk read rate, bytes/sec, delta-computed independently of
    /// StorageWriteBytesPerSec so a counter reset in one direction does not
    /// suppress the other. Zero under the same conditions as
    /// StorageBytesPerSec.</summary>
    public double StorageReadBytesPerSec { get; set; }
    /// <summary>Disk write rate, bytes/sec - see StorageReadBytesPerSec.</summary>
    public double StorageWriteBytesPerSec { get; set; }
}

/// <summary>Lazily resolved publisher and signature status for one exe path,
/// served by ProcessMonitor.GetProcessMeta. Not a wire type itself - its two
/// fields flow into ProcessEntry.Publisher/Signed.</summary>
public sealed record ProcessMeta(string? Publisher, string Signed);
