using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Models.Sensors;

public sealed class AstralStateDto
{
    /// <summary>False off Windows unless the dev-tools simulation is active.</summary>
    public bool Supported { get; set; }
    public List<AstralCardDto> Cards { get; set; } = new();
    public AstralSettings Settings { get; set; } = new();
    public AstralCountdownDto? Countdown { get; set; }
    /// <summary>The user cancelled an overcurrent countdown; a critical pin still arms.</summary>
    public bool HeldOff { get; set; }
    public bool Simulated { get; set; }
}

public sealed class AstralCardDto
{
    /// <summary>The GPU's sensor parent id.</summary>
    public string GpuId { get; set; } = "";
    public string Name { get; set; } = "";
    public AstralLevelDto Level { get; set; } = new();
    public List<AstralPinDto> Pins { get; set; } = new();
    /// <summary>Worst pin: ok, caution, warning, dead, overcurrent or critical.</summary>
    public string State { get; set; } = "ok";
}

public sealed class AstralLevelDto
{
    public bool Available { get; set; }
    public bool Calibrating { get; set; }
    public long? CalibratedAtMs { get; set; }
}

public sealed class AstralPinDto
{
    public float Amps { get; set; }
    public string State { get; set; } = "ok";
}

public sealed class AstralCountdownDto
{
    /// <summary>"overcurrent" or "critical".</summary>
    public string Reason { get; set; } = "";
    public long EndsAtMs { get; set; }
    /// <summary>Milliseconds left when the state was built, never negative.</summary>
    public long RemainingMs { get; set; }
    /// <summary>The card whose countdown this is; the soonest when several run.</summary>
    public string GpuId { get; set; } = "";
}

public sealed class AstralCalibrateRequest
{
    public string? GpuId { get; set; }
}
