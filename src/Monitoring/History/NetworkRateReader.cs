using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;

namespace Nexus.Service.Monitoring.History;

/// <summary>One byte/s rate reading; both null when no prior reading exists
/// yet to diff against, or the diff was rejected (see ComputeRate).</summary>
public readonly record struct NetworkRate(double? InBytesPerSec, double? OutBytesPerSec);

/// <summary>
/// System-wide IPv4 byte-rate gauge sampled by MetricsSampler each tick.
/// Sums cumulative BytesReceived/BytesSent across every non-loopback,
/// non-tunnel NIC and diffs against the previous read using
/// Environment.TickCount64 (monotonic, immune to wall-clock adjustments).
/// Independent of the gated per-process INetworkProvider (WindowsNetworkProvider
/// et al.), which churns process handles and TCP table reads on-demand - this
/// reader only touches NetworkInterface counters, a different I/O class.
/// The NIC list is re-enumerated every NicRefreshMs; GetIPv4Statistics on a
/// kept NetworkInterface reads the live counters. Down adapters stay in the
/// sum: their counters are constant, whereas admitting one only once it
/// comes up would add its whole cumulative count in a single tick.
/// </summary>
public sealed class NetworkRateReader
{
    // A reading gap wider than this (missed tick, system suspend) makes the
    // byte delta span more than the assumed 1-second window, so the read is
    // discarded instead of reported as a rate spike/trough.
    private const long MaxElapsedMs = 5000;

    // How long the enumerated NIC list is reused before it is refreshed to
    // pick up adapters added since (a hot-plugged USB NIC, a new VPN).
    private const long NicRefreshMs = 30_000;

    private readonly Func<NetworkInterface[]> _enumerate;
    private readonly Func<long> _nowTicks;

    private long _lastTicks = -1;
    private long _lastBytesIn;
    private long _lastBytesOut;

    private NetworkInterface[] _nics = Array.Empty<NetworkInterface>();
    private long _nicsRefreshedAtMs;
    private bool _nicsStale = true;

    public NetworkRateReader() : this(EnumerateNics, static () => Environment.TickCount64) { }

    /// <summary>Test seam: the adapter enumerator and the monotonic clock.</summary>
    internal NetworkRateReader(Func<NetworkInterface[]> enumerate, Func<long> nowTicks)
    {
        _enumerate = enumerate;
        _nowTicks = nowTicks;
    }

    public NetworkRate Read()
    {
        var nowTicks = _nowTicks();
        var (bytesIn, bytesOut) = ReadCumulativeCounters(nowTicks);
        var rate = ComputeRate(_lastTicks, _lastBytesIn, _lastBytesOut, nowTicks, bytesIn, bytesOut);
        _lastTicks = nowTicks;
        _lastBytesIn = bytesIn;
        _lastBytesOut = bytesOut;
        return rate;
    }

    /// <summary>Pure delta math: null on the first read (lastTicks &lt; 0),
    /// a non-positive or over-threshold elapsed gap (MaxElapsedMs), or a
    /// negative byte delta (NIC counter reset, e.g. adapter re-enumerated).</summary>
    internal static NetworkRate ComputeRate(
        long lastTicks, long lastBytesIn, long lastBytesOut,
        long nowTicks, long nowBytesIn, long nowBytesOut)
    {
        if (lastTicks < 0)
        {
            return new NetworkRate(null, null);
        }

        var elapsedMs = nowTicks - lastTicks;
        if (elapsedMs <= 0 || elapsedMs > MaxElapsedMs)
        {
            return new NetworkRate(null, null);
        }

        var deltaIn = nowBytesIn - lastBytesIn;
        var deltaOut = nowBytesOut - lastBytesOut;
        if (deltaIn < 0 || deltaOut < 0)
        {
            return new NetworkRate(null, null);
        }

        var seconds = elapsedMs / 1000.0;
        return new NetworkRate(deltaIn / seconds, deltaOut / seconds);
    }

    private (long BytesIn, long BytesOut) ReadCumulativeCounters(long nowTicks)
    {
        if (_nicsStale || nowTicks - _nicsRefreshedAtMs >= NicRefreshMs)
        {
            _nics = _enumerate();
            _nicsRefreshedAtMs = nowTicks;
            // An empty result (enumeration failed, or no adapter yet) is
            // retried next tick rather than held for the whole window.
            _nicsStale = _nics.Length == 0;
        }

        long bytesIn = 0;
        long bytesOut = 0;
        var failed = 0;
        foreach (var nic in _nics)
        {
            try
            {
                var stats = nic.GetIPv4Statistics();
                bytesIn += stats.BytesReceived;
                bytesOut += stats.BytesSent;
            }
            catch
            {
                // NIC vanished since the list was enumerated (USB adapter
                // unplugged) - skip it; the next refresh drops it.
                failed++;
            }
        }
        if (failed > 0 && failed == _nics.Length)
        {
            _nicsStale = true;
        }
        return (bytesIn, bytesOut);
    }

    private static NetworkInterface[] EnumerateNics()
    {
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return Array.Empty<NetworkInterface>();
        }

        var kept = new List<NetworkInterface>(all.Length);
        foreach (var nic in all)
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }
            kept.Add(nic);
        }
        return kept.ToArray();
    }
}
