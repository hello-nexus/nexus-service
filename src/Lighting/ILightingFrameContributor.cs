using System;
using System.Collections.Generic;
using Qos.Service.Lighting.Engine;

namespace Qos.Service.Lighting;

/// <summary>
/// Hook for non-OpenRGB lighting subsystems (NP50 today, future hubs)
/// to inject their per-zone <see cref="DeviceFrame"/>s into the engine's
/// device array. <see cref="Rgb.RgbBridge"/> queries every registered
/// contributor at the end of each refresh and appends their frames after
/// the OpenRGB-managed ones, so canvas sampling + brightness + identify
/// flashes apply uniformly across all surfaces.
///
/// Contributors fire <see cref="DevicesChanged"/> when their device list
/// shifts (hot-plug, port enumeration update) so the bridge can re-refresh
/// without polling.
/// </summary>
public interface ILightingFrameContributor
{
    /// <summary>
    /// Build DeviceFrames starting at <paramref name="startingIndex"/>.
    /// Each returned frame's <see cref="DeviceFrame.Index"/> must be
    /// startingIndex + N (N = position in the returned list). IDs should
    /// match the corresponding <c>LightingDevice.Id</c> so the lighting
    /// page's geometry / prefs / disabled state apply to the right frames.
    /// </summary>
    IReadOnlyList<DeviceFrame> BuildFrames(int startingIndex);

    /// <summary>Fired when the contributor's device list changes (e.g. NP50 hot-plug).</summary>
    event Action DevicesChanged;
}
