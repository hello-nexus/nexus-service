using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Benchmarks;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Benchmarks;

public enum BenchmarkState
{
    Idle,
    Running,
    Complete,
    Cancelled,
    Failed,
}

/// <summary>
/// Singleton orchestrator for a single benchmark run. Mirrors the
/// <c>CalibrationRunner</c> pattern: <see cref="Start"/> launches the run on a
/// background <see cref="Task"/> and returns immediately with a run id; the web
/// client polls <see cref="GetStatus"/> / <see cref="GetResult"/> and/or
/// subscribes to the <c>benchmark/{runId}</c> multiplex topic for progress
/// frames at ~500 ms.
///
/// Only one run can be in-flight at a time -- a second <see cref="Start"/>
/// returns <c>false</c> until the previous run transitions to Complete /
/// Cancelled / Failed. Callers can <see cref="Reset"/> afterwards to clear the
/// terminal state and start a new run.
/// </summary>
public sealed class BenchmarkRunner
{
    private readonly object _lock = new();
    private readonly IBenchmarkProvider _provider;
    private readonly ISensorProvider _sensors;
    private readonly SystemSpecsCollector _specs;
    private readonly MultiplexHub _hub;
    private Task? _task;
    private CancellationTokenSource? _cts;
    private BenchmarkProgressFrame? _lastFrame;

    public BenchmarkState State { get; private set; } = BenchmarkState.Idle;
    public string CurrentRunId { get; private set; } = "";
    public BenchmarkResult? Result { get; private set; }

    public BenchmarkRunner(IBenchmarkProvider provider, ISensorProvider sensors, SystemSpecsCollector specs, MultiplexHub hub)
    {
        _provider = provider;
        _sensors = sensors;
        _specs = specs;
        _hub = hub;
    }

    public string? Start(bool includeGpu)
    {
        lock (_lock)
        {
            if (State == BenchmarkState.Running)
                return null;
            var runId = Guid.NewGuid().ToString("N");
            CurrentRunId = runId;
            State = BenchmarkState.Running;
            Result = null;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _task = Task.Run(() => RunAsync(runId, includeGpu, ct));
            return runId;
        }
    }

    public bool Cancel(string runId)
    {
        lock (_lock)
        {
            if (State != BenchmarkState.Running || runId != CurrentRunId)
                return false;
            _cts?.Cancel();
            return true;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            if (State is BenchmarkState.Complete or BenchmarkState.Cancelled or BenchmarkState.Failed)
            {
                State = BenchmarkState.Idle;
                Result = null;
                _lastFrame = null;
            }
        }
    }

    public BenchmarkProgressFrame? GetStatus(string runId)
    {
        lock (_lock)
        {
            if (runId != CurrentRunId)
                return null;
            return _lastFrame;
        }
    }

    public BenchmarkResult? GetResult(string runId)
    {
        lock (_lock)
        {
            return Result is not null && Result.RunId == runId ? Result : null;
        }
    }

