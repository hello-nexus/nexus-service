using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Xunit;

namespace Nexus.Service.Tests.Keeb;

/// <summary>
/// Gold test for the web-cell → firmware-slot map: the FACTORY layer-0 table
/// captured from a live Keeb TKL (T1 bench, ANSI, fw 1.33, 2026-07-11) must be
/// reproducible cell-for-cell from the map defaults + <see cref="KeebKeyCodes"/>.
/// This pins the slot indices, the encoder byte order, and the special slots
/// (vendor RGB key at 1, sentinel at 127) against real firmware bytes.
/// </summary>
public class KeebLayerMapTests
{
    // 8 pages × 65 bytes as returned by KeebHub.ReadLayerRaw (report-id byte
    // leads each page). Captured via GET /keeb/debug/layer-raw/0.
    internal const string FactoryLayer0Hex =
        "00290000010300000A3A0000013B0000013C0000013D0000013E0000013F000001400000014100" +
        "000142000001430000014400000145000001460000014700000100480000010000000000000000" +
        "0000000000000000350000011E0000011F00000120000001210000012200000123000001240000" +
        "01250000012600000127000001002D0000012E0000012A000001490000014A0000014B00000100" +
        "0000000000000000000000000000002B000001140000011A000001080000011500000117000001" +
        "001C000001180000010C00000112000001130000012F00000130000001310000014C0000014D00" +
        "00014E000001000000000000000000000000000000003900000100040000011600000107000001" +
        "090000010A0000010B0000010D0000010E0000010F000001330000013400000100000000280000" +
        "01B7000003B6000003CD0000030000000000000000000000000000000000E1000001000000001D" +
        "0000011B0000010600000119000001050000011100000110000001360000013700000138000001" +
        "0000000000E5000001B500000352000001E200000300000000000000000000000000000000E000" +
        "0001E3000001E20000010000000000000000000000002C00000100000000000000000000000000" +
        "E6000001150100F0E7000001E400000150000001510000014F0000010000000000000000000000" +
        "000000000000000000AAAA5555";

    internal static byte[] FactoryLayer0Pages => Convert.FromHexString(FactoryLayer0Hex);

    private static int? DefaultInput(KeebLayerCell cell)
        // The factory MO key targets layer 1; every other default carries none.
        => cell.Function == "MOSwitch" ? 1 : null;

    [Fact]
    public void Ansi_map_defaults_rebuild_the_factory_layer0_dump_cell_for_cell()
    {
        var pages = FactoryLayer0Pages;
        foreach (var row in KeebLayerMap.Rows("ANSI"))
        {
            foreach (var cell in row)
            {
                Assert.True(
                    KeebKeyCodes.TryMatrixCode(cell.Mode, cell.Function, DefaultInput(cell), out var code),
                    $"default {cell.Mode}/{cell.Function} must encode");
                var onWire = KeebLayerCodec.ReadSlot(pages, cell.Slot);
                Assert.True(onWire.SequenceEqual(code),
                    $"{cell.Function} at slot {cell.Slot}: expected {Convert.ToHexString(code)}, dump has {Convert.ToHexString(onWire)}");
            }
        }
    }

    [Fact]
    public void Every_nonzero_dump_slot_is_covered_by_the_map_or_a_known_special()
    {
        var pages = FactoryLayer0Pages;
        var mapped = KeebLayerMap.Rows("ANSI").SelectMany(r => r).Select(c => c.Slot).ToHashSet();
        for (var slot = 0; slot < KeebLayerMap.SlotCount; slot++)
        {
            var code = KeebLayerCodec.ReadSlot(pages, slot);
            if (code.All(b => b == 0)) continue;
            if (slot == 127) continue; // factory sentinel AA AA 55 55
            Assert.True(mapped.Contains(slot), $"unmapped non-zero slot {slot}: {Convert.ToHexString(code)}");
        }
    }

    [Fact]
    public void Slots_are_unique_within_each_layout()
    {
        foreach (var layout in new[] { "ANSI", "ISO" })
        {
            var slots = KeebLayerMap.Rows(layout).SelectMany(r => r).Select(c => c.Slot).ToList();
            Assert.Equal(slots.Count, slots.Distinct().Count());
        }
    }

    [Fact]
    public void Iso_layout_places_the_three_iso_keys_on_their_vendor_slots()
    {
        // Vendor ISO_COMMAND_INDEX (1-based): big Return 77, Europe1 76,
        // Europe2 86 → 0-based 76 / 75 / 85. Slot 55 (ANSI Backslash) unused.
        var cells = KeebLayerMap.Rows("ISO").SelectMany(r => r).ToList();
        Assert.Equal(76, cells.Single(c => c.Function == "Return").Slot);
        Assert.Equal(75, cells.Single(c => c.Function == "NonUsPound").Slot);
        Assert.Equal(85, cells.Single(c => c.Function == "NonUsBackslash").Slot);
        Assert.DoesNotContain(cells, c => c.Slot == 55);
        Assert.DoesNotContain(cells, c => c.Function == "Backslash");
    }

    [Fact]
    public void Both_layouts_expose_the_same_row_count_and_row7_shape()
    {
        var ansi = KeebLayerMap.Rows("ANSI");
        var iso = KeebLayerMap.Rows("ISO");
        Assert.Equal(8, ansi.Count);
        Assert.Equal(8, iso.Count);
        Assert.Equal(
            ansi[7].Select(c => c.Function),
            iso[7].Select(c => c.Function));
    }

