using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Models.Sensors;

/// <summary>One ROG Astral card's 12V-2x6 pins as the Power Detector last read them.</summary>
public sealed record PowerDetectorGpuDto
{
    public string GpuId { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Amps through each 12V pin, pin 1 first.</summary>
    public IReadOnlyList<float> PinAmps { get; init; } = Array.Empty<float>();
    public long ReadAtMs { get; init; }
}

/// <summary>The pin carrying the most current, across every card; Pin is 1-based.</summary>
public sealed record PowerDetectorPinDto
{
    public string GpuId { get; init; } = "";
    public int Pin { get; init; }
    public float Amps { get; init; }
}

/// <summary>A shutdown countdown: why it started ("overcurrent" or "critical"), when it ends (Unix ms) and the words for it.</summary>
public sealed record PowerDetectorCountdownDto
{
    public string Reason { get; init; } = "";
    public long StartedAtMs { get; init; }
    public long EndsAtMs { get; init; }
    /// <summary>What started it, as the countdown notification says it.</summary>
    public string Text { get; init; } = "";
}

/// <summary>The latest alert, for the in-app pop-up; Id grows with each alert.</summary>
public sealed record PowerDetectorAlertDto
{
    public long Id { get; init; }
    public string Level { get; init; } = "";
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public long AtMs { get; init; }
}

/// <summary>
/// The ROG Astral Power Detector: the cards it watches, the level ("normal",
/// "overcurrent" or "critical"), the limits in force, a countdown when one runs,
/// the latest alert while in-app pop-ups are on, and the user's settings.
/// Supported is false when no ROG Astral card was found.
/// </summary>
public sealed record PowerDetectorStateDto
{
    public bool Supported { get; init; }
    public IReadOnlyList<PowerDetectorGpuDto> Gpus { get; init; } = Array.Empty<PowerDetectorGpuDto>();
    public string Level { get; init; } = "normal";
    public PowerDetectorPinDto? Worst { get; init; }
    public float OvercurrentAmps { get; init; }
    public float CriticalAmps { get; init; }
    /// <summary>Each pin is fine from PinLowAmps to under PinWarnAmps, as GPU Tweak III colours its dots; at PinCautionAmps it carries clearly more than its share.</summary>
    public float PinWarnAmps { get; init; }
    public float PinLowAmps { get; init; }
    public float PinCautionAmps { get; init; }
    public PowerDetectorCountdownDto? Countdown { get; init; }
    public bool ShuttingDown { get; init; }
    /// <summary>The user cancelled a countdown; no new one starts until the pins are back to normal.</summary>
    public bool HeldOff { get; init; }
    public PowerDetectorAlertDto? Alert { get; init; }
    public AstralPowerDetectorSettings Settings { get; init; } = new();
}

/// <summary>A change to the Power Detector's settings; null fields stay as they are.</summary>
public sealed record PowerDetectorConfigBody
{
    public bool? TrayAlerts { get; init; }
    public bool? InAppAlerts { get; init; }
    public bool? Equalizer { get; init; }
    public bool? HideEqualizerReminder { get; init; }
    public bool? OvercurrentShutdown { get; init; }
    public int? OvercurrentSeconds { get; init; }
    public int? CountdownSeconds { get; init; }
    public bool? CriticalShutdown { get; init; }
}

/// <summary>Whether a cancel or shutdown-now request took effect.</summary>
public sealed record PowerDetectorActionResponse
{
    public bool Ok { get; init; }
}