    private async Task RunAsync(string runId, bool includeGpu, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var subs = new System.Collections.Generic.List<BenchmarkSubScore>();
        var progressReporter = new Progress<BenchmarkPhaseProgress>(p => PushFrame(runId, p, subs));
        var (hardware, gpus) = await CollectHardwareAsync(ct);

        try
        {
            // CPU
            BroadcastPhaseStart(runId, "cpu", subs);
            var cpu = await _provider.RunCpuAsync(progressReporter, ct);
            subs.Add(cpu);

            // RAM
            BroadcastPhaseStart(runId, "ram", subs);
            var ram = await _provider.RunRamAsync(progressReporter, ct);
            subs.Add(ram);

            // Storage
            BroadcastPhaseStart(runId, "storage", subs);
            var storage = await _provider.RunStorageAsync(progressReporter, ct);
            subs.Add(storage);

            // GPU (optional)
            BenchmarkSubScore gpu;
            if (includeGpu)
            {
                BroadcastPhaseStart(runId, "gpu", subs);
                gpu = await _provider.RunGpuAsync(progressReporter, ct);
                if (!string.IsNullOrWhiteSpace(gpu.MeasuredDevice))
                {
                    // The GPU sub-score names the card clpeak/vkpeak actually
                    // measured; realign the hardware identity to it now that
                    // it is known (it was collected before this phase ran).
                    hardware.GpuModels = SelectReportedGpus(gpus, gpu.MeasuredDevice);
                }
            }
            else
            {
                gpu = new BenchmarkSubScore { Key = "gpu", Label = "GPU", Score = 0, Detail = "skipped" };
            }
            subs.Add(gpu);

            var composite = Scoring.Composite(cpu.Score, gpu.Score, ram.Score, storage.Score);
            lock (_lock)
            {
                Result = new BenchmarkResult
                {
                    RunId = runId,
                    State = "complete",
                    StartedAt = started,
                    FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Hardware = hardware,
                    Composite = composite,
                    Cpu = cpu,
                    Gpu = gpu,
                    Ram = ram,
                    Storage = storage,
                    ScoringVersion = Scoring.ScoringVersion,
                    Baselines = new Models.Benchmarks.BenchmarkBaselines
                    {
                        Cpu = Scoring.BaselineCpuPrimesPerSec,
                        Gpu = Scoring.BaselineGpuGflops,
                        Ram = Scoring.BaselineRamGbPerSec,
                        Storage = Scoring.BaselineStorageMbPerSec,
                    },
                    Tools = new System.Collections.Generic.Dictionary<string, string>(_provider.CollectedTools),
                };
                State = BenchmarkState.Complete;
                _lastFrame = new BenchmarkProgressFrame
                {
                    RunId = runId,
                    State = "complete",
                    OverallPercent = 1,
                    Phase = new BenchmarkPhaseProgress { Phase = "done", Percent = 1 },
                    CompletedSubScores = subs,
                };
            }
            await BroadcastFrameAsync(_lastFrame!);
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            { State = BenchmarkState.Cancelled; }
            await BroadcastStateAsync(runId, "cancelled", subs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[benchmark] run {runId} failed: {ex.Message}");
            lock (_lock)
            {
                State = BenchmarkState.Failed;
                Result = new BenchmarkResult
                {
                    RunId = runId,
                    State = "failed",
                    StartedAt = started,
                    FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Hardware = hardware,
                    Error = ex.Message,
                };
            }
            await BroadcastStateAsync(runId, "failed", subs);
        }
    }

    private async Task<(HardwareIdentity Hardware, System.Collections.Generic.IReadOnlyList<GpuReadout> Gpus)> CollectHardwareAsync(CancellationToken ct)
    {
        // RuntimeInformation.OSDescription reports the kernel version, which on
        // Windows 11 is "Microsoft Windows 10.0.<build>" (major.minor stays 10.0;
        // only build >= 22000 means 11), so it reads as Windows 10. Reuse the
        // specs collector's WMI Caption ("Windows 11 Pro (10.0.22631)") instead,
        // matching what /system/specs and the panel widget already show.
        string os = RuntimeInformation.OSDescription;
        try
        {
            var specs = await _specs.GetAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(specs.OsBuild))
                os = specs.OsBuild;
        }
        catch { /* fall back to OSDescription */ }

        try
        {
            // RamModel carries brand + part number (e.g. "Corsair
            // CMK16GX4M2B3000C15"), not capacity; the System Builder's fuzzy
            // matcher keys off it to locate the DIMM in the parts catalogue.
            // Capacity lives in RamBytes / /system/memory.
            var ramBrand = _sensors.GetRamBrandModel() ?? "";
            var storageBrand = _sensors.GetStorageBrandModel() ?? "";
            var ramBytes = ParseRamBytes(_sensors.GetMemoryTotalFormatted());
            var gpus = _sensors.GetGpus();
            var hardware = new HardwareIdentity
            {
                CpuModel = _sensors.GetCpuModel() ?? "",
                GpuModels = SelectReportedGpus(gpus),
                RamBytes = ramBytes,
                RamModel = ramBrand,
                StorageModel = storageBrand,
                LogicalCores = Environment.ProcessorCount,
                Os = os,
                Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            };
            return (hardware, gpus);
        }
        catch
        {
            return (new HardwareIdentity { LogicalCores = Environment.ProcessorCount },
                new System.Collections.Generic.List<GpuReadout>());
        }
    }

    /// <summary>
    /// Report only the card the GPU benchmark targets: the dedicated GPU when
    /// one is present, otherwise the integrated adapter. clpeak enumerates every
    /// OpenCL device and the GPU sub-score keeps the max, so the dedicated card
    /// is always the one measured. Mirrors the client's primary-GPU default
    /// (first discrete, else first) using the provider's authoritative
    /// <see cref="GpuReadout.Integrated"/> flag as the fallback - the WMI/LHM
    /// signal, not clpeak's device name (AMD can report a bare codename like
    /// "gfx1036" that shares no substring with the WMI-friendly name).
    /// <paramref name="measuredDevice"/>, when it matches one of the sensor
    /// entries, takes priority so two discrete GPUs report the one clpeak
    /// actually measured rather than always the first.
    /// </summary>
    internal static System.Collections.Generic.List<string> SelectReportedGpus(
        System.Collections.Generic.IReadOnlyList<GpuReadout> gpus, string? measuredDevice = null)
    {
        if (gpus.Count == 0)
            return new System.Collections.Generic.List<string>();

        var matched = MatchByMeasuredDevice(gpus, measuredDevice);
        if (matched is not null)
            return new System.Collections.Generic.List<string> { matched };

        foreach (var g in gpus)
        {
            if (!g.Integrated)
                return new System.Collections.Generic.List<string> { g.Name };
        }
        return new System.Collections.Generic.List<string> { gpus[0].Name };
    }

