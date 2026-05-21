using System;
using System.Collections.Generic;
using Qos.Service.Devices;
using Qos.Service.Models.Devices;

namespace Qos.Service.Lighting;

/// <summary>
/// Aggregates the OpenRGB-backed lighting devices (motherboard, RAM, AIO,
/// etc.) with NP50 hub devices behind a single <see cref="ILightingDeviceProvider"/>
/// so the existing /devices/lighting-devices/* routes and the React lighting
/// page don't need to know there's a second source. Routes by id prefix:
/// anything starting with <c>np50:</c> goes to the NP50 provider; everything
/// else stays on the OpenRGB provider.
///
/// Mirrors <see cref="Cooling.CompositeFanControlProvider"/> in spirit.
/// </summary>
public sealed class CompositeLightingDeviceProvider : ILightingDeviceProvider
{
    private readonly ILightingDeviceProvider _openRgb;
    private readonly Np50LightingDeviceProvider _np50;
    private readonly MiniHubLightingDeviceProvider _miniHub;

    public CompositeLightingDeviceProvider(
        ILightingDeviceProvider openRgb,
        Np50LightingDeviceProvider np50,
        MiniHubLightingDeviceProvider miniHub)
    {
        _openRgb = openRgb;
        _np50 = np50;
        _miniHub = miniHub;
    }

    public bool IsConnected => _openRgb.IsConnected || _np50.IsConnected || _miniHub.IsConnected;

    public GetLightingDevicesResponse GetAll()
    {
        var rgb = _openRgb.GetAll();

        // Filter out OpenRGB's NP50/MiniHub entries when our own providers
        // are live. qos-service now opens those COM ports exclusively for
        // hub control; OpenRGB's entries become zombies the animation
        // system can't push frames to.
        if (rgb.Devices.Count > 0)
        {
            if (_np50.IsConnected)
            {
                rgb.Devices.RemoveAll(d =>
                    d.Name.Contains("Nexus Portal NP50", StringComparison.OrdinalIgnoreCase) ||
                    d.Name.Contains("HYTE NP50", StringComparison.OrdinalIgnoreCase));
            }
            if (_miniHub.IsConnected)
            {
                rgb.Devices.RemoveAll(d =>
                    d.Name.Contains("MiniHub", StringComparison.OrdinalIgnoreCase) ||
                    d.Name.Contains("HYTE Mini", StringComparison.OrdinalIgnoreCase));
            }
        }

        var hub = _np50.GetAll();
        if (hub.Devices.Count > 0)
        {
            rgb.IsInit = rgb.IsInit || hub.IsInit;
            rgb.Devices.AddRange(hub.Devices);
        }
        var mini = _miniHub.GetAll();
        if (mini.Devices.Count > 0)
        {
            rgb.IsInit = rgb.IsInit || mini.IsInit;
            rgb.Devices.AddRange(mini.Devices);
        }
        return rgb;
    }

    public void SetDisabled(IReadOnlyList<string> ids)
    {
        // Per-id routing: split the ids and dispatch each batch to its owner.
        // Keeps each provider's "I own these ids" invariants intact.
        var rgbIds = new List<string>(ids.Count);
        var np50Ids = new List<string>(ids.Count);
        var miniIds = new List<string>(ids.Count);
        foreach (var id in ids)
        {
            if (IsNp50Id(id)) np50Ids.Add(id);
            else if (IsMiniHubId(id)) miniIds.Add(id);
            else rgbIds.Add(id);
        }
        if (rgbIds.Count > 0) _openRgb.SetDisabled(rgbIds);
        if (np50Ids.Count > 0) _np50.SetDisabled(np50Ids);
        if (miniIds.Count > 0) _miniHub.SetDisabled(miniIds);
    }

    public void SetPower(string id, bool on) { Pick(id).SetPower(id, on); }
    public void SetBrightness(string id, int brightness) { Pick(id).SetBrightness(id, brightness); }
    public void SetHue(string id, float hue) { Pick(id).SetHue(id, hue); }
    public void SetSaturation(string id, float saturation) { Pick(id).SetSaturation(id, saturation); }
    public void SetZoneLedCount(string id, int count) { Pick(id).SetZoneLedCount(id, count); }
    public void Identify(string id, int durationMs) { Pick(id).Identify(id, durationMs); }

    private ILightingDeviceProvider Pick(string id)
        => IsNp50Id(id) ? _np50 : IsMiniHubId(id) ? _miniHub : _openRgb;

    private static bool IsNp50Id(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("np50:", StringComparison.Ordinal);

    private static bool IsMiniHubId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("minihub:", StringComparison.Ordinal);
}
