using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Keeb;

/// <summary>
/// Golden vectors for the 0xF3 macro payload (vendor LightDancing Macro.cs /
/// KeyCode.cs): 4×65B pages, [Repeat_L, Repeat_H] then [Attribute, KeyCode]
/// action pairs, attr bit7 = press(0)/release(1), bits0-6 = delay in 10 ms
/// units, 0x7F = extended escape with a 16-bit unit count.
/// </summary>
public class KeebMacroCodecTests
{
    private static byte[] FirstPageData(byte[] pages)
    {
        // page 0 = bytes 0..64; byte 0 is the report id, data starts at 1.
        var data = new byte[KeebLayout.PageDataSize];
        System.Array.Copy(pages, 1, data, 0, KeebLayout.PageDataSize);
        return data;
    }

    private static byte[] AllData(byte[] pages)
    {
        var data = new byte[KeebMacroCodec.DataBytes];
        for (var p = 0; p < KeebMacroCodec.PageCount; p++)
            System.Array.Copy(pages, p * KeebLayout.PageSize + 1, data, p * KeebLayout.PageDataSize, KeebLayout.PageDataSize);
        return data;
    }

    [Fact]
    public void Build_emits_4_pages_with_report_ids()
    {
        var r = KeebMacroCodec.Build(new KeebMacroDocument());
        Assert.Equal(KeebMacroCodec.PageCount * KeebLayout.PageSize, r.Pages.Length); // 260
        for (var p = 0; p < KeebMacroCodec.PageCount; p++)
            Assert.Equal(0x00, r.Pages[p * KeebLayout.PageSize]);
        Assert.False(r.Truncated);
        Assert.Empty(r.DroppedKeys);
    }

    [Fact]
    public void Build_press_then_release_with_delays()
    {
        var doc = new KeebMacroDocument
        {
            Keys =
            {
                new KeebMacroKey { Key = "KeyA", Type = "Make", Duration = 10 },
                new KeebMacroKey { Key = "KeyA", Type = "Break", Duration = 50 },
            },
        };
        var data = FirstPageData(KeebMacroCodec.Build(doc).Pages);

        Assert.Equal(0x01, data[0]); // Repeat_L = 1
        Assert.Equal(0x00, data[1]); // Repeat_H
        Assert.Equal(0x01, data[2]); // press, delay 1 unit (10ms)
        Assert.Equal(0x04, data[3]); // KeyA HID
        Assert.Equal(0x85, data[4]); // release(0x80) | delay 5 units (50ms)
        Assert.Equal(0x04, data[5]); // KeyA HID
        Assert.Equal(0x00, data[6]); // terminator
        Assert.Equal(0x00, data[7]);
    }

    [Fact]
    public void Build_overlapping_holds_encode_as_ordered_press_release_stream()
    {
        // A chord: Ctrl down, C down, C up, Ctrl up.
        var doc = new KeebMacroDocument
        {
            Keys =
            {
                new KeebMacroKey { Key = "ControlLeft", Type = "Make", Duration = 10 },
                new KeebMacroKey { Key = "KeyC", Type = "Make", Duration = 10 },
                new KeebMacroKey { Key = "KeyC", Type = "Break", Duration = 10 },
                new KeebMacroKey { Key = "ControlLeft", Type = "Break", Duration = 10 },
            },
        };
        var data = FirstPageData(KeebMacroCodec.Build(doc).Pages);
        Assert.Equal(new byte[] { 0x01, 0xE0, 0x01, 0x06, 0x81, 0x06, 0x81, 0xE0 }, data[2..10]);
    }

    [Fact]
    public void Build_extended_delay_uses_7F_escape_plus_16bit_unit_count()
    {
        var doc = new KeebMacroDocument
        {
            Keys = { new KeebMacroKey { Key = "KeyA", Type = "Make", Duration = 2000 } },
        };
        var data = FirstPageData(KeebMacroCodec.Build(doc).Pages);

        // 2000 ms = 200 units of 10 ms - the 16-bit field carries UNITS
        // (vendor KeyCode.GenerateCommands), not milliseconds.
        Assert.Equal(0x7F, data[2]);        // press | extended-delay escape
        Assert.Equal(0x04, data[3]);        // KeyA HID
        Assert.Equal(200, data[4]);         // units low
        Assert.Equal(0, data[5]);           // units high
    }

