using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Activity.Native;
using Nexus.Service.Models.Activity;
using Nexus.Service.Sockets;

namespace Nexus.Service.Activity;

/// <summary>
/// Windows network monitor. Reads the TCP connection table in-process
/// (<c>GetExtendedTcpTable</c>) to discover which processes have active
/// connections to a non-loopback peer (loopback PIDs are dropped so the web
/// client / panel kiosk talking to nexus-service over localhost do not
/// appear as network users), then reads cumulative I/O byte counters via
/// the kernel process snapshot. Only samples when subscribers exist.
/// </summary>
public sealed class WindowsNetworkProvider : BackgroundService, INetworkProvider
{
    private volatile IReadOnlyList<NetworkProcessInfo> _snapshot = Array.Empty<NetworkProcessInfo>();
    private int _intervalMs = 1000;
    private readonly MultiplexHub _hub;

    public WindowsNetworkProvider(MultiplexHub hub) { _hub = hub; }

    public IReadOnlyList<NetworkProcessInfo> GetSnapshot(bool allowOnDemandSample = true) => _snapshot;
    public void SetInterval(int ms) => _intervalMs = Math.Max(200, ms);

    private bool HasSubscribers =>
        _hub.TopicHasSubscribers("network") || _hub.TopicHasSubscribers("monitoring");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(2000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (HasSubscribers) Sample();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[network-win] sample failed: {ex.Message}");
            }

            try { await Task.Delay(_intervalMs, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private void Sample()
    {
        // Step 1: PIDs whose TCP connections include a non-loopback peer.
        // Pure-loopback PIDs (nexus-service itself, the panel/Y70 launchers)
        // are dropped here so they never reach the per-process I/O counters.
        var activePids = new HashSet<int>();
        IpHlpApi.CollectInternetActivePids(activePids);
        if (activePids.Count == 0)
        {
            _snapshot = Array.Empty<NetworkProcessInfo>();
            return;
        }

        // Step 2: Resolve PIDs to process names and cumulative I/O counters from
        // one kernel snapshot, so no process handle is opened.
        var current = new Dictionary<string, (long bytesIn, long bytesOut)>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ProcessSnapshotEntry>(512);
        if (!WindowsProcessSnapshot.TryRead(entries))
        {
            return;
        }
        foreach (var e in entries)
        {
            if (!activePids.Contains(e.Pid))
            {
                continue;
            }
            if (current.TryGetValue(e.Name, out var prev))
                current[e.Name] = (prev.bytesIn + e.ReadTransferBytes, prev.bytesOut + e.WriteTransferBytes);
            else
                current[e.Name] = (e.ReadTransferBytes, e.WriteTransferBytes);
        }

        // Step 3: Store cumulative snapshot (frontend calculates rates from deltas)
        _snapshot = current
            .Where(kv => kv.Value.bytesIn + kv.Value.bytesOut > 0)
            .OrderByDescending(kv => kv.Value.bytesIn + kv.Value.bytesOut)
            .Select(kv => new NetworkProcessInfo
            {
                Name = kv.Key,
                BytesIn = kv.Value.bytesIn,
                BytesOut = kv.Value.bytesOut,
            })
            .ToList();
    }
}
