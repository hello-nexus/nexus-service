using System.Collections.Generic;

namespace Nexus.Service.Models.Devices;

/// <summary>
/// A Logitech speaker's acoustics (GET /devices/logitech-speaker), as G HUB's Acoustics
/// and Surround sound pages show them. Band gains are dB (<see cref="BandMinDb"/> to
/// <see cref="BandMaxDb"/>), channel gains linear (1 = unchanged), volume 0-1.
/// </summary>
public sealed class LogitechSpeakerStateDto
{
    public bool Present { get; set; }
    public string? Name { get; set; }
    public string? Model { get; set; }
    public float? Volume { get; set; }
    /// <summary>The speaker's subwoofer level, 0-100; null when the speaker did not answer.</summary>
    public int? SubwooferLevel { get; set; }
    public bool EqEnabled { get; set; }
    public List<float> Bands { get; set; } = new();
    public List<int> BandHz { get; set; } = new();
    public float BandMinDb { get; set; }
    public float BandMaxDb { get; set; }
    /// <summary>The preset the bands came from; null once a band was moved by hand.</summary>
    public string? Preset { get; set; }
    public List<LogitechEqPresetDto> Presets { get; set; } = new();
    public bool SurroundEnabled { get; set; }
    /// <summary>"gaming" or "movie": DTS's room model.</summary>
    public string Room { get; set; } = "gaming";
    public List<string> Channels { get; set; } = new();
    public List<float> ChannelGains { get; set; } = new();
    public float ChannelMaxGain { get; set; }
}

public sealed class LogitechEqPresetDto
{
    public string Name { get; set; } = "";
    public List<float> Bands { get; set; } = new();
    public bool BuiltIn { get; set; }
}

/// <summary>POST /devices/logitech-speaker/eq: any of the three; a preset sets the bands from it.</summary>
public sealed class LogitechSpeakerEqRequest
{
    public bool? Enabled { get; set; }
    public List<float>? Bands { get; set; }
    public string? Preset { get; set; }
}

/// <summary>POST /devices/logitech-speaker/surround: DTS:X on or off, and its room ("gaming" or "movie").</summary>
public sealed class LogitechSpeakerSurroundRequest
{
    public bool? Enabled { get; set; }
    public string? Room { get; set; }
}

/// <summary>POST /devices/logitech-speaker/channel: one channel's gain (linear, 1 = unchanged); without a channel every channel goes back to 1.</summary>
public sealed class LogitechSpeakerChannelRequest
{
    /// <summary>An index into <see cref="LogitechSpeakerStateDto.Channels"/>.</summary>
    public int? Channel { get; set; }
    public float? Gain { get; set; }
}

/// <summary>POST /devices/logitech-speaker/volume (0-1) and /subwoofer (0-100).</summary>
public sealed class LogitechSpeakerLevelRequest
{
    public float? Level { get; set; }
}

/// <summary>POST /devices/logitech-speaker/presets: saves the bands as a preset of the user's own.</summary>
public sealed class LogitechSpeakerPresetRequest
{
    public string? Name { get; set; }
    public List<float>? Bands { get; set; }
}

/// <summary>POST /devices/logitech-speaker/test: a short sound from one channel.</summary>
public sealed class LogitechSpeakerTestRequest
{
    public int? Channel { get; set; }
}
