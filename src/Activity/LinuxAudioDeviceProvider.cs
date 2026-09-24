using System;
using System.Collections.Generic;
using Nexus.Service.Models.Activity;
using Nexus.Service.Platform;

namespace Nexus.Service.Activity;

/// <summary>
/// Linux audio device enumeration + default switching via PulseAudio/PipeWire
/// <c>pactl</c>. Device id is the sink/source name; friendly name comes from the
/// block "Description". Best-effort: depends on a running PulseAudio/PipeWire
/// server with pactl available.
/// </summary>
public sealed class LinuxAudioDeviceProvider : IAudioDeviceProvider
{
    public AudioDeviceList ListDevices()
    {
        var list = new AudioDeviceList();
        if (!OperatingSystem.IsLinux()) return list;
        try
        {
            var defaultSink = ShellExecutor.Run("pactl", "get-default-sink").Trim();
            var defaultSource = ShellExecutor.Run("pactl", "get-default-source").Trim();
            list.Outputs.AddRange(Parse(ShellExecutor.Run("pactl", "list", "sinks"), "output", defaultSink));
            list.Inputs.AddRange(Parse(ShellExecutor.Run("pactl", "list", "sources"), "input", defaultSource));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio-linux] list failed: {ex.Message}");
        }
        return list;
    }

    public bool SetDefaultOutput(string deviceId) => Set("set-default-sink", deviceId);
    public bool SetDefaultInput(string deviceId) => Set("set-default-source", deviceId);
    public bool SetSpatial(string deviceId, string formatId) => false;

    private static bool Set(string verb, string id)
        => OperatingSystem.IsLinux() && !string.IsNullOrEmpty(id) && ShellExecutor.RunExit("pactl", 3000, verb, id) == 0;

    // Parse `pactl list sinks/sources` blocks: each device has a "Name:" line
    // (the stable id) and a "Description:" line (the friendly name).
    private static IEnumerable<AudioDevice> Parse(string output, string direction, string defaultId)
    {
        string? name = null;
        string? description = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Name:", StringComparison.Ordinal))
            {
                name = line["Name:".Length..].Trim();
            }
            else if (line.StartsWith("Description:", StringComparison.Ordinal))
            {
                description = line["Description:".Length..].Trim();
            }

            if (name is not null && description is not null)
            {
                yield return new AudioDevice
                {
                    Id = name,
                    Name = string.IsNullOrEmpty(description) ? name : description,
                    IsDefault = name == defaultId,
                    Direction = direction,
                };
                name = null;
                description = null;
            }
        }
    }
}

public sealed class StubAudioDeviceProvider : IAudioDeviceProvider
{
    public AudioDeviceList ListDevices() => new();
    public bool SetDefaultOutput(string deviceId) => false;
    public bool SetDefaultInput(string deviceId) => false;
    public bool SetSpatial(string deviceId, string formatId) => false;
}
