using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// The OpenRGB SDK connection. Per-controller operations take the controller's
/// <see cref="RgbDevice.Address"/>.
/// </summary>
public interface IRgbController : IAsyncDisposable
{
    bool IsConnected { get; }

    /// <summary>The daemon's device list changed, or a new connection came up.</summary>
    event Action? DeviceListChanged;

    /// <summary>True when the daemon starts a detection pass, false when it completes one.</summary>
    event Action<bool>? DetectionStateChanged;

    /// <summary>A detector ran: inside a pass, or alone when hotplug registers a device (no STARTED/COMPLETE around it).</summary>
    event Action? DetectionProgress;

    /// <summary>The daemon refused a write: controller address, packet id, ACK status.</summary>
    event Action<int, uint, uint>? WriteRejected;

    Task<bool> TryConnectAsync(CancellationToken ct = default);

    Task DisconnectAsync();

    /// <summary>Controller addresses in list order; touches no controller, so a controller stuck on its bus cannot stall it.</summary>
    Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default);

    Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default);

    Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default);

    Task SetOffAsync(int address, int ledCount, CancellationToken ct = default);

    Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default);

    Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default);

    /// <summary>Re-run detection inside the running daemon. False when the daemon did not accept it.</summary>
    Task<bool> RescanAsync(CancellationToken ct = default);

    /// <summary>Replace one top-level key of the daemon's live settings (memory only; the next detection reads it). False when not accepted.</summary>
    Task<bool> SetSettingsAsync(string key, string valueJson, CancellationToken ct = default);
}
