using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Activity;
using Nexus.Service.Fps;
using Nexus.Service.Models.Displays;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Platform.Displays;
using Nexus.Service.Sensors;

namespace Nexus.Service.Games;

/// <summary>
/// Follows the helper's own focus session with no boundary logic of its own:
/// its 1Hz poll loop opens a session whenever nothing is open and the
/// current focus resolves to a catalog game (start = now), samples
/// IFpsProvider.TryReadCurrentFps once per tick, and closes on a matching
/// IFocusDetailsProvider.SessionEnded (pid match only) or a mid-session
/// display mode change.
///
/// IFocusDetailsProvider.SessionEnded is raised synchronously from the
/// helper pipe's read loop, so its handler only enqueues the event and
/// returns; the poll loop drains the queue and does the actual close/persist
/// work on its own background task. Windows-only (the fps provider is), so
/// its registration is #if WINDOWS in NexusServiceCollectionExtensions.
/// </summary>
public sealed class FpsSessionRecorder : IHostedService, IDisposable
{
    // Re-checks the focus monitor's display mode this often (in poll ticks),
    // not every tick - Enumerate() round-trips to the helper on Windows.
    private const int ModeCheckEveryTicks = 5;

    private readonly IFpsProvider _fps;
    private readonly IFocusDetailsProvider _focusDetails;
    private readonly GameCatalog _catalog;
    private readonly IConfigStore _config;
    private readonly BinaryFpsSessionStore _store;
    private readonly IDisplayTopologyProvider _displays;
    private readonly ISensorProvider _sensors;
    private readonly Nexus.Service.FocusModes.FocusModeState _focus;

    private readonly object _lock = new();
    private readonly ConcurrentQueue<FocusSessionEnded> _pendingEnds = new();
    private TrackedSession? _open;
    private int _modeCheckCounter;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;

