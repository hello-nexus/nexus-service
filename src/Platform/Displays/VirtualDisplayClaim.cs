using System;

namespace Nexus.Service.Platform.Displays;

/// <summary>The density of the one live virtual display, held by the monitor that set it so a late dispose of an older monitor cannot clear a newer claim.</summary>
internal sealed class VirtualDisplayClaim
{
    private readonly object _lock = new();
    private object? _owner;
    private double? _dpi;

    public double? Dpi
    {
        get
        {
            lock (_lock)
            {
                return _dpi;
            }
        }
    }

    /// <summary>Raised after a claim is set.</summary>
    public event Action? Claimed;

    public void Set(object owner, double? dpi)
    {
        lock (_lock)
        {
            _owner = owner;
            _dpi = dpi;
        }
        Claimed?.Invoke();
    }

    public void Clear(object owner)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_owner, owner))
            {
                return;
            }
            _owner = null;
            _dpi = null;
        }
    }
}
