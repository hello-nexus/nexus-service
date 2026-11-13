#if DEV_TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LogitechAudio;

namespace Nexus.Service.Dev;

public sealed class LogitechSpeakerSimDto
{
    public bool Active { get; set; }
    public int TestsPlayed { get; set; }
    /// <summary>Index into the state's channels of the last test sound; null before the first.</summary>
    public int? LastTestChannel { get; set; }
}

/// <summary>
/// Dev-tools simulated Logitech speaker. While active it answers as the speaker effect and the
/// subwoofer channel from memory; otherwise it hands every call to the real implementations
/// (none off Windows), so the service above it cannot tell the difference.
/// </summary>
public sealed class LogitechSpeakerSim : ILogitechSpeakerApo, ILogitechSpeakerBass, ILogitechSpeakerSimulation
{
    public const string EndpointId = "{0.0.0.00000000}.{51a15e00-0000-4000-8000-000000000560}";
    private const int GenerationBase = 1_000_000;

    private readonly ILogitechSpeakerApo? _apo;
    private readonly ILogitechSpeakerBass? _bass;
    private readonly object _lock = new();
    private bool _active;
    private int _epoch;
    private int _eq;
    private float[] _bands = new float[LogitechSpeakerProtocol.BandCount];
    private int _surround;
    private bool _dts;
    private int _room;
    private float[] _gains = Array.Empty<float>();
    private float _volume;
    private int _bassLevel;
    private int _tests;
    private int? _lastTest;

    public LogitechSpeakerSim(ILogitechSpeakerApo? apo, ILogitechSpeakerBass? bass)
    {
        _apo = apo;
        _bass = bass;
        Reset();
    }

    public bool Active
    {
        get
        {
            lock (_lock)
            {
                return _active;
            }
        }
    }

    public LogitechSpeakerSimDto Get()
    {
        lock (_lock)
        {
            return new LogitechSpeakerSimDto { Active = _active, TestsPlayed = _tests, LastTestChannel = _lastTest };
        }
    }

    public LogitechSpeakerSimDto Start()
    {
        lock (_lock)
        {
            if (!_active)
            {
                Reset();
                _active = true;
                _epoch++;
            }
        }
        return Get();
    }

    public LogitechSpeakerSimDto Stop()
    {
        lock (_lock)
        {
            if (_active)
            {
                _active = false;
                _epoch++;
            }
        }
        return Get();
    }

    // A fresh G560 as G HUB leaves it: EQ off and flat, surround off, every channel at unity.
    private void Reset()
    {
        _eq = LogitechSpeakerProtocol.EqOff;
        _bands = new float[LogitechSpeakerProtocol.BandCount];
        _surround = LogitechSpeakerProtocol.SurroundDts;
        _dts = false;
        _room = LogitechSpeakerProtocol.Rooms.Gaming;
        _gains = Enumerable.Repeat(1f, LogitechSpeakerProtocol.ChannelCount).ToArray();
        _volume = 0.5f;
        _bassLevel = 50;
        _tests = 0;
        _lastTest = null;
    }

    public IReadOnlyList<LogitechSpeakerEndpoint> FindEndpoints()
    {
        lock (_lock)
        {
            if (_active)
            {
                return new[] { new LogitechSpeakerEndpoint(EndpointId, "Logitech G560 Gaming Speaker (simulated)", "G560") };
            }
        }
        return _apo?.FindEndpoints() ?? Array.Empty<LogitechSpeakerEndpoint>();
    }

    public LogitechSpeakerApoValues Read(string endpointId)
    {
        lock (_lock)
        {
            if (_active && endpointId == EndpointId)
            {
                return new LogitechSpeakerApoValues
                {
                    SelectedEq = _eq,
                    AdvancedBands = (float[])_bands.Clone(),
                    SelectedSurround = _surround,
                    DtsOn = _dts,
                    DtsRoom = _room,
                    ChannelGains = (float[])_gains.Clone(),
                };
            }
        }
        return _apo?.Read(endpointId) ?? new LogitechSpeakerApoValues();
    }

    public bool Write(string endpointId, LogitechSpeakerApoChange change)
    {
        lock (_lock)
        {
            if (_active && endpointId == EndpointId)
            {
                _eq = change.SelectedEq ?? _eq;
                _bands = change.AdvancedBands is { } bands ? Clamp(bands, LogitechSpeakerProtocol.BandMinDb, LogitechSpeakerProtocol.BandMaxDb) : _bands;
                _surround = change.SelectedSurround ?? _surround;
                _dts = change.DtsOn ?? _dts;
                _room = change.DtsRoom ?? _room;
                _gains = change.ChannelGains is { } gains ? Clamp(gains, LogitechSpeakerProtocol.ChannelMinGain, LogitechSpeakerProtocol.ChannelMaxGain) : _gains;
                return true;
            }
        }
        return _apo?.Write(endpointId, change) ?? false;
    }

    public float? GetVolume(string endpointId)
    {
        lock (_lock)
        {
            if (_active && endpointId == EndpointId)
            {
                return _volume;
            }
        }
        return _apo?.GetVolume(endpointId);
    }

    public bool SetVolume(string endpointId, float level)
    {
        lock (_lock)
        {
            if (_active && endpointId == EndpointId)
            {
                _volume = Math.Clamp(level, 0f, 1f);
                return true;
            }
        }
        return _apo?.SetVolume(endpointId, level) ?? false;
    }

    public bool PlayTestSound(string endpointId, int channel)
    {
        lock (_lock)
        {
            if (_active && endpointId == EndpointId)
            {
                if (channel is < 0 or >= LogitechSpeakerProtocol.ChannelCount)
                {
                    return false;
                }
                _tests++;
                _lastTest = channel;
                return true;
            }
        }
        return _apo?.PlayTestSound(endpointId, channel) ?? false;
    }

    public int Generation
    {
        get
        {
            lock (_lock)
            {
                if (_active)
                {
                    return GenerationBase + _epoch;
                }
            }
            return _bass?.Generation ?? 0;
        }
    }

    public bool IsPresent()
    {
        lock (_lock)
        {
            if (_active)
            {
                return true;
            }
        }
        return _bass?.IsPresent() ?? false;
    }

    public int? GetLevel()
    {
        lock (_lock)
        {
            if (_active)
            {
                return _bassLevel;
            }
        }
        return _bass?.GetLevel();
    }

    public bool SetLevel(int level)
    {
        lock (_lock)
        {
            if (_active)
            {
                _bassLevel = Math.Clamp(level, 0, 100);
                return true;
            }
        }
        return _bass?.SetLevel(level) ?? false;
    }

    private static float[] Clamp(IReadOnlyList<float> values, float min, float max) =>
        values.Select(v => Math.Clamp(v, min, max)).ToArray();
}
#endif
