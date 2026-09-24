using System;
using System.Collections.Generic;
using System.Threading;
using Nexus.Service.Activity;
using Nexus.Service.Models.Activity;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Nexus.Service.Sockets;

namespace Nexus.Service.Audio;

/// <summary>Live per-app strips from <see cref="IAudioSessionProvider"/>, plus
/// levels remembered across an app restart and named preset sets.</summary>
public sealed class AudioMixerService : IDisposable
{
    private readonly IAudioSessionProvider _sessions;
    private readonly IVolumeProvider _volume;
    private readonly IAudioDeviceProvider _devices;
    private readonly IConfigStore _store;
    private readonly MultiplexHub _hub;
    private readonly object _lock = new();
    /// <summary>Strip ids seen in the last snapshot. A sticky level is applied on
    /// the absent-to-present edge only, so re-applying never fights a user (or
    /// the app itself) who moves the level afterwards.</summary>
    private readonly HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);
    private long _configRevision;

    public AudioMixerService(
        IAudioSessionProvider sessions,
        IVolumeProvider volume,
        IAudioDeviceProvider devices,
        IConfigStore store,
        MultiplexHub hub)
    {
        _sessions = sessions;
        _volume = volume;
        _devices = devices;
        _store = store;
        _hub = hub;
        _sessions.SessionsChanged += OnSessionsChanged;
    }

    public void Dispose() => _sessions.SessionsChanged -= OnSessionsChanged;

    public AudioMixerState GetState()
    {
        var settings = _store.Load().AudioMixer;
        return new AudioMixerState
        {
            Supported = _sessions.Supported,
            Sessions = DefaultOutputSessions(_sessions.GetSessions()),
            StickyLevels = settings.StickyLevels,
            Presets = new List<AudioMixerPresetDto>(settings.Presets),
        };
    }

    public void SetVolume(string id, double volume, bool commit)
    {
        if (id.Length == 0) return;
        var clamped = Math.Clamp(volume, 0, 1);
        _sessions.SetVolume(id, clamped);
        if (commit) RememberLevel(id, level => level.Volume = clamped);
    }

    public void SetMuted(string id, bool muted)
    {
        if (id.Length == 0) return;
        _sessions.SetMuted(id, muted);
        RememberLevel(id, level => level.Muted = muted);
    }

    public void SetSticky(bool enabled)
    {
        _store.Update(s => s.AudioMixer.StickyLevels = enabled);
        BumpConfig();
    }

    /// <summary>Drops every remembered level.</summary>
    public void ClearLevels()
    {
        lock (_lock) { _store.Update(s => s.AudioMixer.Levels.Clear()); }
        BumpConfig();
    }

    /// <summary>Saved preset, or why it was refused. Overwriting an existing
    /// preset is always allowed; only a new one can hit the cap.</summary>
    public sealed record SavePresetResult(AudioMixerPresetDto? Preset, string Error);

    /// <summary>Refusal reasons, so a caller can tell them apart without parsing prose.</summary>
    public const string ErrorNoName = "preset needs a name";
    public const string ErrorLimitReached = "preset limit reached";
    public const string ErrorDuplicateName = "a preset already has that name";
    public const string ErrorNoSuchPreset = "no such preset";

    public SavePresetResult SavePreset(SaveAudioMixerPresetBody body)
    {
        var name = ClampName(body.Name);
        if (name.Length == 0) return new SavePresetResult(null, ErrorNoName);

        var apps = body.Apps ?? CaptureRunningApps();
        var preset = new AudioMixerPresetDto
        {
            Id = body.Id.Length > 0 ? body.Id : Guid.NewGuid().ToString("N")[..8],
            Name = name,
            MasterVolume = body.IncludeMaster is false ? null : CaptureMaster(),
            Apps = apps,
        };
        if (body.IncludeDevices is not false) CaptureDevices(preset);

        // Checked inside Update (which holds the store lock) rather than against
        // a prior Load(), so two panels saving the same name cannot both pass.
        var error = "";
        _store.Update(s =>
        {
            var index = s.AudioMixer.Presets.FindIndex(p => p.Id == preset.Id);
            if (index < 0 && s.AudioMixer.Presets.Count >= MaxPresets)
            {
                error = ErrorLimitReached;
                return;
            }
            if (HasName(s.AudioMixer.Presets, name, preset.Id))
            {
                error = ErrorDuplicateName;
                return;
            }
            if (index >= 0) s.AudioMixer.Presets[index] = preset;
            else s.AudioMixer.Presets.Add(preset);
        });
        if (error.Length > 0) return new SavePresetResult(null, error);
        BumpConfig();
        return new SavePresetResult(preset, "");
    }

    /// <summary>A preset is picked by name on the tile, so two that read the
    /// same are indistinguishable there. Stored names are already trimmed and
    /// clamped, so a plain case-insensitive compare is the whole test.</summary>
    private static bool HasName(List<AudioMixerPresetDto> presets, string name, string exceptId)
    {
        foreach (var p in presets)
        {
            if (p.Id == exceptId) continue;
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Longest preset name kept. Chips sit in a row on a 4x4 tile, so a
    /// long name would either wrap or push its neighbours off.</summary>
    public const int MaxPresetNameLength = 10;

    /// <summary>Most presets kept. The tile pages them by measured width, so a
    /// larger set is more paging than anyone will click through.</summary>
    public const int MaxPresets = 10;

    /// <summary>Renames in place; the captured levels and devices are untouched.
    /// Returns "" on success, else the refusal reason.</summary>
    public string RenamePreset(string id, string name)
    {
        var trimmed = ClampName(name);
        if (id.Length == 0 || trimmed.Length == 0) return ErrorNoName;
        var error = ErrorNoSuchPreset;
        _store.Update(s =>
        {
            if (HasName(s.AudioMixer.Presets, trimmed, id))
            {
                error = ErrorDuplicateName;
                return;
            }
            foreach (var p in s.AudioMixer.Presets)
            {
                if (p.Id != id) continue;
                p.Name = trimmed;
                error = "";
                break;
            }
        });
        if (error.Length == 0) BumpConfig();
        return error;
    }

    /// <summary>TrimEnd after the cut as well: a name clipped mid-word would
    /// otherwise keep a trailing space, which renders as a different name than
    /// it compares as.</summary>
    private static string ClampName(string name)
    {
        var stripped = new string(Array.FindAll(name.ToCharArray(), c => !char.IsControl(c)));
        var trimmed = stripped.Trim();
        return trimmed.Length <= MaxPresetNameLength ? trimmed : trimmed[..MaxPresetNameLength].TrimEnd();
    }

    public bool DeletePreset(string id)
    {
        var removed = false;
        _store.Update(s => removed = s.AudioMixer.Presets.RemoveAll(p => p.Id == id) > 0);
        if (removed) BumpConfig();
        return removed;
    }

    /// <summary>Writes the live strips and the remembered levels, so an app that
    /// is closed when the preset is picked still opens at the preset's level.</summary>
    public bool ApplyPreset(string id)
    {
        AudioMixerPresetDto? preset = null;
        foreach (var p in _store.Load().AudioMixer.Presets)
        {
            if (p.Id == id) { preset = p; break; }
        }
        if (preset is null) return false;

        if (preset.OutputDeviceId.Length > 0) _devices.SetDefaultOutput(preset.OutputDeviceId);
        if (preset.InputDeviceId.Length > 0) _devices.SetDefaultInput(preset.InputDeviceId);

        if (preset.MasterVolume is double master)
        {
            _volume.SetVolume(Math.Clamp(master, 0, 1));
            PanelTopics.BroadcastVolume(_hub);
        }

        foreach (var app in preset.Apps)
        {
            if (app.Id.Length == 0) continue;
            var clamped = Math.Clamp(app.Volume, 0, 1);
            _sessions.SetVolume(app.Id, clamped);
            _sessions.SetMuted(app.Id, app.Muted);
        }

        lock (_lock)
        {
            _store.Update(s =>
            {
                foreach (var app in preset.Apps)
            {
                    if (app.Id.Length == 0) continue;
                    s.AudioMixer.Levels[app.Id] = new AudioMixerLevelDto
                    {
                        Name = app.Name,
                        Volume = Math.Clamp(app.Volume, 0, 1),
                        Muted = app.Muted,
                    };
                }
            });
        }
        // Bump rather than a plain broadcast: applying can move the endpoints,
        // and the revision is what tells every open mixer to re-read them
        // instead of waiting out its device poll.
        BumpConfig();
        return true;
    }

    /// <summary>The default output or input moved, so open mixers must re-read
    /// the device list rather than wait out their poll.</summary>
    public void NotifyEndpointsChanged() => BumpConfig();

    /// <summary>Gated on the audio/mixer topic having subscribers (AppBootstrap).</summary>
    public void StartStreaming() => _sessions.StartStreaming();

    public void StopStreaming() => _sessions.StopStreaming();

    private List<AudioMixerPresetEntryDto> CaptureRunningApps()
    {
        var apps = new List<AudioMixerPresetEntryDto>();
        foreach (var s in DefaultOutputSessions(_sessions.GetSessions()))
        {
            apps.Add(new AudioMixerPresetEntryDto
            {
                Id = s.Id,
                Name = s.Name,
                Volume = s.Volume,
                Muted = s.Muted,
            });
        }
        return apps;
    }

    private void CaptureDevices(AudioMixerPresetDto preset)
    {
        AudioDeviceList list;
        try { list = _devices.ListDevices(); }
        catch { return; }
        foreach (var d in list.Outputs)
        {
            if (!d.IsDefault) continue;
            preset.OutputDeviceId = d.Id;
            preset.OutputDeviceName = d.Name;
            break;
        }
        foreach (var d in list.Inputs)
        {
            if (!d.IsDefault) continue;
            preset.InputDeviceId = d.Id;
            preset.InputDeviceName = d.Name;
            break;
        }
    }

    private double? CaptureMaster()
    {
        var state = _volume.GetState();
        return state.Supported ? Math.Clamp(state.Volume, 0, 1) : null;
    }

    private void RememberLevel(string id, Action<AudioMixerLevelDto> mutate)
    {
        var name = "";
        foreach (var s in _sessions.GetSessions())
        {
            if (!string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
            name = s.Name;
            break;
        }

        lock (_lock)
        {
            _store.Update(s =>
            {
                if (!s.AudioMixer.Levels.TryGetValue(id, out var level))
                {
                    level = new AudioMixerLevelDto { Volume = 1 };
                    s.AudioMixer.Levels[id] = level;
                }
                if (name.Length > 0) level.Name = name;
                mutate(level);
            });
        }
    }

    private void OnSessionsChanged()
    {
        ApplyStickyToNewSessions();
        Broadcast();
    }

    private void ApplyStickyToNewSessions()
    {
        var sessions = DefaultOutputSessions(_sessions.GetSessions());

        // Every read of and write to AudioMixer.Levels goes through _lock: this
        // runs on the helper-push thread at up to 10Hz while route threads
        // mutate the same dictionary, and a TryGetValue racing a resize
        // corrupts it. _lock is always taken OUTSIDE the store's own lock.
        List<(string Id, AudioMixerLevelDto Level)>? pending = null;
        lock (_lock)
        {
            var settings = _store.Load().AudioMixer;
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sessions) live.Add(s.Id);
            _present.RemoveWhere(id => !live.Contains(id));

            foreach (var s in sessions)
            {
                // Add() is the edge test: false means we already saw this strip.
                if (!_present.Add(s.Id)) continue;
                if (!settings.StickyLevels) continue;
                if (!settings.Levels.TryGetValue(s.Id, out var level)) continue;
                (pending ??= new List<(string, AudioMixerLevelDto)>()).Add((s.Id, level));
            }
        }
        if (pending is null) return;

        foreach (var (id, level) in pending)
        {
            _sessions.SetVolume(id, Math.Clamp(level.Volume, 0, 1));
            _sessions.SetMuted(id, level.Muted);
        }
    }

    private void BumpConfig()
    {
        Interlocked.Increment(ref _configRevision);
        Broadcast();
    }

    private void Broadcast()
    {
        if (!_hub.TopicHasSubscribers(PanelTopics.AudioMixer)) return;
        var frame = new AudioMixerFrame
        {
            Supported = _sessions.Supported,
            Sessions = DefaultOutputSessions(_sessions.GetSessions()),
            ConfigRevision = Interlocked.Read(ref _configRevision),
        };
        var env = WsEnvelope.Build(PanelTopics.AudioMixer, frame, AppJsonContext.Default.AudioMixerFrame);
        _ = _hub.BroadcastTopicAsync(PanelTopics.AudioMixer, env);
    }

    /// <summary>The mixer widget only lists apps rendering to the default
    /// output; the volume-target resolver reads every strip unfiltered.</summary>
    private static List<AudioSessionDto> DefaultOutputSessions(IReadOnlyList<AudioSessionDto> sessions)
    {
        var result = new List<AudioSessionDto>();
        foreach (var s in sessions)
        {
            if (s.OnDefault) result.Add(s);
        }
        return result;
    }
}
