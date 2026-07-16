using System;
using System.Collections.Generic;

namespace Nexus.Service.Peripherals.Hyte.Keeb;

/// <summary>
/// Pure helpers for the 0xF2 layer-table page buffer: 8 pages × 65 bytes
/// (byte 0 of each page = report id 0x00, then 64 data bytes) = 512 data
/// bytes = 128 slots × 4-byte key-matrix codes.
///
/// Layer writes always start from the DEVICE'S OWN pristine table (captured
/// via <see cref="KeebHub.ReadLayerRaw"/> before the first Nexus write) and
/// overlay the persisted per-cell assignments onto it. This preserves the
/// factory sentinel at slot 127 (AA AA 55 55, which the vendor app zeroes on
/// any remap) and every vendor-category slot Nexus does not model, and makes
/// reset-to-default a byte-exact factory restore rather than a rebuilt guess.
/// </summary>
public static class KeebLayerCodec
{
    public const int PageCount = KeebProtocol.LayerPageCount; // 8
    public const int PagesBytes = PageCount * KeebLayout.PageSize; // 520
    public const int SlotBytes = 4;

    /// <summary>Bytes of each buffer shown per side in a verify-mismatch log line.</summary>
    public const int MismatchHexWindow = 16;

    /// <summary>Copy <paramref name="pristinePages"/> and stamp each overlay's 4-byte code onto its slot.</summary>
    public static byte[] ComposePages(byte[] pristinePages, IEnumerable<(int Slot, byte[] Code)> overlays)
    {
        ArgumentNullException.ThrowIfNull(pristinePages);
        if (pristinePages.Length != PagesBytes)
            throw new ArgumentException($"Layer page buffer must be {PagesBytes} bytes.", nameof(pristinePages));
        var pages = (byte[])pristinePages.Clone();
        foreach (var (slot, code) in overlays) WriteSlot(pages, slot, code);
        return pages;
    }

    /// <summary>Stamp a 4-byte slot code into a page buffer (report-id bytes accounted for).</summary>
    public static void WriteSlot(byte[] pages, int slot, ReadOnlySpan<byte> code)
    {
        if ((uint)slot >= KeebLayerMap.SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot));
        if (code.Length != SlotBytes)
            throw new ArgumentException($"Slot code must be {SlotBytes} bytes.", nameof(code));
        // 16 slots per 64-byte page, so a code never straddles a page boundary.
        var dataOffset = slot * SlotBytes;
        var page = dataOffset / KeebLayout.PageDataSize;
        var index = page * KeebLayout.PageSize + 1 + dataOffset % KeebLayout.PageDataSize;
        code.CopyTo(pages.AsSpan(index, SlotBytes));
    }

    /// <summary>
    /// Compare the DATA regions of two page buffers (bytes 1..64 of each
    /// 65-byte page). The leading byte is the report id on writes but is
    /// device-defined on read responses (the vendor driver strips it), so a
    /// write→readback verify must ignore it.
    /// </summary>
    public static bool DataEquals(byte[] a, byte[] b, int pageCount, int ignoreTailBytes = 0)
    {
        if (a.Length < pageCount * KeebLayout.PageSize || b.Length < pageCount * KeebLayout.PageSize)
            return false;
        for (var p = 0; p < pageCount; p++)
        {
            var basePos = p * KeebLayout.PageSize + 1;
            var len = KeebLayout.PageDataSize;
            // The trailing bytes of the LAST page can be firmware-reserved
            // (the macro block's AA AA 55 55 sentinel) - the device returns
            // its own value there regardless of what was written.
            if (p == pageCount - 1) len -= ignoreTailBytes;
            if (!a.AsSpan(basePos, len).SequenceEqual(b.AsSpan(basePos, len)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Locate the first differing data byte between a written and a
    /// read-back page buffer (same comparison domain as
    /// <see cref="DataEquals"/>) and format it with a bounded hex window,
    /// so a verify-mismatch log line stays small instead of dumping both
    /// full buffers.
    /// </summary>
    public static string DescribeMismatch(byte[] wrote, byte[] read, int pageCount, int ignoreTailBytes = 0)
    {
        if (wrote.Length < pageCount * KeebLayout.PageSize || read.Length < pageCount * KeebLayout.PageSize)
            return $"length wrote={wrote.Length} read={read.Length}";
        for (var p = 0; p < pageCount; p++)
        {
            var basePos = p * KeebLayout.PageSize + 1;
            var len = KeebLayout.PageDataSize;
            if (p == pageCount - 1) len -= ignoreTailBytes;
            for (var i = 0; i < len; i++)
            {
                if (wrote[basePos + i] == read[basePos + i]) continue;
                var window = Math.Min(MismatchHexWindow, len - i);
                return $"page {p} data+{i}: wrote {Convert.ToHexString(wrote.AsSpan(basePos + i, window))} read {Convert.ToHexString(read.AsSpan(basePos + i, window))}";
            }
        }
        return "identical in compared region";
    }

    /// <summary>Read a slot's 4-byte code out of a page buffer.</summary>
    public static byte[] ReadSlot(byte[] pages, int slot)
    {
        if ((uint)slot >= KeebLayerMap.SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot));
        var dataOffset = slot * SlotBytes;
        var page = dataOffset / KeebLayout.PageDataSize;
        var index = page * KeebLayout.PageSize + 1 + dataOffset % KeebLayout.PageDataSize;
        return pages.AsSpan(index, SlotBytes).ToArray();
    }
}
