using System.Collections.Generic;
using Nexus.Service.Models;

namespace Nexus.Service.Models.Activity;

/// <summary>One mixer strip. Windows opens a session per stream, so a strip
/// collapses every session of one process name and writes to all of them.</summary>
public sealed class AudioSessionDto
{
    /// <summary>Process name, lowercased, no extension ("chrome"). Survives a
    /// relaunch, which is what makes it the sticky-level and preset key.
    /// <see cref="AudioMixerIds.SystemSounds"/> for the pid-0 system session.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>0-1, same scale as <see cref="VolumeState.Volume"/>.</summary>
    public double Volume { get; set; }
    public bool Muted { get; set; }
    /// <summary>0-1 peak across the group's sessions, for the level meter.</summary>
    public double Peak { get; set; }
    /// <summary>False once every session in the group has gone Inactive: the app
    /// still holds the endpoint but is rendering nothing.</summary>
    public bool Active { get; set; }
    /// <summary>Render endpoint ids this strip has a non-expired session on,
    /// across every active output, not just the default one.</summary>
    public List<string> DeviceIds { get; set; } = new();
    /// <summary>True when one of those sessions is on the default eConsole
    /// endpoint. Defaults true so a snapshot from an older helper build (no
    /// such field) still shows every strip rather than hiding it from the
    /// default-output-filtered mixer.</summary>
    public bool OnDefault { get; set; } = true;
}

/// <summary>Well-known strip ids that are not process names.</summary>
public static class AudioMixerIds
{
    /// <summary>The pid-0 session Windows renders notification sounds through.</summary>
    public const string SystemSounds = "@system";
}

public sealed class AudioMixerState : ApiResponse
{
    /// <summary>False on macOS/Linux, and on Windows until the user-session
    /// helper connects - Session 0 cannot see the user's audio sessions.</summary>
    public bool Supported { get; set; }
    public List<AudioSessionDto> Sessions { get; set; } = new();
    /// <summary>Whether a level set here is remembered and re-applied the next
    /// time that app opens a session.</summary>
    public bool StickyLevels { get; set; }
    public List<AudioMixerPresetDto> Presets { get; set; } = new();
}

public sealed class AudioMixerPresetDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>System output level 0-1. Null leaves the master alone.</summary>
    public double? MasterVolume { get; set; }
    /// <summary>Default render endpoint to switch to. Empty leaves it alone; an
    /// id for a device that is no longer present simply fails to apply.</summary>
    public string OutputDeviceId { get; set; } = "";
    /// <summary>Label captured at save time, so the preset reads as itself even
    /// when that device is unplugged.</summary>
    public string OutputDeviceName { get; set; } = "";
    public string InputDeviceId { get; set; } = "";
    public string InputDeviceName { get; set; } = "";
    public List<AudioMixerPresetEntryDto> Apps { get; set; } = new();
}

public sealed class AudioMixerPresetEntryDto
{
    /// <summary>Process-name key, same space as <see cref="AudioSessionDto.Id"/>.</summary>
    public string Id { get; set; } = "";
    /// <summary>Label captured when the preset was saved, so a preset lists
    /// readable names for apps that are not running.</summary>
    public string Name { get; set; } = "";
    public double Volume { get; set; }
    public bool Muted { get; set; }
}

/// <summary>A remembered level, re-applied the next time that app opens a session.</summary>
public sealed class AudioMixerLevelDto
{
    /// <summary>Label captured when the level was set, so a remembered app that
    /// is not running still reads as itself.</summary>
    public string Name { get; set; } = "";
    public double Volume { get; set; }
    public bool Muted { get; set; }
}

/// <summary>Live frame on the <c>audio/mixer</c> topic. Strips ride inline; a
/// revision-and-refetch frame at meter rate would be one HTTP GET per sample.</summary>
public sealed class AudioMixerFrame
{
    public bool Supported { get; set; }
    public List<AudioSessionDto> Sessions { get; set; } = new();
    /// <summary>Bumped when presets or sticky change. Subscribers refetch
    /// GET /system/audio/mixer when it moves.</summary>
    public long ConfigRevision { get; set; }
}

public sealed class SetSessionVolumeBody
{
    public string Id { get; set; } = "";
    public double Volume { get; set; }
    /// <summary>False is a mid-drag frame: it moves the level without a store
    /// write. Unset counts as true.</summary>
    public bool? Commit { get; set; }
}

public sealed class SetSessionMutedBody
{
    public string Id { get; set; } = "";
    public bool Muted { get; set; }
}

public sealed class AudioMixerStickyBody
{
    public bool Enabled { get; set; }
}

/// <summary>Create (empty <see cref="Id"/>) or overwrite a preset.</summary>
public sealed class SaveAudioMixerPresetBody
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>False stores no master, so applying leaves the system volume
    /// alone. Unset counts as true.</summary>
    public bool? IncludeMaster { get; set; }
    /// <summary>False stores no output/input device, so applying leaves the
    /// endpoints alone. Unset counts as true.</summary>
    public bool? IncludeDevices { get; set; }
    /// <summary>Unset captures every strip that is currently running.</summary>
    public List<AudioMixerPresetEntryDto>? Apps { get; set; }
}

public sealed class AudioMixerPresetIdBody
{
    public string Id { get; set; } = "";
}

/// <summary>Rename in place, leaving the captured levels and devices alone.</summary>
public sealed class RenameAudioMixerPresetBody
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}