    public FpsSessionRecorder(
        IFpsProvider fps, IFocusDetailsProvider focusDetails,
        GameCatalog catalog, IConfigStore config, BinaryFpsSessionStore store,
        IDisplayTopologyProvider displays, ISensorProvider sensors,
        Nexus.Service.FocusModes.FocusModeState focus)
    {
        _fps = fps;
        _focusDetails = focusDetails;
        _catalog = catalog;
        _config = config;
        _store = store;
        _displays = displays;
        _sensors = sensors;
        _focus = focus;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _focusDetails.SessionEnded += OnFocusSessionEnded;
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _focusDetails.SessionEnded -= OnFocusSessionEnded;
        _cts?.Cancel();
        if (_pollTask is not null)
        {
            try { await _pollTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }

        TrackedSession? open;
        lock (_lock) { open = _open; _open = null; }
        if (open is not null)
        {
            FinalizeAndPersist(open, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    /// <summary>The session in progress as a record ending now, or null when nothing is being recorded; its Id is the one the finished session persists under.</summary>
    public FpsSessionRecord? SnapshotOpenSession()
    {
        lock (_lock)
        {
            var open = _open;
            if (open is null || open.ValidSec < 1)
            {
                return null;
            }

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var focusedSec = (int)Math.Max(0, (nowMs - open.StartedUtcMs) / 1000);
            // Hist is cloned because it escapes the lock; the tick thread keeps
            // writing into the live session's array.
            return new FpsSessionRecord(
                open.Id, open.GameKey, open.GameName, open.Store,
                open.StartedUtcMs, nowMs, focusedSec, open.ValidSec, open.Frames,
                open.MinFps == int.MaxValue ? 0 : open.MinFps, open.MaxFps, (uint[])open.Hist.Clone(),
                open.DispW, open.DispH, open.RefreshHz, open.WinW, open.WinH,
                open.WinW > 0 && open.WinH > 0 && open.WinW == open.DispW && open.WinH == open.DispH,
                false, 0, open.HardwareHash, FpsUploadState.Pending);
        }
    }

    public void Dispose() => _cts?.Dispose();

    // Called synchronously from the helper pipe's read loop
    // (HelperConnection.ReadLoopAsync -> WindowsScreenTimeProvider.OnEnvelope);
    // must stay O(1) and never block, or every helper round-trip stalls
    // behind it.
    private void OnFocusSessionEnded(FocusSessionEnded ended) => _pendingEnds.Enqueue(ended);

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                ProcessPendingEnds();

                if (!IsTrackingEnabled())
                {
                    lock (_lock) { _open = null; }
                    continue;
                }

                TrackedSession? open;
                lock (_lock) { open = _open; }

                if (open is null)
                {
                    TryOpenIfFocusedOnAGame();
                    continue;
                }

                AccumulateFpsForOpenSession(open);

                if (++_modeCheckCounter >= ModeCheckEveryTicks)
                {
                    _modeCheckCounter = 0;
                    CheckForModeChange(open);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void ProcessPendingEnds()
    {
        while (_pendingEnds.TryDequeue(out var ended))
        {
            TrackedSession? toClose = null;
            lock (_lock)
            {
                if (_open is not null && _open.Pid == ended.Pid)
                {
                    toClose = _open;
                    _open = null;
                }
            }
            if (toClose is not null)
            {
                FinalizeAndPersist(toClose, ended.EndedUtcMs);
            }
        }
    }

    // Samples the same rolling-window fps TryReadCurrentFps exposes for the
    // fps/current sensor, once per tick, instead of counting presents into
    // wall-clock-second buckets - DxgKrnl delivers presents in delayed,
    // batched ETW flushes, so a bucket that already advanced past a second
    // loses any frame arriving in a later batch for it.
    private void AccumulateFpsForOpenSession(TrackedSession open)
    {
        if (!_fps.TryReadCurrentFps(out var currentFps))
        {
            return;
        }

        var fps = (int)Math.Round(Math.Max(0, currentFps));
        if (!FpsSessionRules.IsValidFrameCount(fps))
        {
            return;
        }

        lock (_lock)
        {
            if (!ReferenceEquals(_open, open))
            {
                return;
            }
            open.ValidSec++;
            open.Frames += fps;
            FpsHistogram.AddSample(open.Hist, fps);
            ExactFpsCounts.Add(open.ExactCounts, fps);
            open.MinFps = Math.Min(open.MinFps, fps);
            open.MaxFps = Math.Max(open.MaxFps, fps);
        }
    }

    private void TryOpenIfFocusedOnAGame()
    {
        var details = _focusDetails.GetCurrentFocusDetails();
        if (details is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(details.ExePath))
        {
            _catalog.NotifyUnknownExe();
            return;
        }

        if (!_catalog.TryResolve(details.ExePath, out var identity))
        {
            _catalog.NotifyUnknownExe();
            return;
        }

        OpenSession(details, identity);
    }

    private void OpenSession(FocusDetails details, GameIdentity identity)
    {
        var (dispW, dispH, refreshHz) = ResolveDisplayMode(details.MonitorDevice);

        var open = new TrackedSession
        {
            Pid = details.Pid,
            GameKey = identity.GameKey,
            GameName = identity.Name,
            Store = identity.Store,
            StartedUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MonitorDevice = details.MonitorDevice,
            DispW = dispW,
            DispH = dispH,
            RefreshHz = refreshHz,
            WinW = details.WinW,
            WinH = details.WinH,
            HardwareHash = ComputeHardwareHash(),
        };

        lock (_lock)
        {
            _open = open;
            _modeCheckCounter = 0;
        }

        // The focus game trigger follows the process, not this session: the
        // session ends on the first alt-tab, while the game keeps running.
        _focus.NoteGameStarted(identity.GameKey, identity.Name, details.Pid);
    }

    // Resolution/Hz are part of the signature, so a mode change mid-session
    // closes and reopens rather than folding two resolutions into one row.
    private void CheckForModeChange(TrackedSession open)
    {
        var (dispW, dispH, refreshHz) = ResolveDisplayMode(open.MonitorDevice);
        if (dispW == 0 && dispH == 0)
        {
            return;
        }
        if (dispW == open.DispW && dispH == open.DispH && refreshHz == open.RefreshHz)
        {
            return;
        }

        TrackedSession? closed;
        lock (_lock)
        {
            if (!ReferenceEquals(_open, open))
            {
                return;
            }
            closed = _open;
            _open = null;
        }
        FinalizeAndPersist(closed, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        TryOpenIfFocusedOnAGame();
    }

    private void FinalizeAndPersist(TrackedSession open, long endedUtcMs)
    {
        var focusedSec = (int)Math.Max(0, (endedUtcMs - open.StartedUtcMs) / 1000);
        // Length gates the upload (MinFocusedSecToUpload), never the local row;
        // a session with no valid second measured no fps at all.
        if (open.ValidSec < 1)
        {
            return;
        }

        // Capped detection needs exact fps values: the persisted hist's
        // ~11%-wide log buckets can straddle a v-sync target (e.g. 60 Hz
        // spanning bucket edges 54/60), which flattens p90-p10 into "capped"
        // false negatives.
        var p10 = ExactFpsCounts.Percentile(open.ExactCounts, 10);
        var p50 = ExactFpsCounts.Percentile(open.ExactCounts, 50);
        var p90 = ExactFpsCounts.Percentile(open.ExactCounts, 90);
        var capped = open.ValidSec > 0 && p90 - p10 <= FpsSessionRules.CappedSpreadFps;

        var fullscreen = open.WinW > 0 && open.WinH > 0 && open.WinW == open.DispW && open.WinH == open.DispH;

        var record = new FpsSessionRecord(
            open.Id, open.GameKey, open.GameName, open.Store,
            open.StartedUtcMs, endedUtcMs, focusedSec, open.ValidSec, open.Frames,
            open.MinFps == int.MaxValue ? 0 : open.MinFps, open.MaxFps, open.Hist,
            open.DispW, open.DispH, open.RefreshHz, open.WinW, open.WinH,
            fullscreen, capped, capped ? p50 : 0, open.HardwareHash, FpsUploadState.Pending);

        try
        {
            _store.Append(record);
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[fps-session] persist failed: {ex.Message}");
        }
    }

    private (int Width, int Height, int RefreshHz) ResolveDisplayMode(string? monitorDevice)
    {
        if (string.IsNullOrEmpty(monitorDevice))
        {
            return (0, 0, 0);
        }
        try
        {
            var displays = _displays.Enumerate();
            if (displays is null)
            {
                return (0, 0, 0);
            }
            foreach (var d in displays)
            {
                if (string.Equals(d.Id, monitorDevice, StringComparison.Ordinal))
                {
                    return (d.ResolutionWidth, d.ResolutionHeight, d.RefreshHz);
                }
            }
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[fps-session] display topology query failed: {ex.Message}");
        }
        return (0, 0, 0);
    }

    private ulong ComputeHardwareHash()
    {
        try
        {
            var cpu = _sensors.GetCpuModel() ?? "";
            var gpu = Nexus.Service.Benchmarks.BenchmarkRunner.SelectReportedGpus(_sensors.GetGpus()).FirstOrDefault() ?? "";
            var mobo = _sensors.GetMotherboardModel() ?? "";
            var ramBytes = Nexus.Service.Benchmarks.BenchmarkRunner.ParseRamBytes(_sensors.GetMemoryTotalFormatted());
            return HardwareHash.Compute(cpu, gpu, mobo, ramBytes);
        }
        catch
        {
            return 0;
        }
    }

    private bool IsTrackingEnabled()
    {
        try { return _config.Load().Fps?.TrackingEnabled ?? true; }
        catch { return true; }
    }

    private sealed class TrackedSession
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required int Pid { get; init; }
        public required string GameKey { get; init; }
        public required string GameName { get; init; }
        public required string Store { get; init; }
        public required long StartedUtcMs { get; init; }
        public string? MonitorDevice { get; init; }
        public int DispW { get; init; }
        public int DispH { get; init; }
        public int RefreshHz { get; init; }
        public int WinW { get; init; }
        public int WinH { get; init; }
        public ulong HardwareHash { get; init; }
        public int ValidSec;
        // Sum of once-per-second sampled fps values, not a present count -
        // see FpsSessionRecord.Frames.
        public long Frames;
        public uint[] Hist { get; } = new uint[FpsHistogram.BucketCount];
        public uint[] ExactCounts { get; } = new uint[ExactFpsCounts.MaxFps + 1];
        public int MinFps = int.MaxValue;
        public int MaxFps;
    }
}

/// <summary>Deterministic 64-bit hash of a rig's stable identity fields
/// (cpu, primary gpu, motherboard, ram capacity), not a benchmark score.
/// Plain FNV-1a: fast and stable across runs and platforms.</summary>
public static class HardwareHash
{
    public static ulong Compute(string cpu, string gpu, string motherboard, long ramBytes)
    {
        var text = $"{cpu}|{gpu}|{motherboard}|{ramBytes}";
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }
}
