using Nexus.Service.Peripherals.Hyte.Keeb;

namespace Nexus.Service.Tests.Keeb;

/// <summary>
/// <see cref="KeebLayerCodec.DataEquals"/> is the sole write→readback verify
/// gate for both layer tables and macros: it must compare only the 64-byte
/// data region of each 65-byte page (byte 0 is the report id, device-defined
/// on reads) and ignore the firmware-owned tail of the LAST page only.
/// </summary>
public class KeebLayerCodecVerifyTests
{
    private const int Pages = 4;

    private static byte[] Buffer(byte fill = 0x11)
    {
        var b = new byte[Pages * KeebLayout.PageSize];
        Array.Fill(b, fill);
        return b;
    }

    [Fact]
    public void DataEquals_ignores_the_report_id_byte_of_every_page()
    {
        var wrote = Buffer();
        var read = Buffer();
        for (var p = 0; p < Pages; p++) read[p * KeebLayout.PageSize] = 0xEE;
        Assert.True(KeebLayerCodec.DataEquals(wrote, read, Pages));
    }

    [Fact]
    public void DataEquals_fails_on_a_data_byte_difference()
    {
        var wrote = Buffer();
        var read = Buffer();
        read[2 * KeebLayout.PageSize + 1 + 30] = 0xEE;
        Assert.False(KeebLayerCodec.DataEquals(wrote, read, Pages));
    }

    [Fact]
    public void DataEquals_tail_mismatch_passes_only_with_ignoreTailBytes()
    {
        var wrote = Buffer();
        var read = Buffer();
        // Firmware sentinel stamped over the last 4 data bytes of the LAST page.
        var tail = (Pages - 1) * KeebLayout.PageSize + 1 + KeebLayout.PageDataSize - 4;
        read[tail] = 0xAA; read[tail + 1] = 0xAA; read[tail + 2] = 0x55; read[tail + 3] = 0x55;
        Assert.False(KeebLayerCodec.DataEquals(wrote, read, Pages));
        Assert.True(KeebLayerCodec.DataEquals(wrote, read, Pages, ignoreTailBytes: 4));
    }

    [Fact]
    public void DataEquals_ignoreTailBytes_applies_to_the_last_page_only()
    {
        var wrote = Buffer();
        var read = Buffer();
        // Same offset-from-page-end on a MIDDLE page must still be compared.
        var midTail = 1 * KeebLayout.PageSize + 1 + KeebLayout.PageDataSize - 2;
        read[midTail] = 0xEE;
        Assert.False(KeebLayerCodec.DataEquals(wrote, read, Pages, ignoreTailBytes: 4));
    }

    [Fact]
    public void DataEquals_rejects_short_buffers()
    {
        var full = Buffer();
        var shortBuf = new byte[Pages * KeebLayout.PageSize - 1];
        Assert.False(KeebLayerCodec.DataEquals(shortBuf, full, Pages));
        Assert.False(KeebLayerCodec.DataEquals(full, shortBuf, Pages));
    }

    [Fact]
    public void DescribeMismatch_reports_page_offset_and_bounded_window()
    {
        var wrote = Buffer();
        var read = Buffer();
        read[2 * KeebLayout.PageSize + 1 + 30] = 0xEE;
        var s = KeebLayerCodec.DescribeMismatch(wrote, read, Pages);
        Assert.StartsWith("page 2 data+30: wrote 11", s);
        Assert.Contains("read EE", s);
        // A bounded window each side, not the full buffers.
        Assert.True(s.Length <= 40 + KeebLayerCodec.MismatchHexWindow * 4);
    }

    [Fact]
    public void DescribeMismatch_skips_the_ignored_tail()
    {
        var wrote = Buffer();
        var read = Buffer();
        var tail = (Pages - 1) * KeebLayout.PageSize + 1 + KeebLayout.PageDataSize - 4;
        read[tail] = 0xAA;
        Assert.Equal("identical in compared region", KeebLayerCodec.DescribeMismatch(wrote, read, Pages, ignoreTailBytes: 4));
    }
}
