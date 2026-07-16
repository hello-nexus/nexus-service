using System.Linq;
using Nexus.Service.Models.Displays;
using Nexus.Service.Peripherals.Corsair.XeneonEdge;
using Xunit;

namespace Nexus.Service.Tests.Corsair;

/// <summary>
/// Golden-vector coverage for the Xeneon Edge orientation sensor protocol,
/// captured from a live USBPcap trace (T1 bench, 2026-07-15). Pure functions,
/// no hardware.
/// </summary>
public class XeneonEdgeProtocolTests
{
    private static byte[] OrientationReport(byte code, int length = 64)
    {
        var buf = new byte[length];
        buf[0] = 0x01; // report id
        buf[1] = 0x11; // msgid
        buf[5] = 0x02; // payload length
        buf[6] = 0x00; // payload[0], always 0
        buf[7] = code; // payload[1]
        return buf;
    }

    [Fact]
    public void BuildOrientationQuery_Is64BytesReportId01Msgid11ZeroPadded()
    {
        var query = XeneonEdgeProtocol.BuildOrientationQuery();

        Assert.Equal(64, query.Length);
        Assert.Equal(0x01, query[0]);
        Assert.Equal(0x11, query[1]);
        Assert.All(query.Skip(2), b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x01)]
    [InlineData((byte)0x02)]
    [InlineData((byte)0x03)]
    public void TryParseOrientationReport_ValidReport_ParsesCode(byte code)
    {
        var report = OrientationReport(code);

        var ok = XeneonEdgeProtocol.TryParseOrientationReport(report, out var parsed);

        Assert.True(ok);
        Assert.Equal(code, parsed);
    }

    [Fact]
    public void TryParseOrientationReport_WrongReportId_ReturnsFalse()
    {
        var report = OrientationReport(0x01);
        report[0] = 0x02;

        Assert.False(XeneonEdgeProtocol.TryParseOrientationReport(report, out _));
    }

    [Fact]
    public void TryParseOrientationReport_WrongMsgId_ReturnsFalse()
    {
        // A different message (e.g. the 0x09 firmware-version response) must
        // never be mistaken for an orientation report.
        var report = OrientationReport(0x01);
        report[1] = 0x09;

        Assert.False(XeneonEdgeProtocol.TryParseOrientationReport(report, out _));
    }

    [Fact]
    public void TryParseOrientationReport_WrongPayloadLength_ReturnsFalse()
    {
        var report = OrientationReport(0x01);
        report[5] = 0x0a; // e.g. the firmware-version response's LEN

        Assert.False(XeneonEdgeProtocol.TryParseOrientationReport(report, out _));
    }

    [Fact]
    public void TryParseOrientationReport_ShortBuffer_ReturnsFalse()
    {
        var report = new byte[] { 0x01, 0x11, 0x00, 0x00, 0x00, 0x02, 0x00 }; // missing the code byte

        Assert.False(XeneonEdgeProtocol.TryParseOrientationReport(report, out _));
    }

    [Fact]
    public void TryParseOrientationReport_IdleAllZeroRead_ReturnsFalse()
    {
        Assert.False(XeneonEdgeProtocol.TryParseOrientationReport(new byte[64], out _));
    }

    [Fact]
    public void OrientationBySensorCode_HasFourEntries_MatchingTheBenchMeasuredTable()
    {
        // The sensor code is the DMDO value. Codes 1 and 2 are measured on hardware.
        Assert.Equal(4, XeneonEdgeProtocol.OrientationBySensorCode.Length);
        Assert.Equal(DisplayOrientations.Landscape, XeneonEdgeProtocol.OrientationBySensorCode[0]);
        Assert.Equal(DisplayOrientations.Portrait, XeneonEdgeProtocol.OrientationBySensorCode[1]);
        Assert.Equal(DisplayOrientations.LandscapeFlipped, XeneonEdgeProtocol.OrientationBySensorCode[2]);
        Assert.Equal(DisplayOrientations.PortraitFlipped, XeneonEdgeProtocol.OrientationBySensorCode[3]);
    }

    [Theory]
    [InlineData((byte)0, DisplayOrientations.Landscape)]
    [InlineData((byte)1, DisplayOrientations.Portrait)]
    [InlineData((byte)2, DisplayOrientations.LandscapeFlipped)]
    [InlineData((byte)3, DisplayOrientations.PortraitFlipped)]
    public void ResolveOrientation_KnownCode_MatchesTable(byte code, string expected)
    {
        Assert.Equal(expected, XeneonEdgeProtocol.ResolveOrientation(code));
    }

    [Fact]
    public void ResolveOrientation_OutOfRangeCode_ReturnsNull()
    {
        Assert.Null(XeneonEdgeProtocol.ResolveOrientation(4));
    }

    private static byte[] SettingsBlockReport(int brightness, int backlight, int contrast, int red, int green, int blue)
    {
        var buf = new byte[64];
        buf[0] = 0x01;
        buf[1] = 0x0e;
        buf[5] = 0x1d;
        buf[6] = (byte)brightness;
        buf[7] = (byte)backlight;
        buf[8] = (byte)contrast;
        buf[9] = (byte)red;
        buf[10] = (byte)green;
        buf[11] = (byte)blue;
        return buf;
    }

    private static byte[] SetAckReport(byte group, byte ok, byte value)
    {
        var buf = new byte[64];
        buf[0] = 0x01;
        buf[1] = 0x0f;
        buf[2] = 0x0f;
        buf[5] = 0x02;
        buf[6] = group;
        buf[7] = ok;
        buf[8] = value;
        return buf;
    }

