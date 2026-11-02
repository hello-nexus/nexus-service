using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Models.Devices;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.LogitechAudio;

/// <summary>
/// G HUB's Acoustics and Surround sound pages for a Logitech speaker (the G560), without
/// G HUB: the EQ, DTS surround and channel volumes go to Logitech's speaker effect the way
/// G HUB sends them, the subwoofer level to the speaker over HID++. The speaker forgets
/// its subwoofer level when it drops off USB, so the level set here is sent again each
/// time it comes back, as G HUB does.
/// </summary>
public sealed class LogitechSpeakerService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EndpointCacheTime = TimeSpan.FromSeconds(3);

    private readonly ILogitechSpeakerApo _apo;
    private readonly ILogitechSpeakerBass _bass;
    private readonly IConfigStore _store;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private IReadOnlyList<LogitechSpeakerEndpoint> _endpoints = Array.Empty<LogitechSpeakerEndpoint>();
    private DateTimeOffset _endpointsAt = DateTimeOffset.MinValue;
    // Serializes the subwoofer conversations (reconnect resend vs a user change) and guards _restoredGeneration.
    private readonly object _bassLock = new();
    private int _restoredGeneration;
    // Settings used while the dev-tools simulator is active, so nothing it does reaches the saved ones.
    private LogitechSpeakerSettings _simSettings = new();
    private bool _simulated;
    private bool _resyncGeneration;
    // The subwoofer level as last read or set, per connection: pages poll the state every
    // few seconds and the speaker does not need a HID++ question each time.
    private int? _subwooferLevel;
    private int _subwooferGeneration;

    public LogitechSpeakerService(ILogitechSpeakerApo apo, ILogitechSpeakerBass bass, IConfigStore store)
        : this(apo, bass, store, TimeProvider.System)
    {
    }

    internal LogitechSpeakerService(ILogitechSpeakerApo apo, ILogitechSpeakerBass bass, IConfigStore store, TimeProvider time)
    {
        _apo = apo;
        _bass = bass;
        _store = store;
        _time = time;
    }

    /// <summary>Drops the cached output scan so the next read sees the outputs as they are now.</summary>
    internal void ForgetEndpoints()
    {
        lock (_lock)
        {
            _endpointsAt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>The model of the speaker whose output carries Logitech's speaker effect ("G560"), or null when there is none.</summary>
    public string? PresentModel() => Endpoint()?.Model;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, _time);
        do
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                ServiceLog.Warn($"[logitech-speaker] check failed: {ex.Message}");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Sends the subwoofer level again once the speaker is back after a drop (or at start).</summary>
    internal void Tick()
    {
        SyncSimulation();
        if (!_bass.IsPresent())
        {
            return;
        }
        lock (_bassLock)
        {
            var generation = _bass.Generation;
            if (_resyncGeneration)
            {
                // The simulator just stopped: the real speaker keeps the level it has.
                _resyncGeneration = false;
                _restoredGeneration = generation;
                return;
            }
            if (generation == _restoredGeneration)
            {
                return;
            }
            var level = ReadSettings(s => s.SubwooferLevel);
            if (level is null)
            {
                _restoredGeneration = generation;
                return;
            }
            if (_bass.SetLevel(level.Value))
            {
                _restoredGeneration = generation;
                lock (_lock)
                {
                    _subwooferLevel = level;
                    _subwooferGeneration = generation;
                }
                ServiceLog.Info($"[logitech-speaker] subwoofer level {level} sent to the speaker");
            }
        }
    }

    public LogitechSpeakerStateDto GetState()
    {
        SyncSimulation();
        var state = new LogitechSpeakerStateDto
        {
            BandHz = LogitechSpeakerProtocol.BandHz.ToList(),
            BandMinDb = LogitechSpeakerProtocol.BandMinDb,
            BandMaxDb = LogitechSpeakerProtocol.BandMaxDb,
            Channels = LogitechSpeakerProtocol.Channels.ToList(),
            ChannelMaxGain = LogitechSpeakerProtocol.ChannelMaxGain,
            Presets = Presets(),
        };
        var endpoint = Endpoint(fresh: true);
        if (endpoint is null)
        {
            state.Bands = Enumerable.Repeat(0f, LogitechSpeakerProtocol.BandCount).ToList();
            state.ChannelGains = Enumerable.Repeat(1f, LogitechSpeakerProtocol.ChannelCount).ToList();
            return state;
        }

        var values = _apo.Read(endpoint.Id);
        state.Present = true;
        state.Simulated = _simulated;
        state.Name = endpoint.Name;
        state.Model = endpoint.Model;
        state.Volume = _apo.GetVolume(endpoint.Id);
        state.SubwooferLevel = SubwooferLevel();
        state.EqEnabled = values.SelectedEq == LogitechSpeakerProtocol.EqAdvanced;
        state.Bands = (values.AdvancedBands ?? new float[LogitechSpeakerProtocol.BandCount]).ToList();
        state.Preset = PresetMatching(ReadSettings(s => s.EqPreset), state.Bands);
        state.SurroundEnabled = values.DtsOn == true;
        state.Room = values.DtsRoom == LogitechSpeakerProtocol.Rooms.Movie ? "movie" : "gaming";
        state.ChannelGains = (values.ChannelGains ?? Enumerable.Repeat(1f, LogitechSpeakerProtocol.ChannelCount).ToArray()).ToList();
        return state;
    }

    /// <summary>
    /// Turns the ten-band EQ on or off and/or sets its bands, from <paramref name="bands"/>
    /// or from the preset named <paramref name="preset"/>. Bands set by hand clear the
    /// preset name, as G HUB's do.
    /// </summary>
    public SpeakerResult SetEq(bool? enabled, IReadOnlyList<float>? bands, string? preset)
    {
        var endpoint = Endpoint(fresh: true);
        if (endpoint is null)
        {
            return SpeakerStatus.NotFound;
        }
        float[]? newBands = null;
        if (bands is not null)
        {
            if (bands.Count != LogitechSpeakerProtocol.BandCount)
            {
                return SpeakerStatus.Invalid;
            }
            newBands = bands.ToArray();
        }
        else if (preset is not null)
        {
            newBands = FindPreset(preset);
            if (newBands is null)
            {
                return SpeakerStatus.NotFound;
            }
        }

        var change = new LogitechSpeakerApoChange
        {
            AdvancedBands = newBands,
            SelectedEq = enabled is null
                ? (newBands is null ? null : LogitechSpeakerProtocol.EqAdvanced)
                : (enabled.Value ? LogitechSpeakerProtocol.EqAdvanced : LogitechSpeakerProtocol.EqOff),
        };
        if (!_apo.Write(endpoint.Id, change))
        {
            return SpeakerStatus.Failed;
        }
        if (newBands is not null)
        {
            UpdateSettings(s => s.EqPreset = bands is null ? preset : null);
        }
        return SpeakerStatus.Ok;
    }

    /// <summary>DTS surround on or off, and its room (gaming or movie).</summary>
    public SpeakerResult SetSurround(bool? enabled, string? room)
    {
        var endpoint = Endpoint(fresh: true);
        if (endpoint is null)
        {
            return SpeakerStatus.NotFound;
        }
        int? roomModel = room switch
        {
            null => null,
            "gaming" => LogitechSpeakerProtocol.Rooms.Gaming,
            "movie" => LogitechSpeakerProtocol.Rooms.Movie,
            _ => -1,
        };
        if (roomModel == -1)
        {
            return SpeakerStatus.Invalid;
        }
        return _apo.Write(endpoint.Id, new LogitechSpeakerApoChange
        {
            SelectedSurround = enabled == true ? LogitechSpeakerProtocol.SurroundDts : null,
            DtsOn = enabled,
            DtsRoom = roomModel,
        }) ? SpeakerStatus.Ok : SpeakerStatus.Failed;
    }

    /// <summary>One channel's gain (<see cref="LogitechSpeakerProtocol.Channels"/> index), or all of them back to 1 when <paramref name="channel"/> is null.</summary>
    public SpeakerResult SetChannelGain(int? channel, float gain)
    {
        var endpoint = Endpoint(fresh: true);
        if (endpoint is null)
        {
            return SpeakerStatus.NotFound;
        }
        if (channel is < 0 or >= LogitechSpeakerProtocol.ChannelCount || !float.IsFinite(gain))
        {
            return SpeakerStatus.Invalid;
        }
        float[] gains;
        if (channel is null)
        {
            gains = Enumerable.Repeat(1f, LogitechSpeakerProtocol.ChannelCount).ToArray();
        }
        else
        {
            gains = _apo.Read(endpoint.Id).ChannelGains ?? Enumerable.Repeat(1f, LogitechSpeakerProtocol.ChannelCount).ToArray();
            gains[channel.Value] = Math.Clamp(gain, LogitechSpeakerProtocol.ChannelMinGain, LogitechSpeakerProtocol.ChannelMaxGain);
        }
        return _apo.Write(endpoint.Id, new LogitechSpeakerApoChange { ChannelGains = gains }) ? SpeakerStatus.Ok : SpeakerStatus.Failed;
    }

    public SpeakerResult SetSubwoofer(int level)
    {
        SyncSimulation();
        var clamped = Math.Clamp(level, 0, 100);
        lock (_bassLock)
        {
            if (!_bass.IsPresent())
            {
                return SpeakerStatus.NotFound;
            }
            if (!_bass.SetLevel(clamped))
            {
                return SpeakerStatus.Failed;
            }
            UpdateSettings(s => s.SubwooferLevel = clamped);
            var generation = _bass.Generation;
            _restoredGeneration = generation;
            _resyncGeneration = false;
            lock (_lock)
            {
                _subwooferLevel = clamped;
                _subwooferGeneration = generation;
            }
        }
        return SpeakerStatus.Ok;
    }

    private int? SubwooferLevel()
    {
        if (!_bass.IsPresent())
        {
            return null;
        }
        lock (_lock)
        {
            var generation = _bass.Generation;
            if (_subwooferLevel is null || _subwooferGeneration != generation)
            {
                _subwooferLevel = _bass.GetLevel();
                _subwooferGeneration = generation;
            }
            return _subwooferLevel;
        }
    }

    public SpeakerResult SetVolume(float level)
    {
        var endpoint = Endpoint(fresh: true);
        if (endpoint is null)
        {
            return SpeakerStatus.NotFound;
        }
        if (!float.IsFinite(level))
        {
            return SpeakerStatus.Invalid;
        }
        return _apo.SetVolume(endpoint.Id, Math.Clamp(level, 0f, 1f)) ? SpeakerStatus.Ok : SpeakerStatus.Failed;
    }

    public SpeakerResult TestChannel(int channel)
    {
        var endpoint = Endpoint(fresh: true);
        if (endpoint is null)
        {
            return SpeakerStatus.NotFound;
        }
        if (channel is < 0 or >= LogitechSpeakerProtocol.ChannelCount)
        {
            return SpeakerStatus.Invalid;
        }
        return _apo.PlayTestSound(endpoint.Id, channel) ? SpeakerStatus.Ok : SpeakerStatus.Failed;
    }

    /// <summary>Saves (or replaces) one of the user's presets. Built-in names are taken.</summary>
    public SpeakerResult SavePreset(string name, IReadOnlyList<float> bands)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 40 || bands.Count != LogitechSpeakerProtocol.BandCount || bands.Any(b => !float.IsFinite(b))
            || LogitechSpeakerProtocol.BuiltInPresets.Any(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return SpeakerStatus.Invalid;
        }
        var clamped = bands.Select(b => Math.Clamp(b, LogitechSpeakerProtocol.BandMinDb, LogitechSpeakerProtocol.BandMaxDb)).ToList();
        UpdateSettings(s =>
        {
            var presets = s.CustomPresets;
            presets.RemoveAll(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            presets.Add(new LogitechEqPreset { Name = trimmed, Bands = clamped });
        });
        return SpeakerStatus.Ok;
    }

    public SpeakerResult DeletePreset(string name)
    {
        var removed = false;
        UpdateSettings(s =>
        {
            removed = s.CustomPresets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed && string.Equals(s.EqPreset, name, StringComparison.OrdinalIgnoreCase))
            {
                s.EqPreset = null;
            }
        });
        return removed ? SpeakerStatus.Ok : SpeakerStatus.NotFound;
    }

    // Detects the simulator starting or stopping; its settings stay apart from the saved ones.
    private void SyncSimulation()
    {
        var active = _apo is ILogitechSpeakerSimulation { Active: true };
        lock (_lock)
        {
            if (active == _simulated)
            {
                return;
            }
            _simulated = active;
            _simSettings = new LogitechSpeakerSettings();
            _subwooferLevel = null;
            _endpointsAt = DateTimeOffset.MinValue;
            _resyncGeneration = !active;
        }
    }

    private T ReadSettings<T>(Func<LogitechSpeakerSettings, T> read)
    {
        SyncSimulation();
        lock (_lock)
        {
            if (_simulated)
            {
                return read(_simSettings);
            }
        }
        T result = default!;
        _store.Read(s => result = read(s.LogitechSpeaker));
        return result;
    }

    private void UpdateSettings(Action<LogitechSpeakerSettings> change)
    {
        SyncSimulation();
        lock (_lock)
        {
            if (_simulated)
            {
                change(_simSettings);
                return;
            }
        }
        _store.Update(s => change(s.LogitechSpeaker));
    }

    private List<LogitechEqPresetDto> Presets()
    {
        var list = LogitechSpeakerProtocol.BuiltInPresets
            .Select(p => new LogitechEqPresetDto { Name = p.Name, Bands = p.Bands.ToList(), BuiltIn = true })
            .ToList();
        list.AddRange(ReadSettings(s => s.CustomPresets
            .Where(p => p.Bands.Count == LogitechSpeakerProtocol.BandCount)
            .Select(p => new LogitechEqPresetDto { Name = p.Name, Bands = p.Bands.ToList() })
            .ToList()));
        return list;
    }

    // The stored preset name holds only while the effect still carries its bands (G HUB may have moved them).
    private string? PresetMatching(string? name, IReadOnlyList<float> bands)
    {
        if (name is null || FindPreset(name) is not { } preset)
        {
            return null;
        }
        for (var i = 0; i < preset.Length; i++)
        {
            if (Math.Abs(preset[i] - bands[i]) > 0.01f)
            {
                return null;
            }
        }
        return name;
    }

    private float[]? FindPreset(string name)
    {
        foreach (var (presetName, bands) in LogitechSpeakerProtocol.BuiltInPresets)
        {
            if (string.Equals(presetName, name, StringComparison.OrdinalIgnoreCase))
            {
                return bands.ToArray();
            }
        }
        return ReadSettings(s => s.CustomPresets
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Bands.Count == LogitechSpeakerProtocol.BandCount)
            ?.Bands.ToArray());
    }

    // The default output when it carries the effect, else the first by id. Presence checks
    // run every few seconds, so the registry scan is cached briefly.
    private LogitechSpeakerEndpoint? Endpoint(bool fresh = false)
    {
        SyncSimulation();
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (fresh || now - _endpointsAt >= EndpointCacheTime)
            {
                _endpoints = _apo.FindEndpoints();
                _endpointsAt = now;
            }
            return _endpoints.FirstOrDefault(e => e.IsDefault) ?? _endpoints.FirstOrDefault();
        }
    }
}

public enum SpeakerStatus
{
    Ok,
    /// <summary>The request itself is malformed.</summary>
    Invalid,
    /// <summary>No speaker, or no such preset.</summary>
    NotFound,
    /// <summary>The speaker or its effect did not take a valid change.</summary>
    Failed,
}

/// <summary>The outcome of a change; converts to true only for <see cref="SpeakerStatus.Ok"/>.</summary>
public readonly record struct SpeakerResult(SpeakerStatus Status)
{
    public static implicit operator SpeakerResult(SpeakerStatus status) => new(status);

    public static implicit operator bool(SpeakerResult result) => result.Status == SpeakerStatus.Ok;
}
