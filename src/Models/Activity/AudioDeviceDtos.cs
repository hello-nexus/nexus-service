using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nexus.Service.Models;

namespace Nexus.Service.Models.Activity;

public sealed class AudioDevice
{
    /// <summary>Stable per-OS id (Windows endpoint id, macOS device UID, Linux sink/source name).</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsDefault { get; set; }
    /// <summary>"output" or "input".</summary>
    public string Direction { get; set; } = "output";
}

public sealed class AudioDeviceList : ApiResponse
{
    public List<AudioDevice> Outputs { get; set; } = new();
    public List<AudioDevice> Inputs { get; set; } = new();
    /// <summary>Saved user order of endpoint ids, including ones not currently detected.</summary>
    public List<string> OutputOrder { get; set; } = new();
    public List<string> InputOrder { get; set; } = new();
    /// <summary>Every endpoint id this PC has ever registered, including unplugged and disabled ones. Empty when the provider cannot tell; in-process only.</summary>
    [JsonIgnore]
    public List<string> KnownOutputIds { get; set; } = new();
    [JsonIgnore]
    public List<string> KnownInputIds { get; set; } = new();
    /// <summary>Spatial sound on the default output. Unsupported off Windows.</summary>
    public AudioSpatialState Spatial { get; set; } = new();
}

/// <summary>One spatial sound format Windows offers for an endpoint (Windows
/// Sonic, Dolby Atmos, DTS:X). Only formats the machine is licensed for are
/// listed; Windows shows the rest as store links, which the mixer does not.</summary>
public sealed class AudioSpatialFormat
{
    /// <summary>Encoder GUID, the id <see cref="SetAudioSpatialBody.FormatId"/> takes.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class AudioSpatialState
{
    /// <summary>False when the platform has no spatial switch, or the read failed.</summary>
    public bool Supported { get; set; }
    /// <summary>Endpoint the state describes.</summary>
    public string DeviceId { get; set; } = "";
    /// <summary>Encoder id of the active format; empty means off.</summary>
    public string ActiveId { get; set; } = "";
    public List<AudioSpatialFormat> Formats { get; set; } = new();
}

/// <summary>POST /system/audio/order body. <see cref="Ids"/> replaces the whole saved order for the direction.</summary>
public sealed class SetAudioDeviceOrderBody
{
    /// <summary>"output" or "input".</summary>
    public string Direction { get; set; } = "output";
    public List<string> Ids { get; set; } = new();
}

public sealed class SetAudioDefaultBody
{
    public string DeviceId { get; set; } = "";
}

/// <summary>POST /system/audio/spatial body. An empty <see cref="FormatId"/> turns spatial sound off.</summary>
public sealed class SetAudioSpatialBody
{
    public string DeviceId { get; set; } = "";
    public string FormatId { get; set; } = "";
}

/// <summary>POST /system/audio/play body - the touch deck widget's Play Audio press, mirroring the physical deck's playAudio DeckAction.</summary>
public sealed class PlayAudioBody
{
    public string Path { get; set; } = "";
    /// <summary>Playback volume percent, 0-100. Unset falls back to AudioFilePlayer's default.</summary>
    public int? Volume { get; set; }
}
