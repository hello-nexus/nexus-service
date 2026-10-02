using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// Cross-platform fallback used when no OpenRGB binary is bundled (macOS) or
/// when device support is otherwise unavailable. Always reports zero devices
/// and silently swallows all push operations.
/// </summary>
public sealed class NoOpRgbController : IRgbController
{
    public bool IsConnected => false;
    public event Action? DeviceListChanged { add { } remove { } }
    public event Action<bool>? DetectionStateChanged { add { } remove { } }
    public event Action? DetectionProgress { add { } remove { } }
    public event Action<int, uint, uint>? WriteRejected { add { } remove { } }

    public Task<bool> TryConnectAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task DisconnectAsync() => Task.CompletedTask;

    public Task<IReadOnlyList<int>> GetControllerAddressesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());

    public Task<IReadOnlyList<RgbDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RgbDevice>>(Array.Empty<RgbDevice>());

    public Task SetDirectModeAsync(RgbDevice device, CancellationToken ct = default) => Task.CompletedTask;
    public Task PushFrameAsync(int address, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetOffAsync(int address, int ledCount, CancellationToken ct = default) => Task.CompletedTask;
    public Task PushZoneFrameAsync(int address, int zoneIndex, ReadOnlyMemory<RgbColor> colors, CancellationToken ct = default) => Task.CompletedTask;
    public Task ResizeZoneAsync(int address, int zoneIndex, int newSize, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> RescanAsync(CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> SetSettingsAsync(string key, string valueJson, CancellationToken ct = default) => Task.FromResult(false);

    public ValueTask DisposeAsync() => default;
}
