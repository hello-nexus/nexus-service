using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nexus.Service.Sensors;

/// <summary>
/// Fills the <see cref="SystemSpecsCollector"/> cache in the background after
/// the host reports ready, so the first Devices → System Specs request hits a
/// warm cache instead of paying for the native hardware reads.
///
/// Must not delay host startup. <see cref="Task.Yield"/> hands control back to
/// <c>BackgroundService.StartAsync</c> immediately so the host completes boot
/// and <c>/ping</c> responds while the prewarm runs on a thread-pool thread.
/// </summary>
public sealed class SystemSpecsPrewarmService : BackgroundService
{
    private readonly SystemSpecsCollector _collector;
    private readonly ILogger<SystemSpecsPrewarmService> _logger;

    public SystemSpecsPrewarmService(SystemSpecsCollector collector, ILogger<SystemSpecsPrewarmService> logger)
    {
        _collector = collector;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield once so StartAsync returns and the rest of host start can
        // finish. Without this the await chain below would run on the startup
        // thread before /ping is reachable.
        await Task.Yield();
        if (stoppingToken.IsCancellationRequested) return;

        var sw = Stopwatch.StartNew();
        try
        {
            // GetAsync awaits the sensor provider's readiness (Windows: the
            // LHM background-open task; Mac/Linux: an already-completed task),
            // so a single Build runs once hardware is fully enumerated. No
            // polling, no IsCacheable check.
            await _collector.GetAsync(stoppingToken);
            _logger.LogInformation("SystemSpecs prewarm completed in {Ms} ms.", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Service shutting down.
        }
        catch (Exception ex)
        {
            // Never fatal - the next /system/specs request will rebuild on
            // demand. Logged so a silent regression is discoverable.
            _logger.LogWarning(ex, "SystemSpecs prewarm failed; first request will pay cold start.");
        }
    }
}
