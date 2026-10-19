using System;
using System.Text;
using Nexus.Service.Sensors;
using Xunit;

namespace Nexus.Service.Tests.Sensors;

public class WindowsSpecsParsersTests
{
    private const string Key = "AAAAA-BBBBB-CCCCC-DDDDD-EEEEE";

    private static byte[] BuildMsdm(string key, uint? declaredLength = null)
    {
        var data = Encoding.ASCII.GetBytes(key);
        var table = new byte[56 + data.Length];
        Encoding.ASCII.GetBytes("MSDM").CopyTo(table, 0);
        BitConverter.GetBytes((uint)table.Length).CopyTo(table, 4);
        BitConverter.GetBytes(1u).CopyTo(table, 36); // SLS version
        BitConverter.GetBytes(1u).CopyTo(table, 44); // data type
        BitConverter.GetBytes(declaredLength ?? (uint)data.Length).CopyTo(table, 52);
        data.CopyTo(table, 56);
        return table;
    }

    [Fact]
    public void ParseMsdmKey_ReadsTheKeyAtTheEndOfTheTable()
    {
        Assert.Equal(Key, WindowsSpecsParsers.ParseMsdmKey(BuildMsdm(Key)));
    }

    [Fact]
    public void ParseMsdmKey_DeclaredLengthPastTheBuffer_IsClamped()
    {
        Assert.Equal(Key, WindowsSpecsParsers.ParseMsdmKey(BuildMsdm(Key, declaredLength: 400)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void ParseMsdmKey_MissingOrShortTable_ReturnsEmpty(byte[]? table)
    {
        Assert.Equal("", WindowsSpecsParsers.ParseMsdmKey(table));
    }

    [Fact]
    public void ParseMsdmKey_WrongSignature_ReturnsEmpty()
    {
        var table = BuildMsdm(Key);
        table[0] = (byte)'X';
        Assert.Equal("", WindowsSpecsParsers.ParseMsdmKey(table));
    }

    [Theory]
    [InlineData("Windows 10 Pro", 19045, "Windows 10 Pro")]
    [InlineData("Windows 10 Pro", 22631, "Windows 11 Pro")]
    [InlineData("Windows 10 Home", 22000, "Windows 11 Home")]
    [InlineData("Windows 10 Enterprise N", 26100, "Windows 11 Enterprise N")]
    [InlineData("Windows Server 2022 Datacenter", 20348, "Windows Server 2022 Datacenter")]
    [InlineData("Windows 11 Pro", 26100, "Windows 11 Pro")]
    [InlineData("", 22631, "")]
    public void ComposeOsCaption_MapsTheBuildForwardOnWindows11(string productName, int build, string expected)
    {
        Assert.Equal(expected, WindowsSpecsParsers.ComposeOsCaption(productName, build));
    }

    [Theory]
    [InlineData(1_000_000_000UL, "1 Gbps")]
    [InlineData(2_500_000_000UL, "2.5 Gbps")]
    [InlineData(10_000_000_000UL, "10 Gbps")]
    [InlineData(100_000_000UL, "100 Mbps")]
    [InlineData(866_700_000UL, "866.7 Mbps")]
    [InlineData(54_000_000UL, "54 Mbps")]
    [InlineData(64_000UL, "64 Kbps")]
    [InlineData(0UL, "0 bps")]
    public void FormatLinkSpeed_MatchesNetAdapterStrings(ulong bps, string expected)
    {
        Assert.Equal(expected, WindowsSpecsParsers.FormatLinkSpeed(bps));
    }

    private static byte[] BuildStorageDescriptor(string vendor, string product, uint busType, bool removable)
    {
        const int headerSize = 36;
        var vendorBytes = Encoding.ASCII.GetBytes(vendor + "\0");
        var productBytes = Encoding.ASCII.GetBytes(product + "\0");
        var buffer = new byte[headerSize + vendorBytes.Length + productBytes.Length];
        buffer[10] = (byte)(removable ? 1 : 0);
        var vendorOffset = vendor.Length == 0 ? 0u : (uint)headerSize;
        var productOffset = (uint)(headerSize + vendorBytes.Length);
        BitConverter.GetBytes(vendorOffset).CopyTo(buffer, 12);
        BitConverter.GetBytes(productOffset).CopyTo(buffer, 16);
        BitConverter.GetBytes(busType).CopyTo(buffer, 28);
        vendorBytes.CopyTo(buffer, headerSize);
        productBytes.CopyTo(buffer, headerSize + vendorBytes.Length);
        return buffer;
    }

    [Fact]
    public void ParseStorageDescriptor_Nvme_ReadsProductAndBus()
    {
        var d = WindowsSpecsParsers.ParseStorageDescriptor(BuildStorageDescriptor("", "Samsung SSD 980 PRO 1TB   ", 17, false));

        Assert.NotNull(d);
        Assert.Equal("Samsung SSD 980 PRO 1TB", d.Value.Product);
        Assert.Equal(17u, d.Value.BusType);
        Assert.False(d.Value.Removable);
        Assert.Equal("NVMe", WindowsSpecsParsers.BusTypeName(d.Value.BusType));
    }

    [Fact]
    public void ParseStorageDescriptor_UsbRemovable_IsFlagged()
    {
        var d = WindowsSpecsParsers.ParseStorageDescriptor(BuildStorageDescriptor("SanDisk", "Ultra", 7, true));

        Assert.NotNull(d);
        Assert.True(d.Value.Removable);
        Assert.Equal(7u, d.Value.BusType);
    }

    [Fact]
    public void ParseStorageDescriptor_TruncatedBuffer_ReturnsNull()
    {
        Assert.Null(WindowsSpecsParsers.ParseStorageDescriptor(new byte[8]));
        Assert.Null(WindowsSpecsParsers.ParseStorageDescriptor(null));
    }

    [Theory]
    [InlineData("ATA", "Samsung SSD 860 EVO 500GB", "Samsung SSD 860 EVO 500GB")]
    [InlineData("NVMe", "WD_BLACK SN850X 2000GB", "WD_BLACK SN850X 2000GB")]
    [InlineData("", "KINGSTON SA400S37", "KINGSTON SA400S37")]
    [InlineData("Msft", "Virtual Disk", "Msft Virtual Disk")]
    [InlineData("WDC", "WDC WD10EZEX", "WDC WD10EZEX")]
    public void DiskFriendlyName_DropsTransportVendorTags(string vendor, string product, string expected)
    {
        var d = new WindowsSpecsParsers.StorageDescriptor(vendor, product, 0, false);
        Assert.Equal(expected, WindowsSpecsParsers.DiskFriendlyName(d));
    }

    private static byte[] BuildEdid(string? name, string manufacturerId = "DEL")
    {
        var edid = new byte[128];
        var word = ((manufacturerId[0] - 'A' + 1) << 10) | ((manufacturerId[1] - 'A' + 1) << 5) | (manufacturerId[2] - 'A' + 1);
        edid[8] = (byte)(word >> 8);
        edid[9] = (byte)(word & 0xFF);
        // Descriptor 0 is a timing block, descriptor 1 holds the name.
        edid[54] = 0x01;
        if (name is not null)
        {
            var o = 54 + 18;
            edid[o + 3] = 0xFC;
            var text = Encoding.ASCII.GetBytes(name + "\n");
            Array.Fill(edid, (byte)0x20, o + 5, 13);
            Array.Copy(text, 0, edid, o + 5, Math.Min(text.Length, 13));
        }
        return edid;
    }

    [Fact]
    public void EdidMonitorName_ReadsTheNameDescriptor()
    {
        Assert.Equal("DELL U2415", WindowsSpecsParsers.EdidMonitorName(BuildEdid("DELL U2415")));
    }

    [Fact]
    public void EdidMonitorName_NoNameDescriptor_ReturnsEmpty()
    {
        Assert.Equal("", WindowsSpecsParsers.EdidMonitorName(BuildEdid(null)));
        Assert.Equal("", WindowsSpecsParsers.EdidMonitorName(new byte[16]));
        Assert.Equal("", WindowsSpecsParsers.EdidMonitorName(null));
    }

    [Fact]
    public void EdidManufacturer_DecodesTheThreeLetterId()
    {
        Assert.Equal("DEL", WindowsSpecsParsers.EdidManufacturer(BuildEdid("X", "DEL")));
        Assert.Equal("GSM", WindowsSpecsParsers.EdidManufacturer(BuildEdid("X", "GSM")));
    }

    [Fact]
    public void PickUsbInstanceId_MatchesTheSerialSuffixOnly()
    {
        var ids = new[]
        {
            @"USB\ROOT_HUB30\4&2a1b",
            @"USB\VID_0E8D&PID_2048\ABC123X",
            @"USB\VID_0E8D&PID_2051\abc123",
        };

        Assert.Equal(@"USB\VID_0E8D&PID_2051\abc123", WindowsSpecsParsers.PickUsbInstanceId(ids, "ABC123"));
        Assert.Null(WindowsSpecsParsers.PickUsbInstanceId(ids, "C123"));
        Assert.Null(WindowsSpecsParsers.PickUsbInstanceId(new[] { @"USB\ABC123" }, "ABC123"));
    }

    [Fact]
    public void SplitMultiSz_StopsAtTheDoubleTerminator()
    {
        var buffer = "USB\\A\\1\0USB\\B\\2\0\0junk".ToCharArray();

        Assert.Equal(new[] { "USB\\A\\1", "USB\\B\\2" }, WindowsSpecsParsers.SplitMultiSz(buffer));
        Assert.Empty(WindowsSpecsParsers.SplitMultiSz(new char[] { '\0', '\0' }));
    }
}
