using System.Collections.Generic;
using Nexus.Service.Models.Common;

namespace Nexus.Service.Models.Peripherals.Keeb;

public class KeyboardState
{
    public bool IsConnected { get; set; }
    public int Profile { get; set; }
    public int Layer { get; set; }
    public string Layout { get; set; } = "ANSI";
    public List<List<KeebKey>> Keys { get; set; } = new();
}

public class KeebKey
{
    public string Mode { get; set; } = "";
    public string Function { get; set; } = "";
    public int? Input { get; set; }
}

public class GetRotaryFunctionsResponse : ApiResponse
{
    public string[] Functions { get; set; } = System.Array.Empty<string>();
}

public class SetRotaryWheelsBody
{
    public string Left { get; set; } = "";
    public string Right { get; set; } = "";
}

public class GetKeebSettingsResponse : ApiResponse
{
    public bool ShiftKeyDisabled { get; set; }
    public bool WindowsKeyDisabled { get; set; }
    public bool AltF4Disabled { get; set; }
    public bool AltTabDisabled { get; set; }
    // Rotary assignment is persisted desired-state like everything else here;
    // without it the panel cannot restore the wheels after a reload.
    public string RotaryLeft { get; set; } = "";
    public string RotaryRight { get; set; } = "";
    public string AnimationMode { get; set; } = "Static";
    public string Speed { get; set; } = "Medium";
    public string Direction { get; set; } = "Forward";
    public int Brightness { get; set; } = 80;
    public bool KeyReactive { get; set; }
    public bool KeyReactiveMask { get; set; }
    public string KeyReactiveMode { get; set; } = "Off";
    public RGBA KeyReactiveColor { get; set; }
}

public class SetPassiveLightingBody
{
    public bool KeyReactive { get; set; }
    public bool KeyReactiveMask { get; set; }
    public string KeyReactiveMode { get; set; } = "Off";
    public RGBA KeyReactiveColor { get; set; }
}

public class SetFirmwareLightingBody
{
    public string AnimationMode { get; set; } = "Static";
    public string Speed { get; set; } = "Medium";
    public string Direction { get; set; } = "Forward";
    public int Brightness { get; set; } = 80;
}

/// <summary>One key-assignment write: web layout cell (x=row, y=index in row) plus the function to bind.</summary>
public class SetLayerKeyBody
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Func { get; set; } = "";
    public string Mode { get; set; } = "";
    public int? Input { get; set; }
}

public class SetLayerKeyResponse : ApiResponse
{
    public KeyboardState State { get; set; } = new();
    /// <summary>False when the keyboard was disconnected or the onboard write could not be verified.</summary>
    public bool WroteDevice { get; set; }
}

public class SetGameModeBody
{
    public bool AltF4 { get; set; }
    public bool AltTab { get; set; }
    public bool ShiftTab { get; set; }
    public bool WindowsKey { get; set; }
}

public class GetMacroResponse : ApiResponse
{
    public KeebMacro Macro { get; set; } = new();
}

public class SetMacroResponse : ApiResponse
{
    public KeebMacro Macro { get; set; } = new();
    /// <summary>True when the encoded actions overflowed the 256-byte onboard stream and were cut.</summary>
    public bool Truncated { get; set; }
    /// <summary>Key names with no HID mapping - persisted but never played by the firmware.</summary>
    public string[] DroppedKeys { get; set; } = System.Array.Empty<string>();
    /// <summary>False when the keyboard was disconnected or the onboard write could not be verified.</summary>
    public bool WroteDevice { get; set; }
}

public class KeebMacro
{
    public int Index { get; set; }
    public List<MacroKey> Keys { get; set; } = new();
}

public class MacroKey
{
    public string Key { get; set; } = "";
    public int Duration { get; set; }
    public string Type { get; set; } = "KeyDown";
}

public class SetMacroBody
{
    public List<MacroKey>? Keys { get; set; }
}

public class InputterBody
{
    public List<MacroStroke> Strokes { get; set; } = new();
}

public class MacroStroke
{
    public string Key { get; set; } = "";
    public bool Meta { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public int Duration { get; set; }
    public string Type { get; set; } = "keydown";
}
