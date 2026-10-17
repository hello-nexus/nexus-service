using Nexus.Service.Peripherals.Hid;

namespace Nexus.Service.Tests.Hid;

/// <summary>
/// Report-length derivation from raw HID report descriptors. The long hex vectors are
/// the ACTUAL descriptors read from /sys/class/hidraw/hidrawN/device/report_descriptor
/// on the Bazzite lab box. Lengths follow the Windows HidP_GetCaps convention: bytes
/// including the report-ID byte, counted even when no report IDs are declared.
/// </summary>
public class HidReportDescriptorSizesTests
{
    private static byte[] Hex(string h) => Convert.FromHexString(h.Replace(" ", ""));

    [Theory]
    // ASUS 0B05:19AF hidraw0: report ID 0xEC, 64 data bytes in and out.
    [InlineData("0672ff09a1a10185ec0910150026ff007508954081020911150026ff00750895409102c0", 65, 65, 0)]
    // Touchscreen hidraw5: IDs 0x50 / 0x51, 63 data bytes + ID byte.
    [InlineData("060aff09ffa101855009ff15002600ff7508953f8106855109ff15002600ff7508953f9106c0", 64, 64, 0)]
    // Multi-ID composite hidraw1: max across IDs per kind.
    [InlineData("050c0901a1018501150025017501950209cd09b581027501950209b609b781027501950409b009b109b309b481027501950409e909ea09e209cf8102750195048101c00600ff0901a10185401500250109027501950181060903750195018106950681018541150026ff00090195027508910285420901810285430901810285440901950491028545090181028546950209018102854709018102c00699ff0901a101859b090415002501950175018106750195078101859a150026ff00951f75080903b20201c0050b0905a101850515002501092f750195018106092075019501812295068101050885050909750195019122050b092a75019501912295069101c00653ff0a0404a10185027508953f150026ff000904810009049100c0", 64, 64, 32)]
    // Mouse hidraw6: input only.
    [InlineData("05010902a10185070901a100050919012903150025017501950381027501950581030501093075109501550e6511350046790826ff3f81020931468a03267f25810209381581257f750895018106c0c0", 7, 0, 0)]
    public void Real_box_descriptors(string hex, int input, int output, int feature)
    {
        Assert.Equal((input, output, feature), HidReportDescriptorSizes.Compute(Hex(hex)));
    }

    [Fact]
    public void No_report_id_still_counts_the_id_byte()
    {
        var d = Hex("06 00ff 09 01 a1 01 15 00 26 ff00 75 08 95 40 81 02 91 02 c0");
        Assert.Equal((65, 65, 0), HidReportDescriptorSizes.Compute(d));
    }

    [Fact]
    public void Partial_bytes_round_up()
    {
        var d = Hex("0501 0901 a101 7501 9503 8102 c0");
        Assert.Equal((2, 0, 0), HidReportDescriptorSizes.Compute(d));
    }

    [Fact]
    public void Push_and_pop_restore_globals()
    {
        // 16x2 input, then after pop 8x4 input: 4 + 4 bytes in one report, plus the ID byte.
        var d = Hex("0600ff 0901 a101 7508 9504 a4 7510 9502 8102 b4 8102 c0");
        Assert.Equal((9, 0, 0), HidReportDescriptorSizes.Compute(d));
    }

    [Fact]
    public void Long_item_is_skipped()
    {
        var d = Hex("0600ff 09 01 a1 01 fe 02 10 aa bb 75 08 95 08 81 02 c0");
        Assert.Equal((9, 0, 0), HidReportDescriptorSizes.Compute(d));
    }

    [Theory]
    [InlineData("")]
    [InlineData("75")]
    [InlineData("7508 95")]
    [InlineData("fe")]
    [InlineData("fe 10 01")]
    [InlineData("b4")]
    [InlineData("85 00 7508 9501 8102")]
    [InlineData("77 ffffff7f 97 ffffff7f 8102")]
    public void Malformed_returns_zero(string hex)
    {
        Assert.Equal((0, 0, 0), HidReportDescriptorSizes.Compute(Hex(hex)));
    }
}
