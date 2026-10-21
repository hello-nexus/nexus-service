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
    internal const int MaxRequestBytes = 512 * 1024;

    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StopFlushTimeout = TimeSpan.FromSeconds(3);
    // Two minutes keeps a steady stream at 30 requests/hour against nexus-api's 60/hour per-IP throttle (headroom for retries and a shared NAT); one minute would sit on the limit.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    private readonly UsageTelemetrySink _sink;
    private readonly IUsageTransport _transport;
    private readonly IConfigStore _store;
    private readonly Action<string> _log;
    private readonly string _queuePath;
    private int _lastDropStatus;

    public UsageTelemetryWorker(UsageTelemetrySink sink, IUsageTransport transport, IConfigStore store, Action<string>? log = null, string? queuePath = null)
    {
        _queuePath = queuePath ?? UsageQueueFile.DefaultPath();
        _log = log ?? Console.Error.WriteLine;
        _sink = sink;
        _transport = transport;
        _store = store;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Before the first capture can be flushed, so the restored tail goes out ahead of new events.
        try { _sink.Restore(_queuePath); }
        catch (Exception ex) { _log($"[usage] queue restore failed: {ex.GetType().Name}: {ex.Message}"); }
        return base.StartAsync(cancellationToken);
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

        var limit = MaxBatch;
        List<UsageEvent> batch;
        while ((batch = _sink.Take(limit)).Count > 0)
        {
            // The request body must stay under nexus-api's size limit; the tail goes back for the next request.
            var fit = FitBytes(batch);
            if (fit < batch.Count)
            {
                _sink.Requeue(batch.GetRange(fit, batch.Count - fit));
                batch = batch.GetRange(0, fit);
            }

            var (delivery, status) = await _transport.SendAsync(installId, BuildInfo.Version, batch, ct).ConfigureAwait(false);
            if (delivery == UsageDelivery.Retry)
            {
                _sink.Requeue(batch);
                return;
            }
            if (delivery == UsageDelivery.TooLarge && batch.Count > 1)
            {
                _sink.Requeue(batch);
                limit = Math.Max(1, batch.Count / 2);
                continue;
            }
            if (delivery is UsageDelivery.Drop or UsageDelivery.TooLarge && status != _lastDropStatus)
            {
                _lastDropStatus = status;
                _log($"[usage] batch of {batch.Count} rejected by nexus-api ({status}), dropped");
            }
        }
    }

    private static int FitBytes(List<UsageEvent> batch)
    {
        long total = 0;
        for (var i = 0; i < batch.Count; i++)
        {
            total += batch[i].Bytes;
            if (total > MaxRequestBytes && i > 0)
                return i;
        }
        return batch.Count;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (!Nexus.Service.FocusModes.FocusNetworkGate.IsHeld)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(StopFlushTimeout);
            try { await FlushAsync(cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { Console.Error.WriteLine($"[usage] final flush failed: {ex.GetType().Name}: {ex.Message}"); }
        }
        // Whatever the flush could not deliver (offline, focus mode, timeout) survives to the next start.
        _sink.Persist(_queuePath, !_store.Load().Telemetry.CollectAnonymousData);
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