    [Fact]
    public void TryGetCell_rejects_out_of_range_coordinates()
    {
        Assert.False(KeebLayerMap.TryGetCell("ANSI", -1, 0, out _));
        Assert.False(KeebLayerMap.TryGetCell("ANSI", 8, 0, out _));
        Assert.False(KeebLayerMap.TryGetCell("ANSI", 2, 99, out _));
        Assert.True(KeebLayerMap.TryGetCell("ANSI", 2, 0, out var esc));
        Assert.Equal("Escape", esc.Function);
        Assert.Equal(0, esc.Slot);
    }
}

/// <summary>Overlay composition against the same factory fixture.</summary>
public class KeebLayerCodecTests
{
    private static byte[] Pristine => KeebLayerMapTests.FactoryLayer0Pages;

    [Fact]
    public void ComposePages_with_no_overlays_is_byte_identical_to_pristine()
    {
        var composed = KeebLayerCodec.ComposePages(Pristine, new List<(int, byte[])>());
        Assert.True(composed.SequenceEqual(Pristine));
    }

    [Fact]
    public void ComposePages_changes_exactly_the_overlaid_slot()
    {
        // Remap Q (slot 43) to MouseLButton.
        Assert.True(KeebKeyCodes.TryMatrixCode("MouseKey", "MouseLButton", null, out var code));
        var composed = KeebLayerCodec.ComposePages(Pristine, new[] { (43, code) });

        Assert.True(KeebLayerCodec.ReadSlot(composed, 43).SequenceEqual(code));
        for (var slot = 0; slot < KeebLayerMap.SlotCount; slot++)
        {
            if (slot == 43) continue;
            Assert.True(KeebLayerCodec.ReadSlot(composed, slot)
                .SequenceEqual(KeebLayerCodec.ReadSlot(Pristine, slot)), $"slot {slot} must be untouched");
        }
        // Report-id bytes stay zero.
        for (var p = 0; p < KeebLayerCodec.PageCount; p++)
            Assert.Equal(0x00, composed[p * KeebLayout.PageSize]);
    }

    [Fact]
    public void ComposePages_preserves_the_factory_sentinel_and_vendor_slot()
    {
        Assert.True(KeebKeyCodes.TryMatrixCode("StandardKey", "A", null, out var code));
        var composed = KeebLayerCodec.ComposePages(Pristine, new[] { (64, code) });
        Assert.Equal(new byte[] { 0xAA, 0xAA, 0x55, 0x55 }, KeebLayerCodec.ReadSlot(composed, 127));
        Assert.Equal(new byte[] { 0x03, 0x00, 0x00, 0x0A }, KeebLayerCodec.ReadSlot(composed, 1));
    }

    [Fact]
    public void PassThrough_encodes_as_the_transparent_category()
    {
        Assert.True(KeebKeyCodes.TryMatrixCode("StandardKey", "PassThrough", null, out var code));
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0xF8 }, code);
    }

    [Fact]
    public void Unknown_functions_are_rejected_not_encoded_as_zeros()
    {
        Assert.False(KeebKeyCodes.TryMatrixCode("StandardKey", "Bogus", null, out _));
        Assert.False(KeebKeyCodes.TryMatrixCode("RGBKey", "Bogus", null, out _));
        Assert.False(KeebKeyCodes.TryMatrixCode("SoftwareKey", "Anything", null, out _));
        Assert.False(KeebKeyCodes.TryMatrixCode("NoSuchMode", "A", null, out _));
    }

    [Fact]
    public void Layer_switch_kinds_use_distinct_vendor_bytes()
    {
        Assert.True(KeebKeyCodes.TryMatrixCode("LayerKey", "MOSwitch", 1, out var mo));
        Assert.True(KeebKeyCodes.TryMatrixCode("LayerKey", "TGSwitch", 2, out var tg));
        Assert.True(KeebKeyCodes.TryMatrixCode("LayerKey", "TOSwitch", 3, out var to));
        Assert.True(KeebKeyCodes.TryMatrixCode("LayerKey", "DFSwitch", 0, out var df));
        Assert.Equal(new byte[] { 0x15, 0x01, 0x00, 0xF0 }, mo);
        Assert.Equal(new byte[] { 0x16, 0x02, 0x00, 0xF0 }, tg);
        Assert.Equal(new byte[] { 0x17, 0x03, 0x00, 0xF0 }, to);
        Assert.Equal(new byte[] { 0x18, 0x00, 0x00, 0xF0 }, df);
    }

    [Fact]
    public void Rgb_and_profile_codes_match_the_vendor_dictionary()
    {
        Assert.True(KeebKeyCodes.TryMatrixCode("RGBKey", "RGBOnOff", null, out var onOff));
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00, 0x0A }, onOff);
        Assert.True(KeebKeyCodes.TryMatrixCode("RGBKey", "SpeedLoop", null, out var speedLoop));
        Assert.Equal(new byte[] { 0x03, 0x00, 0x02, 0x0A }, speedLoop);
        Assert.True(KeebKeyCodes.TryMatrixCode("RGBKey", "RGBEffectValue", 4, out var fxValue));
        Assert.Equal(new byte[] { 0x04, 0x04, 0x00, 0x0A }, fxValue);
        Assert.True(KeebKeyCodes.TryMatrixCode("ProfileKey", "ProfilePlus", null, out var plus));
        Assert.Equal(new byte[] { 0x04, 0x01, 0x01, 0xF0 }, plus);
        Assert.True(KeebKeyCodes.TryMatrixCode("ProfileKey", "ProfileValue", 1, out var value));
        Assert.Equal(new byte[] { 0x04, 0x01, 0x01, 0xF0 }, value);
        Assert.True(KeebKeyCodes.TryMatrixCode("ProfileKey", "ProfilePlusLoop", null, out var loop));
        Assert.Equal(new byte[] { 0x03, 0x00, 0x01, 0xF0 }, loop);
    }
}