    [Fact]
    public void Build_extended_release_sets_the_break_bit_on_the_escape()
    {
        var doc = new KeebMacroDocument
        {
            Keys = { new KeebMacroKey { Key = "KeyA", Type = "Break", Duration = 30000 } },
        };
        var data = FirstPageData(KeebMacroCodec.Build(doc).Pages);
        Assert.Equal(0xFF, data[2]);              // release(0x80) | 0x7F
        Assert.Equal(0x04, data[3]);
        Assert.Equal(3000 & 0xFF, data[4]);       // 30000 ms = 3000 units
        Assert.Equal(3000 >> 8 & 0xFF, data[5]);
    }

    [Fact]
    public void Build_clamps_sub_5ms_durations_to_one_unit()
    {
        var doc = new KeebMacroDocument
        {
            Keys = { new KeebMacroKey { Key = "KeyA", Type = "Make", Duration = 0 } },
        };
        var data = FirstPageData(KeebMacroCodec.Build(doc).Pages);
        // Attribute 0x00 is the stream terminator, never a valid action.
        Assert.Equal(0x01, data[2]);
        Assert.Equal(0x04, data[3]);
    }

    [Fact]
    public void Build_reports_unmappable_keys_instead_of_dropping_silently()
    {
        var doc = new KeebMacroDocument
        {
            Keys =
            {
                new KeebMacroKey { Key = "NotAKey", Type = "Make", Duration = 10 },
                new KeebMacroKey { Key = "KeyB", Type = "Make", Duration = 10 },
                new KeebMacroKey { Key = "NotAKey", Type = "Break", Duration = 10 },
            },
        };
        var r = KeebMacroCodec.Build(doc);
        Assert.Equal(new[] { "NotAKey" }, r.DroppedKeys);
        var data = FirstPageData(r.Pages);
        Assert.Equal(0x01, data[2]); // KeyB press is the only action
        Assert.Equal(0x05, data[3]);
        Assert.Equal(0x00, data[4]);
    }

    [Fact]
    public void Build_truncates_at_the_stream_limit_and_says_so()
    {
        var doc = new KeebMacroDocument();
        // 200 press/release pairs = 400 actions = 800 bytes, far over the
        // 250-byte action budget (256 - repeat header - terminator).
        for (var i = 0; i < 200; i++)
        {
            doc.Keys.Add(new KeebMacroKey { Key = "KeyA", Type = "Make", Duration = 10 });
            doc.Keys.Add(new KeebMacroKey { Key = "KeyA", Type = "Break", Duration = 10 });
        }
        var r = KeebMacroCodec.Build(doc);
        Assert.True(r.Truncated);
        var data = AllData(r.Pages);
        // The firmware-reserved tail (sentinel area) stays untouched and the
        // terminator fits before it.
        for (var i = KeebMacroCodec.DataBytes - KeebMacroCodec.ReservedTailBytes; i < KeebMacroCodec.DataBytes; i++)
            Assert.Equal(0x00, data[i]);
        // The stream is packed right up to the budget.
        Assert.NotEqual(0x00, data[KeebMacroCodec.DataBytes - KeebMacroCodec.ReservedTailBytes - 4]);
    }

    [Fact]
    public void Build_actions_straddle_page_boundaries_contiguously()
    {
        // 40 pairs = 160 action bytes + 2 header: the stream crosses the
        // 64-byte page-1/page-2 boundary mid-way. The data must be contiguous
        // when pages are re-joined.
        var doc = new KeebMacroDocument();
        for (var i = 0; i < 40; i++)
        {
            doc.Keys.Add(new KeebMacroKey { Key = "KeyA", Type = "Make", Duration = 10 });
            doc.Keys.Add(new KeebMacroKey { Key = "KeyA", Type = "Break", Duration = 20 });
        }
        var r = KeebMacroCodec.Build(doc);
        Assert.False(r.Truncated);
        var data = AllData(r.Pages);
        for (var i = 0; i < 40; i++)
        {
            Assert.Equal(0x01, data[2 + i * 4]);
            Assert.Equal(0x04, data[3 + i * 4]);
            Assert.Equal(0x82, data[4 + i * 4]);
            Assert.Equal(0x04, data[5 + i * 4]);
        }
    }
}
