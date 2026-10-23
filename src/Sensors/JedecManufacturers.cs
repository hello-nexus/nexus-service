using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nexus.Service.Sensors;

/// <summary>
/// Names memory vendors from JEDEC JEP106 manufacturer ids. SMBIOS reports the id as hex
/// text (continuation-byte count then id byte) on boards that do not write a vendor name.
/// Only vendors confirmed from real module readouts are listed; an unlisted id stays
/// unnamed rather than guessed, and the caller keeps the raw id.
/// </summary>
internal static class JedecManufacturers
{
    // Key: (bank, id with parity bit stripped). Bank is the continuation-byte count plus one.
    private static readonly Dictionary<(int Bank, int Id), string> Names = new()
    {
        [(1, 0x2C)] = "Micron",
        [(1, 0x4E)] = "Samsung",
        [(1, 0x2D)] = "SK hynix",
        [(2, 0x18)] = "Kingston",
        [(3, 0x1E)] = "Corsair",
        [(5, 0x4B)] = "A-DATA",
        [(5, 0x4D)] = "G.Skill",
        [(6, 0x1B)] = "Crucial",
    };

    /// <summary>True when <paramref name="text"/> is a four-digit hex id, with or without a 0x prefix.</summary>
    public static bool LooksLikeId(string text) => TryParseBytes(text, out _, out _);

    /// <summary>The vendor name for a hex id string in either byte order, or null when unlisted.</summary>
    public static string? Decode(string text)
    {
        if (!TryParseBytes(text, out var first, out var second))
            return null;
        // Firmware prints either (continuation, id) or the swapped pair.
        return Lookup(first, second) ?? Lookup(second, first);
    }

    private static string? Lookup(int continuation, int idByte)
    {
        var bank = (continuation & 0x7F) + 1;
        return Names.TryGetValue((bank, idByte & 0x7F), out var name) ? name : null;
    }

    private static bool TryParseBytes(string text, out int first, out int second)
    {
        first = second = 0;
        var t = (text ?? "").Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            t = t[2..];
        if (t.Length != 4)
            return false;
        return int.TryParse(t.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out first)
            && int.TryParse(t.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out second);
    }
}
