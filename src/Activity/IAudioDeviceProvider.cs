using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>
/// Enumerates audio output/input devices and switches the system default.
/// Used by the deck "switch audio device" action. Separate from
/// <see cref="IVolumeProvider"/> (which only touches the current default
/// endpoint's level/mute).
/// </summary>
public interface IAudioDeviceProvider
{
    AudioDeviceList ListDevices();
    /// <summary>Every endpoint id this PC has ever registered, including unplugged and disabled ones. Ids only, so it is cheap; empty when the provider cannot tell. Called on demand, not by <see cref="ListDevices"/>.</summary>
    AudioKnownEndpointIds ListKnownEndpointIds() => new();
    bool SetDefaultOutput(string deviceId);
    bool SetDefaultInput(string deviceId);
    /// <summary>Switches the endpoint's spatial sound format; empty turns it
    /// off. Only ids from <see cref="AudioDeviceList.Spatial"/> are accepted.</summary>
    bool SetSpatial(string deviceId, string formatId);
}
