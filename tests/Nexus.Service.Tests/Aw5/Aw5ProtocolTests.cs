using Nexus.Service.Peripherals.Aw5;
using Xunit;

namespace Nexus.Service.Tests.Aw5;

/// <summary>
/// Pins the bench-decoded wire formats (2026-07-15, both coolers on the Y70, read off
/// the glass). The two variants share nothing, and neither encoding is guessable from
/// the other: Levelplay is a digit map over feature reports, CoolerMaster is plain
/// binary over interrupt OUT. A wrong byte here renders a plausible-but-wrong number,
/// which no test but this one would catch.
/// </summary>
public class Aw5ProtocolTests
{
    // ---- Levelplay ----

    [Fact]
    public void Levelplay_cycle_is_five_feature_reports_tagged_by_sub_command()
    {
        var frames = Aw5Protocol.BuildLevelplayCycle(tempC: 28, loadPct: 0, mhz: 2000);

        Assert.Equal(5, frames.Length);
        for (var i = 0; i < frames.Length; i++)
        {
            Assert.Equal(64, frames[i].Length);
            Assert.Equal(0x07, frames[i][0]);
            Assert.Equal((byte)i, frames[i][1]);
        }
    }

    [Fact]
    public void Levelplay_renders_one_digit_per_byte_in_the_low_nibble()
    {
        // The panel ignores each byte's high nibble: bench, b3 = 0x12 / 0x52 / 0x92 all
        // render "2". Packing two digits per byte (BCD) renders only half the number,
        // so every emitted byte must be a bare 0-9.
        var frames = Aw5Protocol.BuildLevelplayCycle(tempC: 45, loadPct: 0, mhz: 6789);

        Assert.Equal(4, frames[0][3]);
        Assert.Equal(5, frames[0][4]);
        Assert.Equal(6, frames[3][2]);
        Assert.Equal(7, frames[3][3]);
        Assert.Equal(8, frames[3][4]);
        Assert.Equal(9, frames[3][5]);
    }

    [Fact]
    public void Levelplay_temp_matches_the_captured_vendor_frame()
    {
        // Vendor sent 07 00 00 42 68 .. and the glass read 28C: low nibbles 2 and 8.
        var frames = Aw5Protocol.BuildLevelplayCycle(tempC: 28, loadPct: 0, mhz: 0);

        Assert.Equal(2, frames[0][3] & 0x0F);
        Assert.Equal(8, frames[0][4] & 0x0F);
    }

    [Fact]
    public void Levelplay_clock_matches_the_captured_vendor_frame()
    {
        // Vendor sent 07 03 02 50 10 00 .. and the glass read 2000: low nibbles 2,0,0,0.
        var frames = Aw5Protocol.BuildLevelplayCycle(tempC: 0, loadPct: 0, mhz: 2000);

        Assert.Equal(2, frames[3][2] & 0x0F);
        Assert.Equal(0, frames[3][3] & 0x0F);
        Assert.Equal(0, frames[3][4] & 0x0F);
        Assert.Equal(0, frames[3][5] & 0x0F);
    }

    [Fact]
    public void Levelplay_load_percent_rides_sub_01()
    {
        // Bench: the field left of the rpm reads "N%", not the "Nx" fan count it looks
        // like. Pinned by a stuck reading on hardware - a constant here renders a load
        // that never moves while the CPU is plainly busy.
        var frames = Aw5Protocol.BuildLevelplayCycle(tempC: 0, loadPct: 42, mhz: 0);

        Assert.Equal(4, frames[1][3]);
        Assert.Equal(2, frames[1][4]);
    }

    [Fact]
    public void Levelplay_load_matches_the_captured_vendor_frame()
    {
        // Vendor sent 07 01 00 00 96 .. at idle and the glass read 6%: low nibbles 0,6.
        var frames = Aw5Protocol.BuildLevelplayCycle(tempC: 0, loadPct: 6, mhz: 0);

        Assert.Equal(0, frames[1][3] & 0x0F);
        Assert.Equal(6, frames[1][4] & 0x0F);
    }

    [Fact]
    public void Levelplay_full_load_renders_as_99_not_00()
    {
        // Two digits only: 100 must clamp, not wrap to a panel reading 0% at full tilt.
        var frames = Aw5Protocol.BuildLevelplayCycle(tempC: 0, loadPct: 100, mhz: 0);

        Assert.Equal(9, frames[1][3]);
        Assert.Equal(9, frames[1][4]);
    }

    [Fact]
    public void Levelplay_sub_04_is_the_invariant_commit_frame()
    {
        // Byte-identical in all 36 captured cycles regardless of every other field.
        var idle = Aw5Protocol.BuildLevelplayCycle(0, 0, 0);
        var busy = Aw5Protocol.BuildLevelplayCycle(99, 100, 9999);

        Assert.Equal(idle[4], busy[4]);
        Assert.Equal(0x09, idle[4][3]);
        Assert.Equal(0x09, idle[4][4]);
        Assert.Equal(0x01, idle[4][6]);
    }

    [Theory]
    [InlineData(-5, 0, 0)]
    [InlineData(0, 0, 0)]
    [InlineData(7, 0, 7)]
    [InlineData(99, 9, 9)]
    [InlineData(140, 9, 9)]
    public void Levelplay_temp_clamps_rather_than_wraps(int tempC, int expectTens, int expectOnes)
    {
        // A wrapped overflow would render a plausible small number: 140C as "40" reads
        // like a healthy loop. Clamping renders 99, which reads as wrong.
        var frames = Aw5Protocol.BuildLevelplayCycle(tempC, 0, 0);

        Assert.Equal(expectTens, frames[0][3]);
        Assert.Equal(expectOnes, frames[0][4]);
    }

