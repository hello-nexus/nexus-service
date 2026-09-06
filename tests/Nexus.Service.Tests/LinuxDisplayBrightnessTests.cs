using System;
using System.IO;
using System.Text;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Covers the no-hardware parts of LinuxDisplayBrightnessProvider: the sysfs
/// backlight read/write (fake /sys/class/backlight tree) and the EDID decode.
/// The DDC/CI i2c transaction is hardware-only and not covered here.
/// </summary>
public class LinuxDisplayBrightnessTests
{
    [Fact]
    public void ReadBacklightPercent_ScalesByMax()
    {
        using var t = new TempDir();
        var dir = t.Dir("bl/intel");
        t.Write("bl/intel/max_brightness", "1000\n");
        t.Write("bl/intel/actual_brightness", "500\n");
        Assert.Equal(50, LinuxDisplayBrightnessProvider.ReadBacklightPercent(dir));
    }

    [Fact]
    public void ReadBacklightPercent_FallsBackToBrightnessFile()
    {
        using var t = new TempDir();
        var dir = t.Dir("bl/intel");
        t.Write("bl/intel/max_brightness", "100\n");
        t.Write("bl/intel/brightness", "20\n"); // no actual_brightness
        Assert.Equal(20, LinuxDisplayBrightnessProvider.ReadBacklightPercent(dir));
    }

    [Fact]
    public void WriteBacklightPercent_WritesScaledRaw()
    {
        using var t = new TempDir();
        var dir = t.Dir("bl/intel");
        t.Write("bl/intel/max_brightness", "1000\n");
        t.Write("bl/intel/brightness", "0\n");
        Assert.True(LinuxDisplayBrightnessProvider.WriteBacklightPercent(dir, 75));
        Assert.Equal("750", File.ReadAllText(Path.Combine(dir, "brightness")));
    }

    [Fact]
    public void WriteBacklightPercent_NoMaxFails()
    {
        using var t = new TempDir();
        Assert.False(LinuxDisplayBrightnessProvider.WriteBacklightPercent(t.Dir("bl/empty"), 50));
    }

    [Fact]
    public void DecodeEdid_ParsesManufacturerAndModel()
    {
        var edid = new byte[128];
        edid[0] = 0x00;
        edid[1] = 0xFF;
        // Manufacturer "DEL" -> PNP id 0x10AC (Dell).
        var id = ((4 & 0x1F) << 10) | ((5 & 0x1F) << 5) | (12 & 0x1F); // D,E,L
        edid[8] = (byte)(id >> 8);
        edid[9] = (byte)(id & 0xFF);
        // Model-name descriptor (tag 0xFC) at offset 54: 00 00 00 FC 00 + ASCII + 0x0A.
        edid[57] = 0xFC;
        var name = Encoding.ASCII.GetBytes("U2720Q");
        Array.Copy(name, 0, edid, 59, name.Length);
        edid[59 + name.Length] = 0x0A;

        var (mfg, model) = LinuxDisplayBrightnessProvider.DecodeEdid(edid);
        Assert.Equal("DEL", mfg);
        Assert.Equal("U2720Q", model);
    }

    [Fact]
    public void DecodeEdid_InvalidHeaderReturnsEmpty()
        => Assert.Equal(("", ""), LinuxDisplayBrightnessProvider.DecodeEdid(new byte[128]));

    /// <summary>
    /// The Y70 ships PNP id RTK0004 with model-name descriptor "HYTE Y70ti", so
    /// Y70DisplayProtocol.DdcPanelHardwareNames only matches via the PNP id.
    /// </summary>
    [Fact]
    public void DecodePnpId_ComposesManufacturerAndProductCode()
    {
        var edid = new byte[128];
        edid[0] = 0x00;
        edid[1] = 0xFF;
        var id = ((18 & 0x1F) << 10) | ((20 & 0x1F) << 5) | (11 & 0x1F); // R,T,K
        edid[8] = (byte)(id >> 8);
        edid[9] = (byte)(id & 0xFF);
        edid[10] = 0x04; // product code 0x0004, little-endian
        edid[11] = 0x00;
        edid[57] = 0xFC;
        var name = Encoding.ASCII.GetBytes("HYTE Y70ti");
        Array.Copy(name, 0, edid, 59, name.Length);
        edid[59 + name.Length] = 0x0A;

        Assert.Equal("RTK0004", LinuxDisplayBrightnessProvider.DecodePnpId(edid));
        Assert.Equal("HYTE Y70ti", LinuxDisplayBrightnessProvider.DecodeEdid(edid).Model);
    }

    [Fact]
    public void DecodePnpId_InvalidHeaderReturnsEmpty()
        => Assert.Equal("", LinuxDisplayBrightnessProvider.DecodePnpId(new byte[128]));
}
