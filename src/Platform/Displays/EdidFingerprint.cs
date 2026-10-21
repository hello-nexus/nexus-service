using System;
using System.Text;
using Nexus.Service.Models.Displays;

namespace Nexus.Service.Platform.Displays;

/// <summary>Builds a <see cref="DisplayFingerprint"/> from raw EDID bytes or from already-decoded identity parts.</summary>
public static class EdidFingerprint
{
    /// <summary>Null when the blob is not a base EDID block.</summary>
    public static DisplayFingerprint? FromEdid(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 128) return null;
        for (var i = 1; i <= 6; i++)
        {
            if (edid[i] != 0xFF) return null;
        }
        if (edid[0] != 0x00 || edid[7] != 0x00) return null;

        var packed = (edid[8] << 8) | edid[9];
        var manufacturer = new string(new[]
        {
            (char)(((packed >> 10) & 0x1F) + '@'),
            (char)(((packed >> 5) & 0x1F) + '@'),
            (char)((packed & 0x1F) + '@'),
        });
        var product = edid[10] | (edid[11] << 8);
        long serial = edid[12] | ((long)edid[13] << 8) | ((long)edid[14] << 16) | ((long)edid[15] << 24);

        string? serialText = null;
        for (var offset = 54; offset <= 108; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0 || edid[offset + 3] != 0xFF) continue;
            var text = Encoding.ASCII.GetString(edid.Slice(offset + 5, 13)).Split('\n', '\0')[0].Trim();
            if (text.Length > 0) serialText = text;
            break;
        }

        return new DisplayFingerprint
        {
            Manufacturer = manufacturer,
            Product = product.ToString("X4"),
            Serial = serial,
            SerialText = serialText,
        };
    }

    /// <summary>Partial fingerprint (no serial) for a platform that exposes only vendor and model; null when either is missing.</summary>
    public static DisplayFingerprint? FromParts(string? manufacturer, string? product, long serial = 0)
    {
        if (string.IsNullOrEmpty(manufacturer) || string.IsNullOrEmpty(product)) return null;
        return new DisplayFingerprint { Manufacturer = manufacturer, Product = product, Serial = serial };
    }
}
