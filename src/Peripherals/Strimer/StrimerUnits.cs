using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Strimer;

/// <summary>Fixed pool of per-controller hubs. Slot 0 is the original single-unit hub.</summary>
public sealed class StrimerUnits
{
    public const int MaxUnits = 4;

    private readonly StrimerHub[] _hubs;

    public StrimerUnits()
    {
        _hubs = new StrimerHub[MaxUnits];
        for (var i = 0; i < MaxUnits; i++) _hubs[i] = new StrimerHub(i);
    }

    public StrimerUnits(StrimerHub primary) => _hubs = new[] { primary };

    public IReadOnlyList<StrimerHub> All => _hubs;

    public bool IsConnected
    {
        get
        {
            foreach (var h in _hubs)
            {
                if (h.IsConnected) return true;
            }
            return false;
        }
    }

    public List<StrimerHub> Connected()
    {
        var list = new List<StrimerHub>(_hubs.Length);
        foreach (var h in _hubs)
        {
            if (h.IsConnected) list.Add(h);
        }
        return list;
    }
}
