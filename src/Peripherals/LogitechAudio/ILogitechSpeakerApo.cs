using System.Collections.Generic;

namespace Nexus.Service.Peripherals.LogitechAudio;

/// <summary>An active audio output that carries Logitech's speaker effect (a G560 and its kin).</summary>
public sealed record LogitechSpeakerEndpoint(string Id, string Name, string Model, bool IsDefault);

/// <summary>What the speaker effect reads from its registry key; null where the key has no (valid) value.</summary>
public sealed class LogitechSpeakerApoValues
{
    public int? SelectedEq { get; init; }
    public float[]? AdvancedBands { get; init; }
    public int? SelectedSurround { get; init; }
    public bool? DtsOn { get; init; }
    public int? DtsRoom { get; init; }
    public float[]? ChannelGains { get; init; }
}

/// <summary>A change to the speaker effect; null fields are left as they are.</summary>
public sealed class LogitechSpeakerApoChange
{
    public int? SelectedEq { get; init; }
    public float[]? AdvancedBands { get; init; }
    public int? SelectedSurround { get; init; }
    public bool? DtsOn { get; init; }
    public int? DtsRoom { get; init; }
    public float[]? ChannelGains { get; init; }
}

/// <summary>
/// The Windows side of Logitech's speaker effect: finding the outputs that carry it,
/// reading and writing its registry settings and making it reload them, the output's
/// volume, and a test sound on one speaker.
/// </summary>
public interface ILogitechSpeakerApo
{
    IReadOnlyList<LogitechSpeakerEndpoint> FindEndpoints();

    LogitechSpeakerApoValues Read(string endpointId);

    /// <summary>Writes the given values and signals the effect to reload each one. False when a write failed.</summary>
    bool Write(string endpointId, LogitechSpeakerApoChange change);

    /// <summary>The output's master volume, 0-1; null when it cannot be read.</summary>
    float? GetVolume(string endpointId);

    bool SetVolume(string endpointId, float level);

    /// <summary>Plays a short test sound on one channel (<see cref="LogitechSpeakerProtocol.Channels"/> index) of the output.</summary>
    bool PlayTestSound(string endpointId, int channel);
}

/// <summary>The speaker's own subwoofer level (HID++ 2.0 feature 0x8305), 0-100.</summary>
public interface ILogitechSpeakerBass
{
    /// <summary>Whether a speaker with the feature is connected; also tells a reconnect apart (<see cref="Generation"/>).</summary>
    bool IsPresent();

    /// <summary>Bumps every time the speaker is found again after it was gone (it re-enumerates on a USB drop).</summary>
    int Generation { get; }

    int? GetLevel();

    bool SetLevel(int level);
}