    /// <summary>Case-insensitive substring match either direction, tolerant of the WMI/clpeak naming mismatch. Returns the sensor-side name (never clpeak's raw string) so downstream formatting stays consistent.</summary>
    internal static string? MatchByMeasuredDevice(System.Collections.Generic.IReadOnlyList<GpuReadout> gpus, string? measuredDevice)
    {
        if (string.IsNullOrWhiteSpace(measuredDevice))
            return null;
        var needle = measuredDevice.Trim();
        foreach (var g in gpus)
        {
            var hay = g.Name?.Trim() ?? "";
            if (hay.Length == 0)
                continue;
            if (hay.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                needle.Contains(hay, StringComparison.OrdinalIgnoreCase))
            {
                return g.Name;
            }
        }
        return null;
    }

    /// <summary>
    /// GetMemoryTotalFormatted returns e.g. "16.0 GB" / "32.5 GB" / "1.5 TB".
    /// Parse back into bytes so the matcher can distinguish capacity tiers when
    /// the brand string alone is ambiguous. Returns 0 on unparseable input.
    /// </summary>
    internal static long ParseRamBytes(string formatted)
    {
        if (string.IsNullOrWhiteSpace(formatted))
            return 0;
        var m = System.Text.RegularExpressions.Regex.Match(
            formatted.Trim(),
            @"^(?<n>[0-9]+(?:\.[0-9]+)?)\s*(?<u>[KMGT]B)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
            return 0;
        if (!double.TryParse(m.Groups["n"].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
        {
            return 0;
        }

        long unit = m.Groups["u"].Value.ToUpperInvariant() switch
        {
            "KB" => 1024L,
            "MB" => 1024L * 1024,
            "GB" => 1024L * 1024 * 1024,
            "TB" => 1024L * 1024 * 1024 * 1024,
            _ => 0L,
        };
        return unit == 0 ? 0 : (long)(n * unit);
    }

    private void PushFrame(string runId, BenchmarkPhaseProgress phase, System.Collections.Generic.List<BenchmarkSubScore> completed)
    {
        var overall = ComputeOverall(phase);
        var frame = new BenchmarkProgressFrame
        {
            RunId = runId,
            State = "running",
            OverallPercent = overall,
            Phase = phase,
            CompletedSubScores = new System.Collections.Generic.List<BenchmarkSubScore>(completed),
        };
        lock (_lock)
        { _lastFrame = frame; }
        _ = BroadcastFrameAsync(frame);
    }

    private void BroadcastPhaseStart(string runId, string phase, System.Collections.Generic.List<BenchmarkSubScore> completed)
    {
        PushFrame(runId, new BenchmarkPhaseProgress { Phase = phase, Percent = 0 }, completed);
    }

    private async Task BroadcastStateAsync(string runId, string state, System.Collections.Generic.List<BenchmarkSubScore> completed)
    {
        var frame = new BenchmarkProgressFrame
        {
            RunId = runId,
            State = state,
            OverallPercent = state == "complete" ? 1 : 0,
            Phase = new BenchmarkPhaseProgress { Phase = state, Percent = 1 },
            CompletedSubScores = completed,
        };
        lock (_lock)
        { _lastFrame = frame; }
        await BroadcastFrameAsync(frame);
    }

    private async Task BroadcastFrameAsync(BenchmarkProgressFrame frame)
    {
        try
        {
            var topic = $"benchmark/{frame.RunId}";
            var envelope = WsEnvelope.Build(topic, frame, AppJsonContext.Default.BenchmarkProgressFrame);
            await _hub.BroadcastTopicAsync(topic, envelope);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[benchmark] broadcast failed: {ex.Message}");
        }
    }

    private static double ComputeOverall(BenchmarkPhaseProgress p)
    {
        // Rough planning weights - same order as run.
        return p.Phase switch
        {
            "cpu" => 0.00 + 0.25 * Math.Clamp(p.Percent, 0, 1),
            "ram" => 0.25 + 0.15 * Math.Clamp(p.Percent, 0, 1),
            "storage" => 0.40 + 0.25 * Math.Clamp(p.Percent, 0, 1),
            "gpu" => 0.65 + 0.35 * Math.Clamp(p.Percent, 0, 1),
            "done" => 1,
            _ => 0,
        };
    }
}
