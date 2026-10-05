using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Models.Lighting;

/// <summary>GET /lighting/key-reactive.</summary>
public sealed class KeyReactiveStateResponse
{
    /// <summary>True when a key source is live (the Windows user-session helper); presses still work from the press route without one.</summary>
    public bool InputAvailable { get; set; }
    public List<KeyReactiveDeviceDto> Devices { get; set; } = new();
}

/// <summary>One per-key keyboard card and the reaction stored for its device.</summary>
public sealed class KeyReactiveDeviceDto
{
    /// <summary>Lighting card id; the press and preview routes take it.</summary>
    public string Id { get; set; } = "";
    /// <summary>Physical device the config is stored under; every card of one device shares it.</summary>
    public string DeviceId { get; set; } = "";
    /// <summary>The card's device index in the /lighting/output frame stream, for a live preview.</summary>
    public int FrameIndex { get; set; }
    /// <summary>True when the device reports its own key presses (the keeb), so OS keystrokes do not drive it.</summary>
    public bool HardwareKeys { get; set; }
    public int LedCount { get; set; }
    /// <summary>LEDs the board names; zero means presses match keys by position.</summary>
    public int NamedKeys { get; set; }
    public KeyReaction Config { get; set; } = new();
}

/// <summary>POST /lighting/key-reactive/{id}/press: a key name, else an LED index, else a random key.</summary>
public sealed class KeyReactivePressBody
{
    public string? Key { get; set; }
    public int? Led { get; set; }
}

/// <summary>A looping render of a reaction on one board's real layout, played by the dashboard.</summary>
public sealed class KeyReactivePreviewResponse
{
    /// <summary>LED x in key units from the board's left edge.</summary>
    public List<float> X { get; set; } = new();
    /// <summary>LED y in key units from the board's top row.</summary>
    public List<float> Y { get; set; } = new();
    public float Width { get; set; }
    public float Height { get; set; }
    public int Fps { get; set; }
    public int FrameCount { get; set; }
    /// <summary>Base64 of FrameCount frames, each LED count x RGB bytes.</summary>
    public string Frames { get; set; } = "";
}
