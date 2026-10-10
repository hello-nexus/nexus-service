using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.CorsairLink;

/// <summary>
/// Every iCUE LINK System Hub with a live connection session, in display-number order.
/// The connection worker adds a hub when its session starts and removes it when it ends.
/// </summary>
public sealed class CorsairLinkHubs
{
    private readonly object _lock = new();
    private volatile CorsairLinkHub[] _hubs = Array.Empty<CorsairLinkHub>();

    public IReadOnlyList<CorsairLinkHub> All => _hubs;

    public bool AnyConnected
    {
        get
        {
            foreach (var h in _hubs) if (h.IsConnected) return true;
            return false;
        }
    }

    /// <summary>The connected hub with the lowest display number, or null.</summary>
    public CorsairLinkHub? FirstConnected
    {
        get
        {
            foreach (var h in _hubs) if (h.IsConnected) return h;
            return null;
        }
    }

    /// <summary>The connected hub whose chain carries a pump LCD; one LCD is supported.</summary>
    public CorsairLinkHub? LcdHub
    {
        get
        {
            foreach (var h in _hubs) if (h.IsConnected && h.State.HasLcd) return h;
            return null;
        }
    }

    public CorsairLinkHub? Find(string hubId)
    {
        foreach (var h in _hubs) if (h.HubId == hubId) return h;
        return null;
    }

    /// <summary>Resolves "corsair:ch{N}" / "corsair:{serial}:ch{N}" (optionally ":temp") to its hub and channel.</summary>
    public bool TryResolve(string id, out CorsairLinkHub hub, out int channel)
    {
        hub = null!;
        channel = 0;
        if (!TryParseChannelId(id, out var prefix, out channel)) return false;
        foreach (var h in _hubs)
        {
            if (h.IdPrefix != prefix) continue;
            hub = h;
            return true;
        }
        return false;
    }

    internal static bool TryParseChannelId(string id, out string prefix, out int channel)
    {
        prefix = "";
        channel = 0;
        if (string.IsNullOrEmpty(id) || !id.StartsWith("corsair:", StringComparison.Ordinal)) return false;
        var chIdx = id.LastIndexOf(":ch", StringComparison.Ordinal);
        if (chIdx < 0) return false;
        prefix = id.Substring(0, chIdx + 1);
        var rest = id.AsSpan(chIdx + 3);
        var end = rest.IndexOf(':');
        if (end >= 0) rest = rest.Slice(0, end);
        return int.TryParse(rest, out channel) && channel > 0;
    }

    internal void Add(CorsairLinkHub hub)
    {
        lock (_lock)
        {
            var next = new List<CorsairLinkHub>(_hubs) { hub };
            next.Sort((a, b) => a.Number.CompareTo(b.Number));
            _hubs = next.ToArray();
        }
    }

    internal void Remove(CorsairLinkHub hub)
    {
        lock (_lock)
        {
            var next = new List<CorsairLinkHub>(_hubs);
            next.Remove(hub);
            _hubs = next.ToArray();
        }
    }
}
