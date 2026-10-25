using System;
using System.Globalization;

namespace Nexus.Service.Sensors;

/// <summary>
/// Names memory vendors from JEDEC JEP106 ids. The SMBIOS Type 17 Module Manufacturer ID field and the hex strings boards put in the
/// manufacturer field carry the continuation-code count first (low 7 bits, odd parity in bit 7) and the id code second (bit 7 is odd parity);
/// dmidecode prints that as "Bank (count+1), Hex code". Names come from <see cref="JedecVendorTable"/>. An id outside the table or with a
/// bad parity stays unnamed rather than guessed, and the caller keeps the raw id.
/// </summary>
internal static class JedecManufacturers
{
    /// <summary>True when <paramref name="text"/> is a hex id, with or without a 0x prefix.</summary>
    public static bool LooksLikeId(string text) => TryParseBytes(text, out _, out _);

    /// <summary>The vendor name for a hex id string (count byte then code byte), or null when unlisted or malformed.</summary>
    public static string? Decode(string text)
    {
        if (!TryParseBytes(text, out var count, out var code))
            return null;
        return Decode(count, code);
    }

    public static string? Decode(int countByte, int codeByte)
    {
        var bank = countByte & 0x7F;
        var id = codeByte & 0x7F;
        // Odd parity on the code byte, as decode-dimms requires; zero ids are padding.
        if (int.PopCount(codeByte & 0xFF) % 2 == 0 || id == 0)
            return null;
        if (bank >= JedecVendorTable.Banks.Count || id > JedecVendorTable.Banks[bank].Length)
            return null;
        return JedecVendorTable.Banks[bank][id - 1];
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
