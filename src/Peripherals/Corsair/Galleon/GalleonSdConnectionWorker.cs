using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.Corsair.Galleon;

/// <summary>Opens a Galleon 100 SD interface (the dials stand down while the yield handler is enabled) (keyboard, or the dial LampArray via <see cref="GalleonSdDialsHub"/>) while it is enumerated and its Nexus Control gate is on, closes it (board back in hardware mode) otherwise, and tells the lighting provider when that changes.</summary>
public sealed class GalleonSdConnectionWorker : BackgroundService
{
    private const int PollMs = 1000;
    internal const int OpenRetryMs = 10_000;

    private readonly IGalleonLightingHub _hub;
    private readonly HardwarePresence _presence;
    private readonly DeviceControlGate _gate;
    private readonly GalleonSdLightingDeviceProvider? _lighting;
    private readonly Func<long> _clock;
    private readonly int _productId;
    private readonly string? _yieldToHandlerId;

    internal int ProductId => _productId;
    private long _retryAt;
    private bool _openLastTick;
    private int _lastVersion;

    public GalleonSdConnectionWorker(IGalleonLightingHub hub, HardwarePresence presence, DeviceControlGate gate, GalleonSdLightingDeviceProvider? lighting = null, Func<long>? clock = null, int productId = GalleonSdProtocol.ProductId, string? yieldToHandlerId = null)
    {
        _hub = hub;
        _productId = productId;
        _yieldToHandlerId = yieldToHandlerId;
        _presence = presence;
        _gate = gate;
        _lighting = lighting;
        _clock = clock ?? (static () => Environment.TickCount64);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollMs));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { Tick(); }
            catch (Exception ex)
            {
                ServiceLog.Error($"[galleon-sd-conn] tick exception: {ex.GetType().Name}: {ex.Message}");
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        _hub.Disconnect();
    }

    internal void Tick()
    {
        var allowed = _gate.IsEnabled(GalleonSdProtocol.HandlerId)
            && (_yieldToHandlerId is null || !_gate.IsEnabled(_yieldToHandlerId))
            && _presence.UsbPresent(GalleonSdProtocol.VendorId, _productId);
        var now = _clock();
        var open = _hub.IsConnected;

        if (_openLastTick && !open && allowed) _retryAt = now + OpenRetryMs;
        if (open && !allowed)
        {
            _hub.Reconcile(false);
        }
        else if (!open && allowed && now >= _retryAt)
        {
            _hub.Reconcile(true);
            if (!_hub.IsConnected) _retryAt = now + OpenRetryMs;
        }
        else if (!allowed)
        {
            _retryAt = 0;
        }
        _openLastTick = _hub.IsConnected;

        var version = _hub.Version;
        if (version != _lastVersion)
        {
            _lastVersion = version;
            _lighting?.OnConnectionChanged();
        }
    }
}
