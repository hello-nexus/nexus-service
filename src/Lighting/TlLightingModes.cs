using System.Collections.Generic;

namespace Nexus.Service.Lighting;

public sealed class TlModeInfo
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public byte ModeByte { get; init; }

    /// <summary>Written as the payload's disable flag rather than a mode byte.</summary>
    public bool IsOff { get; init; }
}

/// <summary>
/// Firmware animations the Uni Fan TL controller renders on-chip. Unlike the Uni
/// Hub families there is no per-LED path on this controller, so every look is one
/// of these modes plus brightness, speed, direction and up to four colours.
/// </summary>
public static class TlLightingModes
{
    public const string OffKey = "off";
    public const int MaxColors = 4;
    public const int MaxBrightness = 4;
    public const int MaxSpeed = 4;
    public const int MaxDirection = 5;

    /// <summary>
    /// Mode bytes are sent as-is. L-Connect stores a mode id whose thousands band
    /// selects a merge/side variant and takes it modulo 1000 for the wire, so the
    /// byte here is that remainder.
    /// </summary>
    public static readonly TlModeInfo[] Catalog =
    {
        new() { Key = OffKey,            Label = "Off",              ModeByte = 0,    IsOff = true },
        new() { Key = "rainbow",         Label = "Rainbow",          ModeByte = 1,    },
        new() { Key = "rainbowMorph",    Label = "Rainbow Morph",    ModeByte = 2,    },
        new() { Key = "static",          Label = "Static",           ModeByte = 3,    },
        new() { Key = "breathing",       Label = "Breathing",        ModeByte = 4,    },
        new() { Key = "runway",          Label = "Runway",           ModeByte = 5,    },
        new() { Key = "meteor",          Label = "Meteor",           ModeByte = 6,    },
        new() { Key = "colorCycle",      Label = "Color Cycle",      ModeByte = 7,    },
        new() { Key = "staggered",       Label = "Staggered",        ModeByte = 8,    },
        new() { Key = "tide",            Label = "Tide",             ModeByte = 9,    },
        new() { Key = "mixing",          Label = "Mixing",           ModeByte = 10,   },
        new() { Key = "voice",           Label = "Voice",            ModeByte = 11,   },
        new() { Key = "door",            Label = "Door",             ModeByte = 12,   },
        new() { Key = "render",          Label = "Render",           ModeByte = 13,   },
        new() { Key = "ripple",          Label = "Ripple",           ModeByte = 14,   },
        new() { Key = "reflect",         Label = "Reflect",          ModeByte = 15,   },
        new() { Key = "tailChasing",     Label = "Tail Chasing",     ModeByte = 16,   },
        new() { Key = "paint",           Label = "Paint",            ModeByte = 17,   },
        new() { Key = "pingPong",        Label = "Ping Pong",        ModeByte = 18,   },
        new() { Key = "stack",           Label = "Stack",            ModeByte = 19,   },
        new() { Key = "coverCycle",      Label = "Cover Cycle",      ModeByte = 20,   },
        new() { Key = "wave",            Label = "Wave",             ModeByte = 21,   },
        new() { Key = "racing",          Label = "Racing",           ModeByte = 22,   },
        new() { Key = "lottery",         Label = "Lottery",          ModeByte = 23,   },
        new() { Key = "intertwine",      Label = "Intertwine",       ModeByte = 24,   },
        new() { Key = "meteorShower",    Label = "Meteor Shower",    ModeByte = 25,   },
        new() { Key = "collide",         Label = "Collide",          ModeByte = 26,   },
        new() { Key = "electricCurrent", Label = "Electric Current", ModeByte = 27,   },
        new() { Key = "kaleidoscope",    Label = "Kaleidoscope",     ModeByte = 28,   },
    };

    public static TlModeInfo Find(string? key)
    {
        foreach (var m in Catalog)
        {
            if (m.Key == key)
            {
                return m;
            }
        }
        return Catalog[0];
    }

    public static IReadOnlyList<TlModeInfo> All => Catalog;
}
