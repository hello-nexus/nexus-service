using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Persistence;

namespace Nexus.Service.Telemetry;

/// <summary>Flushes <see cref="UsageTelemetrySink"/> to nexus-api on the heartbeat cadence; same consent gate as PostHog, except a pending opt_out is still delivered.</summary>
internal sealed class UsageTelemetryWorker : BackgroundService
{
    internal const int MaxBatch = 200;

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StopFlushTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly UsageTelemetrySink _sink;
    private readonly IUsageTransport _transport;
    private readonly IConfigStore _store;
    private readonly Action<string> _log;
    private int _lastDropStatus;

    public UsageTelemetryWorker(UsageTelemetrySink sink, IUsageTransport transport, IConfigStore store, Action<string>? log = null)
    {
        _log = log ?? Console.Error.WriteLine;
        _sink = sink;
        _transport = transport;
        _store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                if (!Nexus.Service.FocusModes.FocusNetworkGate.IsHeld)
                    await FlushAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[usage] flush failed: {ex.GetType().Name}: {ex.Message}");
            }
        } while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    internal async Task FlushAsync(CancellationToken ct)
    {
        var installId = InstallIdentity.Resolve(_store);
        if (installId is null)
        {
            _sink.RetainOnlyOptOut();
            installId = InstallIdentity.ResolveStored(_store);
            if (installId is null)
            {
                _sink.Clear();
                return;
            }
        }

        List<UsageEvent> batch;
        while ((batch = _sink.Take(MaxBatch)).Count > 0)
        {
            var (delivery, status) = await _transport.SendAsync(installId, BuildInfo.Version, batch, ct).ConfigureAwait(false);
            if (delivery == UsageDelivery.Retry)
            {
                _sink.Requeue(batch);
                return;
            }
            if (delivery == UsageDelivery.Drop && status != _lastDropStatus)
            {
                _lastDropStatus = status;
                _log($"[usage] batch of {batch.Count} rejected by nexus-api ({status}), dropped");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (Nexus.Service.FocusModes.FocusNetworkGate.IsHeld)
            return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(StopFlushTimeout);
        try { await FlushAsync(cts.Token).ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine($"[usage] final flush failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
