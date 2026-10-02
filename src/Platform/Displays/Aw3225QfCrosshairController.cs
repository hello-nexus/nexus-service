using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Displays;
using Nexus.Service.Persistence;

namespace Nexus.Service.Platform.Displays;

/// <summary>AW3225QF Gen1 AlienVision crosshair over DDC/CI (EC/ED).</summary>
public sealed class Aw3225QfCrosshairController
{
    private const byte EngineVcp = 0xEC;
    private const byte AdjustmentVcp = 0xED;
    private const int CrosshairEngine = 6;
    private readonly IDisplayBrightnessProvider _displays;
    private readonly IConfigStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _discoveryGate = new();
    private readonly Func<int, CancellationToken, Task> _delay;
    private readonly Func<bool> _hostSupported;
    private readonly Func<long> _clock;
    private string? _cachedDisplayId;
    private long? _discoveredAt;

    public Aw3225QfCrosshairController(IDisplayBrightnessProvider displays, IConfigStore store)
        : this(displays, store, Task.Delay, OperatingSystem.IsWindows, () => Environment.TickCount64) { }

    internal Aw3225QfCrosshairController(IDisplayBrightnessProvider displays, IConfigStore store,
        Func<int, CancellationToken, Task> delay, Func<bool> hostSupported, Func<long> clock)
    {
        _displays = displays;
        _store = store;
        _delay = delay;
        _hostSupported = hostSupported;
        _clock = clock;
    }

    public string? FindDisplayId()
    {
        if (!_hostSupported()) return null;
        lock (_discoveryGate)
        {
            var now = _clock();
            if (_discoveredAt is long at && now - at < 5000) return _cachedDisplayId;
            try
            {
                var disabled = _store.Load().Devices.DdcDisabledDisplays;
                _cachedDisplayId = _displays.Enumerate(disabled)
                    .FirstOrDefault(d => d.PhysicalDescription.Contains("AW3225QF", StringComparison.OrdinalIgnoreCase)
                         || d.Name.Contains("AW3225QF", StringComparison.OrdinalIgnoreCase))?.Id;
                _discoveredAt = now;
                return _cachedDisplayId;
            }
            catch
            {
                // Do not cache helper failures during login.
                _discoveredAt = null;
                return null;
            }
        }
    }

    public async Task<Aw3225QfCrosshairStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await ReadStatusAsync(FindDisplayId(), ct); }
        finally { _gate.Release(); }
    }

    public async Task<Aw3225QfCrosshairStatus> ApplyAsync(Aw3225QfCrosshairRequest request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var id = FindDisplayId();
            if (id is null) return Unavailable("AW3225QF is not connected.");
            if (IsDdcDisabled(id)) return Unavailable("DDC/CI is disabled for this display.", id);
            if (request.Type is < 0 or > 11 || request.Color is < 0 or > 5 || request.MaskControl is < 0 or > 2)
                return Unavailable("Invalid crosshair settings.", id);
            ct.ThrowIfCancellationRequested();
            // Finish a started transaction if the browser disconnects, so a
            // cancellation after EC=0 does not leave the crosshair switched off.
            ct = CancellationToken.None;

            if (!request.Enabled)
            {
                var engine = await ReadEngineAsync(id, ct);
                if (engine is null) return Unavailable("Could not read AlienVision state.", id);
                // Switching off another AlienVision mode would be surprising.
                if ((engine.Value & 0xF) == CrosshairEngine && !await WriteRetry(id, EngineVcp, 0, ct))
                    return Unavailable("Could not turn off the crosshair.", id);
            }
            else
            {
                var packed = (CrosshairEngine << 13) | (3 << 10)
                    | (request.MaskControl << 8) | (request.Type << 4) | (request.Color << 1);
                if (!await WriteRetry(id, EngineVcp, 0, ct))
                    return Unavailable("Could not prepare AlienVision.", id);
                await _delay(300, ct);
                if (!await WriteRetry(id, AdjustmentVcp, packed, ct))
                    return Unavailable("Could not set the crosshair.", id);
                await _delay(250, ct);
                if (!await WriteRetry(id, AdjustmentVcp, packed, ct))
                    return Unavailable("Could not set the crosshair.", id);
                await _delay(300, ct);
                if (!await WriteRetry(id, EngineVcp, CrosshairEngine, ct))
                    return Unavailable("Could not enable the crosshair.", id);
            }
            _store.Update(s =>
            {
                s.Devices.Aw3225QfCrosshairs[id] = new Aw3225QfCrosshairConfig
                {
                    Type = request.Type, Color = request.Color, MaskControl = request.MaskControl,
                };
                s.Devices.Aw3225QfCrosshair = null;
            });
            return await ReadStatusAsync(id, ct);
        }
        finally { _gate.Release(); }
    }

    private bool IsDdcDisabled(string id) => _store.Load().Devices.DdcDisabledDisplays.Contains(id);

    private Aw3225QfCrosshairConfig ConfigFor(string id)
    {
        var devices = _store.Load().Devices;
        var config = devices.Aw3225QfCrosshairs.TryGetValue(id, out var saved)
            ? saved : devices.Aw3225QfCrosshair ?? new();
        return new() { Type = config.Type, Color = config.Color, MaskControl = config.MaskControl };
    }

    private async Task<Aw3225QfCrosshairStatus> ReadStatusAsync(string? id, CancellationToken ct)
    {
        if (id is null) return Unavailable("AW3225QF is not connected.");
        if (IsDdcDisabled(id)) return Unavailable("DDC/CI is disabled for this display.", id);
        var engine = await ReadEngineAsync(id, ct);
        if (engine is null) return Unavailable("Could not read AlienVision state.", id);
        var active = engine.Value & 0xF;
        return new Aw3225QfCrosshairStatus
        {
            Connected = true, DisplayId = id, ActiveEngine = active,
            Enabled = active == CrosshairEngine,
            Config = ConfigFor(id),
        };
    }

    private async Task<bool> WriteRetry(string id, byte code, int value, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (IsDdcDisabled(id)) return false;
            if (_displays.SetVcp(id, code, value)) return true;
            if (attempt < 4) await _delay(120, ct);
        }
        return false;
    }

    private async Task<int?> ReadEngineAsync(string id, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var engine = _displays.GetVcp(id, EngineVcp);
            if (engine is not null) return engine.Value;
            if (attempt < 2) await _delay(120, ct);
        }
        return null;
    }

    private Aw3225QfCrosshairStatus Unavailable(string error, string? id = null) => new()
    {
        Error = error, Connected = id is not null, DisplayId = id ?? "",
        Config = id is not null ? ConfigFor(id) : new(),
    };
}
