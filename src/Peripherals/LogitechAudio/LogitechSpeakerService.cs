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
    private int _restoredGeneration;
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
        if (!_bass.IsPresent())
        {
            return;
        }
        var generation = _bass.Generation;
        if (generation == _restoredGeneration)
        {
            return;
        }
        var level = _store.Load().LogitechSpeaker.SubwooferLevel;
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

    public LogitechSpeakerStateDto GetState()
    {
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
        var settings = _store.Load().LogitechSpeaker;
        state.Present = true;
        state.Name = endpoint.Name;
        state.Model = endpoint.Model;
        state.Volume = _apo.GetVolume(endpoint.Id);
        state.SubwooferLevel = SubwooferLevel();
        state.EqEnabled = values.SelectedEq == LogitechSpeakerProtocol.EqAdvanced;
        state.Bands = (values.AdvancedBands ?? new float[LogitechSpeakerProtocol.BandCount]).ToList();
        state.Preset = settings.EqPreset;
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
    public bool SetEq(bool? enabled, IReadOnlyList<float>? bands, string? preset)
    {
        var endpoint = Endpoint(fresh: true);
        if (endpoint is null)
        {
            return false;
        }
        float[]? newBands = null;
        if (bands is not null)
        {
            if (bands.Count != LogitechSpeakerProtocol.BandCount)
            {
                return false;
            }
            newBands = bands.ToArray();
        }
        else if (preset is not null)
        {
            newBands = FindPreset(preset);
            if (newBands is null)
            {
                return false;
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
            return false;
        }
        if (newBands is not null)
        {
            _store.Update(s => s.LogitechSpeaker.EqPreset = bands is null ? preset : null);
        }
        return true;
    }

    /// <summary>DTS surround on or off, and its room (gaming or movie).</summary>
    public bool SetSurround(bool? enabled, string? room)
    {
        var endpoint = Endpoint(fresh: true);
        if (endpoint is null)
        {
            return false;
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
            return false;
        }
        return _apo.Write(endpoint.Id, new LogitechSpeakerApoChange
        {
            SelectedSurround = enabled == true ? LogitechSpeakerProtocol.SurroundDts : null,
            DtsOn = enabled,
            DtsRoom = roomModel,
        });
    }

    /// <summary>One channel's gain (<see cref="LogitechSpeakerProtocol.Channels"/> index), or all of them back to 1 when <paramref name="channel"/> is null.</summary>
    public bool SetChannelGain(int? channel, float gain)
    {
        var endpoint = Endpoint(fresh: true);
        if (endpoint is null || channel is < 0 or >= LogitechSpeakerProtocol.ChannelCount || !float.IsFinite(gain))
        {
            return false;
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
        return _apo.Write(endpoint.Id, new LogitechSpeakerApoChange { ChannelGains = gains });
    }

    public bool SetSubwoofer(int level)
    {
        var clamped = Math.Clamp(level, 0, 100);
        if (!_bass.IsPresent() || !_bass.SetLevel(clamped))
        {
            return false;
        }
        _store.Update(s => s.LogitechSpeaker.SubwooferLevel = clamped);
        _restoredGeneration = _bass.Generation;
        lock (_lock)
        {
            _subwooferLevel = clamped;
            _subwooferGeneration = _bass.Generation;
        }
        return true;
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

    public bool SetVolume(float level)
    {
        var endpoint = Endpoint(fresh: true);
        return endpoint is not null && float.IsFinite(level) && _apo.SetVolume(endpoint.Id, Math.Clamp(level, 0f, 1f));
    }

    public bool TestChannel(int channel)
    {
        var endpoint = Endpoint(fresh: true);
        return endpoint is not null && _apo.PlayTestSound(endpoint.Id, channel);
    }

    /// <summary>Saves (or replaces) one of the user's presets. Built-in names are taken.</summary>
    public bool SavePreset(string name, IReadOnlyList<float> bands)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 40 || bands.Count != LogitechSpeakerProtocol.BandCount || bands.Any(b => !float.IsFinite(b))
            || LogitechSpeakerProtocol.BuiltInPresets.Any(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        var clamped = bands.Select(b => Math.Clamp(b, LogitechSpeakerProtocol.BandMinDb, LogitechSpeakerProtocol.BandMaxDb)).ToList();
        _store.Update(s =>
        {
            var presets = s.LogitechSpeaker.CustomPresets;
            presets.RemoveAll(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            presets.Add(new LogitechEqPreset { Name = trimmed, Bands = clamped });
        });
        return true;
    }

    public bool DeletePreset(string name)
    {
        var removed = false;
        _store.Update(s =>
        {
            removed = s.LogitechSpeaker.CustomPresets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed && string.Equals(s.LogitechSpeaker.EqPreset, name, StringComparison.OrdinalIgnoreCase))
            {
                s.LogitechSpeaker.EqPreset = null;
            }
        });
        return removed;
    }

    private List<LogitechEqPresetDto> Presets()
    {
        var list = LogitechSpeakerProtocol.BuiltInPresets
            .Select(p => new LogitechEqPresetDto { Name = p.Name, Bands = p.Bands.ToList(), BuiltIn = true })
            .ToList();
        list.AddRange(_store.Load().LogitechSpeaker.CustomPresets
            .Where(p => p.Bands.Count == LogitechSpeakerProtocol.BandCount)
            .Select(p => new LogitechEqPresetDto { Name = p.Name, Bands = p.Bands.ToList() }));
        return list;
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
        var custom = _store.Load().LogitechSpeaker.CustomPresets
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Bands.Count == LogitechSpeakerProtocol.BandCount);
        return custom?.Bands.ToArray();
    }

    // The speaker's output: the default one when it is a Logitech speaker, else the first.
    // Presence checks run every few seconds, so the registry scan is cached briefly.
    private LogitechSpeakerEndpoint? Endpoint(bool fresh = false)
    {
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
