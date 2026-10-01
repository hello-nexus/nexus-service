using System.Collections.Generic;
using Nexus.Service.Models.Peripherals.Keeb;

namespace Nexus.Service.Models.Activity;

/// <summary>
/// A keyboard injection request. Either a single chord (Key + modifier flags -
/// the common deck-button case) OR an explicit ordered <see cref="Strokes"/>
/// list. When Strokes is non-empty it wins over the single-chord form.
/// </summary>
public sealed class SendKeysBody
{
    public string Key { get; set; } = "";
    public bool Meta { get; set; }
    public bool Ctrl { get; set; }
    public bool Alt { get; set; }
    public bool Shift { get; set; }
    public List<MacroStroke> Strokes { get; set; } = new();
}

/// <summary>Type text via the deck/panel text action: sets the clipboard, then injects a paste chord.</summary>
public sealed class SendTextBody
{
    public string Text { get; set; } = "";
}

/// <summary>Open a local file or folder with the OS default handler.</summary>
public sealed class OpenPathBody
{
    public string Path { get; set; } = "";
}

/// <summary>Request for the native file/folder picker dialog.</summary>
public sealed class PickPathBody
{
    public bool Folder { get; set; }
}

/// <summary>Picked absolute path, or null when the user cancelled the dialog.</summary>
public sealed class PickPathResponse
{
    public string? Path { get; set; }
}

/// <summary>OS accent colour as #RRGGBB, or empty when unavailable (e.g. served
/// only on Linux, where the dashboard browser has no native accent push).</summary>
public sealed class SystemAccentResponse
{
    public string Accent { get; set; } = "";
}

/// <summary>Stable id for the current OS boot, quantized Unix epoch seconds
/// so the web UI can detect a reboot since it last showed a one-time
/// greeting. Stable across a service process restart within the same boot.</summary>
public sealed class SystemBootResponse
{
    public string BootId { get; set; } = "";
}

/// <summary>This machine's PC case as a catalog part id; null when none is picked, which is written explicitly.</summary>
public sealed class SystemCaseResponse
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public string? CaseId { get; set; }
}

public sealed class SetSystemCaseBody
{
    public string? CaseId { get; set; }
}