    [Fact]
    public void BuildSettingsQuery_Is64BytesReportId01Msgid0eZeroPadded()
    {
        var query = XeneonEdgeProtocol.BuildSettingsQuery();

        Assert.Equal(64, query.Length);
        Assert.Equal(0x01, query[0]);
        Assert.Equal(0x0e, query[1]);
        Assert.All(query.Skip(2), b => Assert.Equal(0, b));
    }

    [Fact]
    public void BuildSetCommand_PlacesGroupItemValueAtFixedBytesNotLengthFramed()
    {
        var cmd = XeneonEdgeProtocol.BuildSetCommand(0x02, 0x02, 0x32);

        Assert.Equal(64, cmd.Length);
        Assert.Equal(0x01, cmd[0]);
        Assert.Equal(0x0f, cmd[1]);
        // byte5 stays 0x00 on a 0x0f write: this msgid is not LEN-framed.
        Assert.Equal(0x00, cmd[5]);
        Assert.Equal(0x02, cmd[6]);
        Assert.Equal(0x02, cmd[7]);
        Assert.Equal(0x32, cmd[8]);
    }

    [Fact]
    public void TryParseSettingsBlock_ValidReport_ParsesAllSixControlsInBlockOrder()
    {
        var report = SettingsBlockReport(brightness: 50, backlight: 100, contrast: 50, red: 151, green: 127, blue: 139);

        var ok = XeneonEdgeProtocol.TryParseSettingsBlock(report, out var block);

        Assert.True(ok);
        Assert.Equal(50, block.Brightness);
        Assert.Equal(100, block.Backlight);
        Assert.Equal(50, block.Contrast);
        Assert.Equal(151, block.Red);
        Assert.Equal(127, block.Green);
        Assert.Equal(139, block.Blue);
    }

    [Fact]
    public void TryParseSettingsBlock_WrongMsgId_ReturnsFalse()
    {
        var report = SettingsBlockReport(50, 100, 50, 151, 127, 139);
        report[1] = 0x11;

        Assert.False(XeneonEdgeProtocol.TryParseSettingsBlock(report, out _));
    }

    [Fact]
    public void TryParseSettingsBlock_WrongLength_ReturnsFalse()
    {
        var report = SettingsBlockReport(50, 100, 50, 151, 127, 139);
        report[5] = 0x02; // e.g. the orientation report's LEN

        Assert.False(XeneonEdgeProtocol.TryParseSettingsBlock(report, out _));
    }

    [Fact]
    public void TryParseSettingsBlock_ShortBuffer_ReturnsFalse()
    {
        Assert.False(XeneonEdgeProtocol.TryParseSettingsBlock(new byte[10], out _));
    }

    [Fact]
    public void TryParseSetAck_Ok_ParsesGroupAndValue()
    {
        var report = SetAckReport(group: 0x02, ok: 0x01, value: 0x32);

        var ok = XeneonEdgeProtocol.TryParseSetAck(report, out var group, out var value);

        Assert.True(ok);
        Assert.Equal(0x02, group);
        Assert.Equal(0x32, value);
    }

    [Fact]
    public void TryParseSetAck_NotOk_ReturnsFalse()
    {
        var report = SetAckReport(group: 0x02, ok: 0x00, value: 0x32);

        Assert.False(XeneonEdgeProtocol.TryParseSetAck(report, out _, out _));
    }

    [Fact]
    public void TryParseSetAck_WrongMsgId_ReturnsFalse()
    {
        var report = SetAckReport(group: 0x02, ok: 0x01, value: 0x32);
        report[1] = 0x0e;

        Assert.False(XeneonEdgeProtocol.TryParseSetAck(report, out _, out _));
    }

    [Fact]
    public void TryParseSetAck_ShortBuffer_ReturnsFalse()
    {
        Assert.False(XeneonEdgeProtocol.TryParseSetAck(new byte[8], out _, out _));
    }

    [Theory]
    [InlineData(XeneonEdgeControl.Backlight, 0x02, 0x00, 1, 0, 100)]
    [InlineData(XeneonEdgeControl.Contrast, 0x02, 0x01, 2, 0, 100)]
    [InlineData(XeneonEdgeControl.Brightness, 0x02, 0x02, 0, 0, 100)]
    [InlineData(XeneonEdgeControl.Red, 0x03, 0x01, 3, 0, 255)]
    [InlineData(XeneonEdgeControl.Green, 0x03, 0x02, 4, 0, 255)]
    [InlineData(XeneonEdgeControl.Blue, 0x03, 0x03, 5, 0, 255)]
    public void XeneonEdgeControls_Coords_MatchBenchMeasuredTable(
        XeneonEdgeControl control, byte group, byte item, int blockIndex, int min, int max)
    {
        var coords = XeneonEdgeControls.Coords[control];

        Assert.Equal(group, coords.Group);
        Assert.Equal(item, coords.Item);
        Assert.Equal(blockIndex, coords.BlockIndex);
        Assert.Equal(min, coords.Min);
        Assert.Equal(max, coords.Max);
    }

    [Fact]
    public void XeneonEdgeDefaults_MatchFactoryMeasuredValues()
    {
        Assert.Equal(50, XeneonEdgeDefaults.Brightness);
        Assert.Equal(100, XeneonEdgeDefaults.Backlight);
        Assert.Equal(50, XeneonEdgeDefaults.Contrast);
        Assert.Equal(151, XeneonEdgeDefaults.Red);
        Assert.Equal(127, XeneonEdgeDefaults.Green);
        Assert.Equal(139, XeneonEdgeDefaults.Blue);
    }
}
