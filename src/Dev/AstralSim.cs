#if DEV_TOOLS
using System;
using System.Collections.Generic;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Sensors.Astral;

namespace Nexus.Service.Dev;

public sealed class AstralSimDto
{
    public bool Active { get; set; }
    public float Sag { get; set; }
    public float Lean { get; set; }
    public float[] PinAmps { get; set; } = new float[AstralTelemetryParser.PinCount];
}

public sealed class AstralSimRequest
{
    public float? Sag { get; set; }
    public float? Lean { get; set; }
    public float[]? PinAmps { get; set; }
}

/// <summary>
/// Dev-tools simulated Astral card. The GPU read of every platform appends it, so its
/// readings reach the monitor, sensors, device entry and alerts exactly like a real card.
/// </summary>
public sealed class AstralSim
{
    public const string CardId = "/gpu-nvidia/astral-sim";
    public const string CardName = "ROG Astral RTX 5090 (simulated)";
    private const float RailVolts = 12f;
    // A vertically mounted card at rest: X on the height axis, Y tilted slightly toward the bracket.
    private static readonly AstralAngles Rest = new(90f, 1.2f, 90f);

    public static AstralSim? Current { get; private set; }

    private readonly AstralMonitor _monitor;
    private readonly object _lock = new();
    private bool _active;
    private float _sag;
    private float _lean;
    private float[] _amps = new float[AstralTelemetryParser.PinCount];
    private DateTimeOffset _nextFeed;

    public AstralSim(AstralMonitor monitor)
    {
        _monitor = monitor;
        Current = this;
    }

    public static void AppendGpu(List<GpuReadout> gpus) => Current?.Append(gpus);

    public AstralSimDto Get()
    {
        lock (_lock)
        {
            return new AstralSimDto { Active = _active, Sag = _sag, Lean = _lean, PinAmps = (float[])_amps.Clone() };
        }
    }

    public AstralSimDto Set(AstralSimRequest request)
    {
        lock (_lock)
        {
            if (!_active)
            {
                _active = true;
                _amps = new float[AstralTelemetryParser.PinCount];
                Array.Fill(_amps, 8f);
                _nextFeed = DateTimeOffset.MinValue;
            }
            _sag = request.Sag ?? _sag;
            _lean = request.Lean ?? _lean;
            if (request.PinAmps is { Length: AstralTelemetryParser.PinCount } pins)
            {
                _amps = (float[])pins.Clone();
            }
        }
        Append(new List<GpuReadout>());
        return Get();
    }

    public AstralSimDto Stop()
    {
        lock (_lock)
        {
            _active = false;
            (_sag, _lean) = (0, 0);
        }
        _monitor.Remove(CardId);
        return Get();
    }

    private void Append(List<GpuReadout> gpus)
    {
        float[] amps;
        AstralAngles angles;
        bool feed;
        lock (_lock)
        {
            if (!_active)
            {
                return;
            }
            amps = _amps;
            angles = new AstralAngles(Rest.X - Math.Abs(_lean), Rest.Y + _sag, Rest.Z + _lean);
            var now = DateTimeOffset.UtcNow;
            feed = now >= _nextFeed;
            if (feed)
            {
                _nextFeed = now + TimeSpan.FromSeconds(1);
            }
        }

        if (feed)
        {
            _monitor.Feed(CardId, CardName, null, amps, simulated: true);
            _monitor.FeedAngles(CardId, angles, Rest);
        }

        var pins = new AstralTelemetryParser.PinReading[amps.Length];
        float total = 0, power = 0;
        for (var i = 0; i < pins.Length; i++)
        {
            pins[i] = new AstralTelemetryParser.PinReading(RailVolts, amps[i], RailVolts * amps[i]);
            total += amps[i];
            power += RailVolts * amps[i];
        }
        var sensors = new List<HardwareSensor>();
        AstralSensorBuilder.Append(CardId, CardName, new AstralTelemetryParser.AstralReadout(pins, total, power), sensors);
        _monitor.AppendLevelSensors(CardId, CardName, sensors);
        gpus.Add(new GpuReadout { Id = CardId, Name = CardName, Vendor = "nvidia", Sensors = sensors });
    }
}
#endif
