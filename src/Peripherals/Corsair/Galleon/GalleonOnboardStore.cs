using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.Corsair.Galleon;

/// <summary>
/// Per-serial onboard state behind <see cref="IConfigStore"/>, or in memory when no store is wired (unit tests).
/// Records handed out are working copies; the stored record is replaced whole on every save.
/// </summary>
public sealed class GalleonOnboardStore
{
    private readonly IConfigStore? _store;
    private Dictionary<string, GalleonOnboardRecord> _memory = new();

    public GalleonOnboardStore(IConfigStore? store) => _store = store;

    public GalleonOnboardRecord? Get(string serial)
    {
        var map = _store is null ? _memory : _store.Load().Devices.GalleonOnboard;
        return map.TryGetValue(serial, out var rec) ? rec.Clone() : null;
    }

    public void Save(string serial, GalleonOnboardRecord record, bool flush = false)
    {
        var copy = record.Clone();
        if (_store is null)
        {
            _memory = new Dictionary<string, GalleonOnboardRecord>(_memory) { [serial] = copy };
            return;
        }
        _store.Update(s => s.Devices.GalleonOnboard = new Dictionary<string, GalleonOnboardRecord>(s.Devices.GalleonOnboard) { [serial] = copy });
        if (flush) _store.FlushNow();
    }
}
