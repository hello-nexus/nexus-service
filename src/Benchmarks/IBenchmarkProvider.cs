using System;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Benchmarks;

namespace Nexus.Service.Benchmarks;

/// <summary>
/// Cross-platform contract for the four sub-benchmarks. Implementations must
/// honour <paramref name="progress"/> (report percent in the range [0, 1]),
/// respect cancellation, and never throw - on failure return a
/// <see cref="BenchmarkSubScore"/> with Score = 0 and a diagnostic in Detail.
/// All implementations are expected to be AOT-safe.
/// </summary>
public interface IBenchmarkProvider
{
    Task<BenchmarkSubScore> RunCpuAsync(IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct);
    Task<BenchmarkSubScore> RunRamAsync(IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct);
    Task<BenchmarkSubScore> RunStorageAsync(IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct);
    Task<BenchmarkSubScore> RunGpuAsync(IProgress<BenchmarkPhaseProgress> progress, CancellationToken ct);
    IReadOnlyDictionary<string, string> CollectedTools { get; }
}
