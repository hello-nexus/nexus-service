using System;
using Nexus.Service.Models.Activity;

namespace Nexus.Service.Activity;

/// <summary>
/// Resolves the media widget's volume-target modes (auto/app/output) to a
/// concrete output device or mixer strip. Reads <see cref="IAudioSessionProvider"/>
/// unfiltered, unlike <see cref="Nexus.Service.Audio.AudioMixerService"/>,
/// whose state and topic frames only ever show default-output apps.
/// </summary>
public sealed class VolumeTargetResolver
{
    private readonly IVolumeProvider _volume;
    private readonly IAudioDeviceProvider _devices;
    private readonly IAudioSessionProvider _sessions;

    public VolumeTargetResolver(IVolumeProvider volume, IAudioDeviceProvider devices, IAudioSessionProvider sessions)
    {
        _volume = volume;
        _devices = devices;
        _sessions = sessions;
    }

    public VolumeTargetState Resolve(string mode, string source, string deviceId)
    {
        var normalizedMode = (mode ?? "").Trim().ToLowerInvariant();
        return normalizedMode switch
        {
            "output" => ResolveOutput(deviceId ?? ""),
            "app" => ResolveApp(source ?? ""),
            _ => ResolveAuto(source ?? ""),
        };
    }

    private VolumeTargetState ResolveApp(string source)
    {
        var strip = FindStrip(source);
        if (strip is not null)
        {
            return new VolumeTargetState
            {
                Supported = true,
                Volume = strip.Volume,
                Muted = strip.Muted,
                Kind = "app",
                Id = strip.Id,
                Name = strip.Name,
            };
        }
        return ResolveAuto(source);
    }

    private VolumeTargetState ResolveAuto(string source)
    {
        var strip = FindStrip(source);
        if (strip is null) return ResolveOutput("");

        var list = _devices.ListDevices();
        var defaultOutput = FindDefaultOutput(list);
        if (!strip.OnDefault)
        {
            foreach (var deviceId in strip.DeviceIds)
            {
                var match = list.Outputs.Find(d => string.Equals(d.Id, deviceId, StringComparison.Ordinal));
                if (match is not null) return OutputState(match, defaultOutput);
            }
        }
        return OutputState(defaultOutput, defaultOutput);
    }

    private VolumeTargetState ResolveOutput(string deviceId)
    {
        var list = _devices.ListDevices();
        var defaultOutput = FindDefaultOutput(list);
        AudioDevice? device = null;
        if (deviceId.Length > 0)
            device = list.Outputs.Find(d => string.Equals(d.Id, deviceId, StringComparison.Ordinal));
        device ??= defaultOutput;
        return OutputState(device, defaultOutput);
    }

    private static AudioDevice? FindDefaultOutput(AudioDeviceList list) => list.Outputs.Find(d => d.IsDefault);

    /// <summary>Queries the default output with an empty id even when it was
    /// reached by an explicit id, so a transient read failure on the default
    /// device degrades the same way through this route as through the legacy
    /// /system/volume route (both fall back to "supported, unknown level"
    /// rather than "unsupported").</summary>
    private VolumeTargetState OutputState(AudioDevice? device, AudioDevice? defaultOutput)
    {
        var isDefault = device is null || (defaultOutput is not null && string.Equals(device.Id, defaultOutput.Id, StringComparison.Ordinal));
        var state = _volume.GetState(isDefault ? "" : device!.Id);
        return new VolumeTargetState
        {
            Supported = state.Supported,
            Volume = state.Volume,
            Muted = state.Muted,
            Kind = "output",
            Id = device?.Id ?? "",
            Name = device?.Name ?? "",
        };
    }

    private AudioSessionDto? FindStrip(string source) =>
        string.IsNullOrWhiteSpace(source) ? null : VolumeTargetMatcher.Match(source, _sessions.GetSessions());
}
