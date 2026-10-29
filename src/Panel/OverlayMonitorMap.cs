using System;
using System.Collections.Generic;
using Nexus.Service.Models.Panel;

namespace Nexus.Service.Panel;

/// <summary>
/// The overlay host's monitor index to display id map, posted by the Windows host. Only that host can number
/// monitors the way the legacy <c>Overlay.Monitor</c> index does. In memory: the host reposts at boot and on
/// every display change.
/// </summary>
public sealed class OverlayMonitorMap
{
    private readonly object _lock = new();
    private IReadOnlyList<OverlayMonitorDto>? _monitors;

    public void Set(IEnumerable<OverlayMonitorDto> monitors)
    {
        var copy = new List<OverlayMonitorDto>();
        foreach (var m in monitors)
        {
            if (string.IsNullOrWhiteSpace(m.DisplayId)) continue;
            copy.Add(new OverlayMonitorDto { Index = m.Index, DisplayId = m.DisplayId, Primary = m.Primary });
        }
        lock (_lock) _monitors = copy;
    }

    public bool HasMap
    {
        get { lock (_lock) return _monitors is { Count: > 0 }; }
    }

    public string? DisplayIdForIndex(int index)
    {
        lock (_lock)
        {
            if (_monitors is null) return null;
            foreach (var m in _monitors)
            {
                if (m.Index == index) return m.DisplayId;
            }
            return null;
        }
    }

    public string? PrimaryDisplayId()
    {
        lock (_lock)
        {
            if (_monitors is null) return null;
            foreach (var m in _monitors)
            {
                if (m.Primary) return m.DisplayId;
            }
            return null;
        }
    }
}
