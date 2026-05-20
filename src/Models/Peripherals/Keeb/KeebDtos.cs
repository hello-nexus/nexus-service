using System.Collections.Generic;
using Qos.Service.Models.Common;

namespace Qos.Service.Models.Peripherals.Keeb;

public class KeyboardState
{
    public bool IsConnected { get; set; }
    public int Profile { get; set; }
    /// <summary>ANSI or ISO; the firmware reports this at handshake.</summary>
    public string Layout { get; set; } = "ANSI";
    /// <summary>Active layer (0-3) for the keys[][] payload. Layer is panel-side state but echoed here so a stale snapshot is self-describing.</summary>
    public int Layer { get; set; }
    /// <summary>Function assignments for the active layer, indexed `[row][col]` matching the UI keyboard render.</summary>
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
    public List<RotaryAppOverride> Apps { get; set; } = new();
}

public class RotaryAppOverride
{
    public string TargetId { get; set; } = "";
    public string Left { get; set; } = "";
    public string Right { get; set; } = "";
}

public class SetRotarySensitivityBody
{
    public string Sensitivity { get; set; } = "Balanced";
}

public class GetKeebSettingsResponse : ApiResponse
{
    public bool ShiftKeyDisabled { get; set; }
    public bool WindowsKeyDisabled { get; set; }
    public bool AltF4Disabled { get; set; }
    public bool AltTabDisabled { get; set; }
    public string AnimationMode { get; set; } = "Static";
    public string Speed { get; set; } = "Medium";
    public string Direction { get; set; } = "Forward";
    public int Brightness { get; set; } = 80;
    public bool KeyIndicator { get; set; }
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
    public bool KeyIndicator { get; set; }
}

/// <summary>Key-reactive overlay settings. Separate body from <see cref="SetFirmwareLightingBody"/> because the firmware applies them through a different code path.</summary>
public class SetPassiveLightingBody
{
    public bool KeyReactive { get; set; }
    public bool KeyReactiveMask { get; set; }
    public string KeyReactiveMode { get; set; } = "Off";
    public RGBA KeyReactiveColor { get; set; }
}

public class SetLayerKeyBody
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Func { get; set; } = "";
    public string Mode { get; set; } = "";
    public int? Input { get; set; }
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
    public string Category { get; set; } = "";
    public bool Meta { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
}

public class SetMacroBody
{
    public List<MacroKey> Keys { get; set; } = new();
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