    [Fact]
    public void Levelplay_clock_is_not_a_fan_reading()
    {
        // The field sits under a fan icon but carries the CPU clock: the vendor's own
        // value steps up to 4200 between consecutive 1.1s cycles and peaks at the
        // part's boost ceiling. Feeding it fan rpm renders a plausible, near-constant
        // number - which is exactly how this shipped wrong once.
        var idle = Aw5Protocol.BuildLevelplayCycle(0, 0, mhz: 800);
        var boost = Aw5Protocol.BuildLevelplayCycle(0, 0, mhz: 4700);

        Assert.Equal(new byte[] { 0, 8, 0, 0 }, idle[3][2..6]);
        Assert.Equal(new byte[] { 4, 7, 0, 0 }, boost[3][2..6]);
    }

    [Fact]
    public void Levelplay_clock_clamps_at_four_digits()
    {
        var frames = Aw5Protocol.BuildLevelplayCycle(0, 0, mhz: 12345);

        Assert.Equal(9, frames[3][2]);
        Assert.Equal(9, frames[3][3]);
        Assert.Equal(9, frames[3][4]);
        Assert.Equal(9, frames[3][5]);
    }

    [Fact]
    public void Levelplay_load_rides_the_byte_the_vendor_uses_even_though_nothing_renders_it()
    {
        // Bench: b5 = 0x00 / 0x50 / 0x90 are indistinguishable on the glass. Sent as
        // tens-in-high-nibble anyway so the stream matches the vendor's.
        var frames = Aw5Protocol.BuildLevelplayCycle(0, loadPct: 70, mhz: 0);

        Assert.Equal(0x70, frames[0][5]);
    }

    // ---- CoolerMaster ----

    [Fact]
    public void CoolerMaster_frame_carries_plain_binary_not_a_digit_map()
    {
        // Bench: b5 = 0x37 rendered "55". This variant is whole bytes, not nibbles.
        var f = Aw5Protocol.BuildCoolerMasterFrame(loadPct: 25, mhz: 3600, tempC: 55);

        Assert.Equal(64, f.Length);
        Assert.Equal(0x10, f[0]);
        Assert.Equal(0x08, f[1]);
        Assert.Equal(25, f[2]);
        Assert.Equal(55, f[5]);
        Assert.Equal(0xF9, f[12]);
    }

    [Fact]
    public void CoolerMaster_clock_is_big_endian()
    {
        // 3600 renders only as b3<<8|b4; little-endian would render 4110.
        var f = Aw5Protocol.BuildCoolerMasterFrame(0, mhz: 3600, tempC: 0);

        Assert.Equal(0x0E, f[3]);
        Assert.Equal(0x10, f[4]);
    }

    [Fact]
    public void CoolerMaster_bytes_6_to_8_stay_zero()
    {
        // Zero in every captured vendor frame; purpose unknown, so nothing is put there.
        var f = Aw5Protocol.BuildCoolerMasterFrame(99, 5000, 90);

        Assert.Equal(0, f[6]);
        Assert.Equal(0, f[7]);
        Assert.Equal(0, f[8]);
    }

    [Fact]
    public void CoolerMaster_blank_frame_is_the_report_id_alone()
    {
        var f = Aw5Protocol.BuildCoolerMasterBlankFrame();

        Assert.Equal(0x10, f[0]);
        for (var i = 1; i < f.Length; i++) Assert.Equal(0, f[i]);
    }

    [Theory]
    [InlineData(0, 0, 100, 0)]
    [InlineData(50, 0, 100, 3)]
    [InlineData(100, 0, 100, 6)]
    [InlineData(-20, 0, 100, 0)]
    [InlineData(400, 0, 100, 6)]
    public void Notches_scale_and_clamp_to_the_panels_six_bars(int value, int min, int max, int expected)
    {
        // Bench: a 7 renders as a full bar, so the panel clamps too; overshooting it
        // is silently indistinguishable from a correct 6.
        Assert.Equal(expected, Aw5Protocol.Notches(value, min, max));
    }

    [Theory]
    [InlineData(0, 0)]      // no reading at all: the only case that empties the bar
    [InlineData(1, 1)]
    [InlineData(15, 1)]
    [InlineData(50, 3)]
    [InlineData(100, 6)]
    public void Notches_keep_one_segment_lit_for_any_non_zero_reading(int loadPct, int expected)
    {
        // A running CPU idling under the first step must not render an empty bar:
        // empty has to mean "no reading", not "low reading".
        Assert.Equal(expected, Aw5Protocol.Notches(loadPct, 0, 100));
    }

    [Fact]
    public void Notches_stay_linear_and_shared_across_all_three_readings()
    {
        // One scale, three ranges: the same helper drives temp, clock and load so a
        // change to the curve cannot drift between them.
        Assert.Equal(1, Aw5Protocol.Notches(20, 20, 90));    // temp floor
        Assert.Equal(6, Aw5Protocol.Notches(90, 20, 90));    // temp ceiling
        Assert.Equal(1, Aw5Protocol.Notches(800, 800, 5000));
        Assert.Equal(6, Aw5Protocol.Notches(5000, 800, 5000));
        Assert.Equal(3, Aw5Protocol.Notches(50, 0, 100));    // midpoint
    }

    [Fact]
    public void CoolerMaster_notches_never_exceed_the_panel_range()
    {
        var f = Aw5Protocol.BuildCoolerMasterFrame(loadPct: 100, mhz: 9999, tempC: 255);

        Assert.InRange(f[9], 0, Aw5Protocol.CoolerMasterMaxNotches);
        Assert.InRange(f[10], 0, Aw5Protocol.CoolerMasterMaxNotches);
        Assert.InRange(f[11], 0, Aw5Protocol.CoolerMasterMaxNotches);
    }
}
