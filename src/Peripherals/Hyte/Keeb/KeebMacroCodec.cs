using System;
using System.Collections.Generic;
using Nexus.Service.Persistence;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Encodes a persisted macro into the 4-page (4×65B) 0xF3 payload.
/// Wire layout (vendor LightDancing Macro.cs / KeyCode.cs): a 256-byte data
/// stream split across 4 output-report pages (each page byte 0 = report id
/// 0x00, then 64 data bytes). Stream = [Repeat_L, Repeat_H] then action pairs
/// [Attribute, KeyCode], terminated by [0x00, 0x00].
///
/// Attribute bit 7 = press(0)/release(1); bits 0..6 = delay in 10 ms units
/// (1..126). Delay 0x7F is the escape: the entry grows to 4 bytes
/// [marker, key, units_lo, units_hi] where the 16-bit value is ALSO in 10 ms
/// units (vendor KeyCode.GenerateCommands writes the same unit value in both
/// forms), so the longest encodable delay is 655,350 ms.
/// </summary>
public static class KeebMacroCodec
{
    public const int PageCount = 4;
    public const int DataBytes = PageCount * KeebLayout.PageDataSize; // 256

    /// <summary>
    /// The firmware stamps its AA AA 55 55 factory sentinel into the final 4
    /// bytes of the stored macro block (bench-verified: a written macro reads
    /// back with the tail replaced). Never encode into them, and ignore them
    /// on write→readback verification.
    /// </summary>
    public const int ReservedTailBytes = 4;

    /// <summary>Longest encodable per-action delay: 65535 units of 10 ms.</summary>
    public const int MaxDurationMs = ushort.MaxValue * 10;

    private const byte ReleaseBit = 0x80;
    private const byte ExtendedDelayMarker = 0x7F;
    private const int MaxInlineDelayUnits = 126; // 0x7E; 0x7F is reserved as the escape

    /// <summary>Encode outcome: the pages plus what could not ride the wire.</summary>
    public sealed class BuildResult
    {
        public byte[] Pages { get; set; } = Array.Empty<byte>();
        /// <summary>Distinct key names skipped because they have no HID usage mapping.</summary>
        public List<string> DroppedKeys { get; } = new();
        /// <summary>True when the encoded actions overflowed the 256-byte stream and were cut.</summary>
        public bool Truncated { get; set; }
    }

    /// <summary>
    /// Build the 4×65B page buffer for <paramref name="macro"/>. Repeat count is
    /// fixed at 1 (the panel records a one-shot sequence). Unmappable keys and
    /// overflow are reported on the result instead of vanishing silently.
    /// </summary>
    public static BuildResult Build(KeebMacroDocument macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        var result = new BuildResult();
        var data = new byte[DataBytes];
        data[0] = 0x01; // Repeat_L = 1
        data[1] = 0x00; // Repeat_H
        var pos = 2;

        foreach (var k in macro.Keys)
        {
            var hid = KeebKeyCodes.MacroHid(k.Key);
            if (hid == 0)
            {
                if (!result.DroppedKeys.Contains(k.Key)) result.DroppedKeys.Add(k.Key);
                continue;
            }

            var release = IsRelease(k.Type);
            // The attribute's delay field is 1-based; clamp so a sub-5 ms duration
            // never emits attribute 0x00/0x80, which the wire format does not
            // define for an action entry.
            var units = Math.Clamp((int)Math.Round(Math.Max(0, k.Duration) / 10.0), 1, ushort.MaxValue);

            if (units <= MaxInlineDelayUnits)
            {
                if (pos + 2 > DataBytes - 2 - ReservedTailBytes) { result.Truncated = true; break; }
                data[pos++] = (byte)((release ? ReleaseBit : 0) | (byte)units);
                data[pos++] = hid;
            }
            else
            {
                if (pos + 4 > DataBytes - 2 - ReservedTailBytes) { result.Truncated = true; break; }
                data[pos++] = (byte)((release ? ReleaseBit : 0) | ExtendedDelayMarker);
                data[pos++] = hid;
                data[pos++] = (byte)(units & 0xFF);
                data[pos++] = (byte)((units >> 8) & 0xFF);
            }
        }
        // [00,00] terminator is already in place (buffer zero-initialised).

        // Split into 4 pages, each prefixed with the 0x00 report id.
        var pages = new byte[PageCount * KeebLayout.PageSize];
        for (var p = 0; p < PageCount; p++)
        {
            var pageBase = p * KeebLayout.PageSize;
            // pages[pageBase] = report id 0x00 (already zero)
            Array.Copy(data, p * KeebLayout.PageDataSize, pages, pageBase + 1, KeebLayout.PageDataSize);
        }
        result.Pages = pages;
        return result;
    }

    private static bool IsRelease(string? type) => (type ?? "").Trim().ToLowerInvariant() switch
    {
        "break" or "keyup" or "release" => true,
        _ => false, // Make / KeyDown / press
    };
}
