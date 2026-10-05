using System.Collections.Generic;

namespace Nexus.Service.Lighting.Rgb;

/// <summary>
/// One RGB device discovered by the OpenRGB SDK server. Mirrors the subset of
/// fields we actually need from the OpenRGB controller struct.
/// </summary>
public sealed class RgbDevice
{
    /// <summary>Position in the daemon's list; the index-fallback StableId uses it.</summary>
    public int Index { get; set; }
    /// <summary>What per-controller packets carry: the controller id from protocol 6 (never reused within one daemon run), the index below it.</summary>
    public int Address { get; set; }
    public string Name { get; set; } = "";
    public uint Type { get; set; }
    public int LedCount { get; set; }
    public string Vendor { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Location { get; set; } = "";
    public List<string> LedNames { get; set; } = new();
    public List<RgbZone> Zones { get; set; } = new();
    public List<RgbMode> Modes { get; set; } = new();

    /// <summary>
    /// Pick the best "per-LED control" mode using OpenRGB's own priority order:
    /// Direct &gt; Custom &gt; Static, with color_mode = PER_LED (1) or MODE_SPECIFIC (2).
    /// Applied via UPDATE_MODE so the controller's hardware mode register is
    /// flipped (SET_CUSTOM_MODE only updates the server's
    /// in-memory active_mode and never calls DeviceUpdateMode, leaving controllers
    /// with hardware mode registers - ENE DRAM is the canonical example - silently
    /// rejecting subsequent UPDATE_LEDS pushes).
    /// </summary>
    public RgbMode? FindCustomMode()
    {
        foreach (var preferredName in new[] { "Direct", "Custom", "Static" })
        {
            foreach (var m in Modes)
            {
                if (m.Name == preferredName && (m.ColorMode == 1u || m.ColorMode == 2u))
                {
                    return m;
                }
            }
        }
        return null;
    }

    private string? _stableId;

    /// <summary>
    /// Stable identifier derived from serial or location, falling back to
    /// index when neither is available. Cached after first access.
    /// </summary>
    public string StableId => _stableId ??= BuildStableId();

    /// <summary>
    /// True when StableId derives from serial or location, so it identifies the
    /// same physical device across a subprocess bounce. False when it falls back
    /// to the index, which OpenRGB reuses across re-enumeration.
    /// </summary>
    public bool HasStableHardwareId => !string.IsNullOrEmpty(Serial) || !string.IsNullOrEmpty(Location);

    /// <summary>Id for a device without serial or location, assigned by <see cref="OpenRgbPinnedIds"/> before <see cref="StableId"/> is first read.</summary>
    public string? PinnedId { get; set; }

    private string BuildStableId()
    {
        if (!string.IsNullOrEmpty(Serial))
            return $"openrgb-s-{Sanitize(Serial)}";
        if (!string.IsNullOrEmpty(Location))
            return $"openrgb-l-{Sanitize(Location)}";
        return PinnedId ?? $"openrgb-{Index}";
    }

    private static string Sanitize(string s)
    {
        var buf = new char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            buf[i] = char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_';
        }
        return new string(buf);
    }
}

public sealed class RgbZone
{
    public string Name { get; set; } = "";
    /// <summary>OpenRGB zone type: 0=Single, 1=Linear, 2=Matrix.</summary>
    public uint ZoneType { get; set; }
    public int LedCount { get; set; }
    /// <summary>Resize bounds the controller advertises; zone type, not these, gates resizing.</summary>
    public uint LedsMin { get; set; }
    /// <inheritdoc cref="LedsMin"/>
    public uint LedsMax { get; set; }
    /// <summary>True when the controller advertises no room to resize this zone.</summary>
    public bool IsFixedSize => LedsMax > 0 && LedsMin == LedsMax;
    /// <summary>ZONE_FLAG_* bits, protocol 5+; 0 below it.</summary>
    public uint Flags { get; set; }
    /// <summary>ZONE_FLAG_MANUALLY_CONFIGURABLE_SIZE.</summary>
    public const uint FlagConfigurableSize = 1u << 1;
    /// <summary>ZONE_FLAG_MANUALLY_CONFIGURED_SIZE: set by a resize.</summary>
    public const uint FlagConfiguredSize = 1u << 12;
    /// <summary>True when the count is the user's to set and is still unset or came from a resize, never from the hardware (NZXT Hue+ reads its own).</summary>
    public bool IsUserSized => (Flags & FlagConfigurableSize) != 0 && (LedCount == 0 || (Flags & FlagConfiguredSize) != 0);
    /// <summary>Matrix width in columns, or 0 when the zone is a linear strip.</summary>
    public int MatrixWidth { get; set; }
    /// <summary>Matrix height in rows, or 0 when the zone is a linear strip.</summary>
    public int MatrixHeight { get; set; }
    /// <summary>Row-major grid of zone-local LED indices. -1 means the cell is unpopulated (gap in the key layout). null when no matrix was provided.</summary>
    public int[]? MatrixMap { get; set; }
}

/// <summary>
/// One mode entry from the OpenRGB controller data response. We keep the raw
/// bytes of the mode struct (everything from the name bstring through the
/// trailing colors[]) so we can echo it back verbatim via UPDATE_MODE without
/// having to re-serialize each field per OpenRGB's per-version wire format.
/// </summary>
public sealed class RgbMode
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    /// <summary>color_mode field: 0=NONE, 1=PER_LED, 2=MODE_SPECIFIC, 3=RANDOM.</summary>
    public uint ColorMode { get; set; }
    /// <summary>Raw mode-entry bytes as they appeared in the controller-data response.</summary>
    public byte[] Bytes { get; set; } = System.Array.Empty<byte>();
}
