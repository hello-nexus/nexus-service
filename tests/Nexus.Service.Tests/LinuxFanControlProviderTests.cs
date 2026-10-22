using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Drives LinuxFanControlProvider against a fake hwmon tree: enumeration,
/// duty↔pwm scaling, mode, the set/release sysfs writes, temps, and the
/// calibration ramp - all without real hardware (1ms settle).
/// </summary>
public class LinuxFanControlProviderTests
{
    private static LinuxFanControlProvider WithControllableFan(TempDir t, int pwm, int enable, int rpm)
    {
        t.Write("hwmon/hwmon0/name", "nct6779\n");
        t.Write("hwmon/hwmon0/pwm2", pwm + "\n");
        t.Write("hwmon/hwmon0/pwm2_enable", enable + "\n");
        t.Write("hwmon/hwmon0/fan2_input", rpm + "\n");
        t.Write("hwmon/hwmon0/fan2_label", "CPU Fan\n");
        return new LinuxFanControlProvider(t.At("hwmon"), TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void GetFanChannels_ReportsDutyRpmMode()
    {
        using var t = new TempDir();
        var p = WithControllableFan(t, pwm: 128, enable: 1, rpm: 900);
        var ch = Assert.Single(p.GetFanChannels());
        Assert.Equal("linux-fan-nct6779-2", ch.Id);
        Assert.Equal("nct6779 CPU Fan", ch.Name);
        Assert.Equal(50, ch.DutyPercent); // 128/255 ≈ 50%
        Assert.Equal(900, ch.Rpm);
        Assert.Equal(FanModes.Manual, ch.Mode); // enable=1
    }

    [Theory]
    [InlineData("kraken2024elite", 0)]
    [InlineData("z53", 1)]
    [InlineData(null, 1)]
    public void NativeKrakenClaimsChip_HidesOnlyThatChipFanAndTemp(string? hidden, int expected)
    {
        using var t = new TempDir();
        t.Write("hwmon/hwmon0/name", "kraken2024elite\n");
        t.Write("hwmon/hwmon0/pwm1", "128\n");
        t.Write("hwmon/hwmon0/pwm1_enable", "1\n");
        t.Write("hwmon/hwmon0/temp1_input", "30000\n");
        var p = new LinuxFanControlProvider(t.At("hwmon"), TimeSpan.FromMilliseconds(1), nativeHwmon: () => hidden);
        Assert.Equal(expected, p.GetFanChannels().Count);
        Assert.Equal(expected, p.GetTemperatureSources().Count);
    }

    [Theory]
    [InlineData(0x2007, "x53")]
    [InlineData(0x2014, "x53")]
    [InlineData(0x3008, "z53")]
    [InlineData(0x300E, "kraken2023")]
    [InlineData(0x300C, "kraken2023elite")]
    [InlineData(0x3012, "kraken2024elite")]
    [InlineData(0x3014, null)]
    public void KernelHwmonName_FollowsTheKernelDriverPerProductId(int productId, string? expected)
        => Assert.Equal(expected, Nexus.Service.Peripherals.Nzxt.KrakenModel.KernelHwmonName(productId));

    [Fact]
    public void KrakenHubClaim_SurvivesDetachAndClearsOnRelease()
    {
        var hub = new Nexus.Service.Peripherals.Nzxt.KrakenHub(null);
        Assert.Null(hub.ClaimedHidPath);
        hub.Claim("/dev/hidraw12", Nexus.Service.Peripherals.Nzxt.KrakenModel.Find(0x3012)!);
        hub.Detach();
        Assert.Equal("/dev/hidraw12", hub.ClaimedHidPath);
        Assert.Equal("kraken2024elite", hub.ClaimedHwmonName);
        hub.ReleaseClaim();
        Assert.Null(hub.ClaimedHidPath);
        Assert.Null(hub.ClaimedHwmonName);
    }

    [Fact]
    public void GetFanChannels_SkipsFanWithoutEnableKnob()
    {
        using var t = new TempDir();
        t.Write("hwmon/hwmon0/name", "it87\n");
        t.Write("hwmon/hwmon0/pwm1", "200\n"); // no pwm1_enable -> not controllable
        var p = new LinuxFanControlProvider(t.At("hwmon"), TimeSpan.FromMilliseconds(1));
        Assert.Empty(p.GetFanChannels());
    }

    [Fact]
    public void SetFanSpeed_WritesPwmAndManualEnable()
    {
        using var t = new TempDir();
        var p = WithControllableFan(t, pwm: 0, enable: 2, rpm: 0);
        Assert.Equal(75, p.SetFanSpeed("linux-fan-nct6779-2", 75));
        Assert.Equal("1", File.ReadAllText(t.At("hwmon/hwmon0/pwm2_enable")));
        Assert.Equal("191", File.ReadAllText(t.At("hwmon/hwmon0/pwm2"))); // 75% of 255
    }

    [Fact]
    public void ReleaseFan_RestoresAutomatic()
    {
        using var t = new TempDir();
        var p = WithControllableFan(t, pwm: 191, enable: 1, rpm: 0);
        p.GetFanChannels();
        p.ReleaseFan("linux-fan-nct6779-2");
        Assert.Equal("2", File.ReadAllText(t.At("hwmon/hwmon0/pwm2_enable")));
    }

    [Fact]
    public void GetTemperatureSources_ReadsHwmonTempsByCategory()
    {
        using var t = new TempDir();
        t.Write("hwmon/hwmon0/name", "k10temp\n");
        t.Write("hwmon/hwmon0/temp1_input", "45000\n");
        t.Write("hwmon/hwmon0/temp1_label", "Tctl\n");
        var p = new LinuxFanControlProvider(t.At("hwmon"), TimeSpan.FromMilliseconds(1));
        var src = Assert.Single(p.GetTemperatureSources());
        Assert.Equal("CPU", src.Category);
        Assert.Equal("k10temp Tctl", src.Name);
        Assert.Equal(45f, src.Value);
        Assert.Equal(45f, p.ReadTemperature(src.Id));
    }

    [Fact]
    public void GetTemperatureSources_DropsUnconnectedSentinelReadings()
    {
        using var t = new TempDir();
        // it8696 temp6 on an unconnected header reports -55000 m°C; temp7 a
        // disabled channel reads 0; an unpopulated SPD-style channel reads
        // ~250 m°C (0.25°C). None must surface as a curve source - otherwise a
        // new/preset curve defaults to a fake input.
        t.Write("hwmon/hwmon0/name", "it8696\n");
        t.Write("hwmon/hwmon0/temp1_input", "43000\n");
        t.Write("hwmon/hwmon0/temp6_input", "-55000\n");
        t.Write("hwmon/hwmon0/temp7_input", "0\n");
        t.Write("hwmon/hwmon0/temp8_input", "250\n");
        var p = new LinuxFanControlProvider(t.At("hwmon"), TimeSpan.FromMilliseconds(1));
        var src = Assert.Single(p.GetTemperatureSources()); // only the real 43°C survives
        Assert.Equal(43f, src.Value);
    }

    [Fact]
    public async Task CalibrateAsync_RampsAllStepsAndRestores()
    {
        using var t = new TempDir();
        var p = WithControllableFan(t, pwm: 255, enable: 2, rpm: 1200);
        var cal = Assert.Single(await p.CalibrateAsync(Array.Empty<string>(), null!, CancellationToken.None));
        Assert.Equal(11, cal.Curve.Count); // 100..0 in 11 steps
        Assert.Equal(1200, cal.MaxRpm);
        Assert.Equal("2", File.ReadAllText(t.At("hwmon/hwmon0/pwm2_enable"))); // restored
    }

    [Fact]
    public async Task Calibrate_UnconnectedHeader_ClassifiedUnresponsiveAndMerged()
    {
        using var t = new TempDir();
        // A header with pwm but a fan that never spins (0 RPM at every duty) =
        // nothing plugged in. Calibration must mark it so the UI can hide it.
        t.Write("hwmon/hwmon0/name", "it8696\n");
        t.Write("hwmon/hwmon0/pwm3", "60\n");
        t.Write("hwmon/hwmon0/pwm3_enable", "2\n");
        t.Write("hwmon/hwmon0/fan3_input", "0\n");
        var store = new InMemoryConfigStore();
        var p = new LinuxFanControlProvider(t.At("hwmon"), TimeSpan.FromMilliseconds(1), store);

        await p.CalibrateAsync(Array.Empty<string>(), null!, CancellationToken.None);

        // persisted with the shared classification
        var cal = store.Load().Cooling.FanCalibrations["linux-fan-it8696-3"];
        Assert.Equal("Unresponsive", cal.Classification);
        Assert.Equal(0, cal.MaxRpm);
        // and merged back into the live channel so the UI sees it
        var ch = Assert.Single(p.GetFanChannels());
        Assert.Equal("Unresponsive", ch.Classification);
    }
}
