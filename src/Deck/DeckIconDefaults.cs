namespace Nexus.Service.Deck;

/// <summary>Visual category an action maps to, driving the auto icon + color. Mirrors nexus-web's deckIcons.ts DeckCategory.</summary>
public enum DeckCategory
{
    Launch, Open, Volume, Media, Brightness, Keyboard, Text, Power, Audio, Nexus,
    Monitoring, Weather, Sequence, Toggle, Folder, Navigation, StreamDeck, Empty,
}

/// <summary>
/// C# port of nexus-web's deckIcons.ts category/auto-icon/toggle-branch
/// resolution: the pure classification logic DeckKeyRenderer and the App
/// Aware default-preset seeding both need. Keep in sync with that file.
/// </summary>
public static class DeckIconDefaults
{
    public static DeckCategory Category(DeckAction? action)
    {
        if (action is null)
        {
            return DeckCategory.Empty;
        }
        switch (action.Type)
        {
            case "launchApp": return DeckCategory.Launch;
            case "openFile":
            case "openFolder":
            case "openUrl": return DeckCategory.Open;
            case "system":
                var op = action.SystemAction?.Op ?? "";
                if (op == "openSettings") { return DeckCategory.Open; }
                if (op.StartsWith("volume") || op == "muteToggle") { return DeckCategory.Volume; }
                if (op.StartsWith("media")) { return DeckCategory.Media; }
                return DeckCategory.Brightness;
            case "hotkey":
            case "hotkeySwitch": return DeckCategory.Keyboard;
            case "text": return DeckCategory.Text;
            case "power": return DeckCategory.Power;
            case "audioOutput":
            case "audioInput": return DeckCategory.Audio;
            case "nexus": return DeckCategory.Nexus;
            case "monitoring": return DeckCategory.Monitoring;
            case "weather": return DeckCategory.Weather;
            case "playAudio": return DeckCategory.Media;
            case "sequence": return DeckCategory.Sequence;
            case "toggle": return DeckCategory.Toggle;
            case "page":
            case "pageIndicator": return DeckCategory.Navigation;
            case "deckBrightness":
            case "deckSleep": return DeckCategory.StreamDeck;
            default: return DeckCategory.Empty;
        }
    }

    public static string CategoryColorHex(DeckCategory category) => category switch
    {
        DeckCategory.Launch => "#64748b",
        DeckCategory.Open => "#64748b",
        DeckCategory.Volume => "#06b6d4",
        DeckCategory.Media => "#22c55e",
        DeckCategory.Brightness => "#f59e0b",
        DeckCategory.Keyboard => "#8b5cf6",
        DeckCategory.Text => "#a855f7",
        DeckCategory.Power => "#ef4444",
        DeckCategory.Audio => "#3b82f6",
        DeckCategory.Nexus => "#f97316",
        DeckCategory.Monitoring => "#4da3ff",
        DeckCategory.Weather => "#0ea5e9",
        DeckCategory.Sequence => "#eab308",
        DeckCategory.Toggle => "#14b8a6",
        DeckCategory.Folder => "#94a3b8",
        DeckCategory.Navigation => "#6366f1",
        DeckCategory.StreamDeck => "#f43f5e",
        _ => "#475569",
    };

    /// <summary>Default lucide icon name (a data/deck-icons/&lt;name&gt;.png key) per action when no explicit icon is set.</summary>
    public static string AutoIconName(DeckAction? action, bool isFolder = false)
    {
        if (isFolder)
        {
            return "Folder";
        }
        if (action is null)
        {
            return "Plus";
        }
        switch (action.Type)
        {
            case "launchApp": return "AppWindow";
            case "openFile": return "FileText";
            case "openFolder": return "FolderOpen";
            case "openUrl": return "Globe";
            case "system":
                return (action.SystemAction?.Op) switch
                {
                    "volumeUp" => "Volume2",
                    "volumeDown" => "Volume1",
                    "volumeSet" => "Volume2",
                    "muteToggle" => "VolumeX",
                    "mediaPlayPause" => "Play",
                    "mediaNext" => "SkipForward",
                    "mediaPrev" => "SkipBack",
                    "brightnessUp" => "Sun",
                    "brightnessDown" => "SunDim",
                    "brightnessSet" => "Sun",
                    "openSettings" => "Settings",
                    _ => "Sliders",
                };
            case "hotkey": return "Keyboard";
            case "hotkeySwitch": return "RefreshCw";
            case "text": return "Type";
            case "power":
                return action.PowerAction switch
                {
                    "lock" => "Lock",
                    "sleep" => "Moon",
                    "shutdown" => "Power",
                    "restart" => "RotateCcw",
                    "logout" => "LogOut",
                    _ => "Power",
                };
            case "audioOutput": return "Headphones";
            case "audioInput": return "Mic";
            case "nexus":
                return (action.NexusAction?.Op) switch
                {
                    "rgbEffect" => "Palette",
                    "lightingBrightness" => "Lightbulb",
                    "lightingPreset" => "Lightbulb",
                    "fanProfile" => "Fan",
                    "coolingPreset" => "Fan",
                    "y70Power" => "Monitor",
                    "y70Brightness" => "Monitor",
                    "y70Rotation" => "Monitor",
                    _ => "Zap",
                };
            case "monitoring": return "Activity";
            case "weather": return "CloudSun";
            case "playAudio": return "Music";
            case "sequence": return "ListOrdered";
            case "toggle": return "ToggleLeft";
            case "page":
                return action.Op switch
                {
                    "prev" => "ChevronLeft",
                    "next" => "ChevronRight",
                    "goto" => "Layers",
                    _ => "Layers",
                };
            case "pageIndicator": return "Layers";
            case "deckBrightness": return action.Op == "down" ? "SunDim" : "Sun";
            case "deckSleep": return "Moon";
            default: return "Plus";
        }
    }

    /// <summary>Resolved icon + color for one branch (on/off) of a toggle slot: explicit slot override wins, else the branch action's own auto icon/category color. Mirrors deckIcons.ts toggleBranchSlot.</summary>
    public static (DeckIcon Icon, string ColorHex) ResolveToggleBranch(DeckSlot slot, DeckAction? branch)
    {
        var icon = slot.Icon ?? new DeckIcon { Kind = "lucide", Value = AutoIconName(branch) };
        var color = slot.Color ?? CategoryColorHex(Category(branch));
        return (icon, color);
    }
}
