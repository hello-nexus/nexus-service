namespace Nexus.Service.Models.Focus;

/// <summary>Pinned cross-repo contract with nexus-web (the top bar chip and the Focus settings page). Field names and shapes must not change on one side alone.</summary>
public sealed class FocusStatus
{
    /// <summary>Id of the mode currently active, or null.</summary>
    public string? ActiveModeId { get; set; }

    /// <summary>"auto", "manual", or "" while inactive.</summary>
    public string Reason { get; set; } = "";

    public long ActivatedUtcMs { get; set; }

    /// <summary>Games currently firing the game trigger, newest last.</summary>
    public List<FocusGame> Games { get; set; } = new();

    /// <summary>Every mode, in precedence order.</summary>
    public List<FocusModeDto> Modes { get; set; } = new();

    /// <summary>Trigger ids this build can actually detect, for the settings picker.</summary>
    public List<string> AvailableTriggers { get; set; } = new();
}

public sealed class FocusGame
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    public long SinceMs { get; set; }
}

public sealed class FocusModeDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public bool BuiltIn { get; set; }
    public string Trigger { get; set; } = "";
    public bool HoldNotifications { get; set; }
    public bool HoldBackgroundTraffic { get; set; }
    public bool TurnPanelDisplaysOff { get; set; }
    public bool StaticPanelBackgrounds { get; set; }
    public int ExitGraceSeconds { get; set; }
}

/// <summary>Every field optional: a PATCH of one mode. Id comes from the route.</summary>
public sealed class UpdateFocusModeBody
{
    public string? Name { get; set; }
    public string? Icon { get; set; }
    public string? Trigger { get; set; }
    public bool? HoldNotifications { get; set; }
    public bool? HoldBackgroundTraffic { get; set; }
    public bool? TurnPanelDisplaysOff { get; set; }
    public bool? StaticPanelBackgrounds { get; set; }
    public int? ExitGraceSeconds { get; set; }
}

public sealed class CreateFocusModeBody
{
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Trigger { get; set; } = "";
}

/// <summary>Top bar action: activate one mode by id, or turn the active one off.</summary>
public sealed class SetFocusActiveBody
{
    /// <summary>Mode id to activate; null or empty turns Focus off.</summary>
    public string? ModeId { get; set; }
}

/// <summary>New precedence order, by mode id.</summary>
public sealed class ReorderFocusModesBody
{
    public List<string> ModeIds { get; set; } = new();
}

/// <summary>Revision frame for the "focus" multiplex topic; subscribers refetch GET /api/focus.</summary>
public sealed class FocusChangedFrame
{
    public long Revision { get; set; }
}
