using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Strimer;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Strimer;

public class StrimerFirmwareCommitTests
{
    internal sealed class FakeHid : IHidDevice
    {
        public List<byte[]> Writes { get; } = new();
        public int VendorId => StrimerProtocol.VendorId;
        public int ProductId => StrimerProtocol.ProductId;
        public string Path => "fake";
        public string? Serial => null;
        public int UsagePage => StrimerProtocol.VendorUsagePage;
        public int Usage => StrimerProtocol.VendorUsage;
        public bool SetFeature(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool Write(ReadOnlySpan<byte> report) { Writes.Add(report.ToArray()); return true; }
        public bool SetOutputReport(ReadOnlySpan<byte> report) => true;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }

    private static List<byte[]> Commit(StrimerLightingSettings ls)
    {
        var hid = new FakeHid();
        var hub = new StrimerHub();
        hub.Attach(hid);
        StrimerLightingFrameWriter.CommitFirmwareMode(hub, StrimerEffectLooks.BuildPlan(ls, new MasterLevel(1f, false)));
        return hid.Writes;
    }

    private static StrimerLightingSettings With(string mode, int? speed = null, int direction = 0, int brightness = 4)
    {
        var ls = new StrimerLightingSettings { Mode = mode, Direction = direction, Brightness = brightness };
        if (speed.HasValue) ls.ModeSpeeds[mode] = speed.Value;
        return ls;
    }

    private static byte[] Head(byte[] report, int n) => report.Take(n).ToArray();

    [Fact]
    public void Drizzling_sends_effect_then_six_colors_to_each_channel_first_port_then_one_latch()
    {
        var w = Commit(With("drizzling"));

        Assert.Equal(5, w.Count);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x25, 0xFE, 0x01, 0x00, 0x00 }, Head(w[0], 7));
        Assert.Equal(
            new byte[]
            {
                0xE0, 0x30,
                0xFF, 0x00, 0x00,
                0xFF, 0x00, 0x69,
                0xFF, 0x00, 0xD7,
                0x00, 0x00, 0xFF,
                0x00, 0xFF, 0x00,
                0xAA, 0xFF, 0x00,
                0x00,
            },
            Head(w[1], 21));
        Assert.Equal(new byte[] { 0xE0, 0x16, 0x25, 0xFE, 0x01, 0x00, 0x00 }, Head(w[2], 7));
        Assert.Equal(new byte[] { 0xE0, 0x36 }, Head(w[3], 2));
        Assert.Equal(new byte[] { 0xE0, 0x2C, 0x00, 0x41, 0x00, 0x00, 0x00, 0x00 }, Head(w[4], 8));
    }

    [Fact]
    public void Drizzling_uses_the_saved_palette_and_reverse_direction()
    {
        var ls = With("drizzling", speed: 0, direction: 1, brightness: 1);
        ls.ModeColors["drizzling"] = new List<string> { "#102030", "#ff6900", "#ffd700", "#00ff00", "#0000ff", "#aa00ff" };
        var w = Commit(ls);

        Assert.Equal(new byte[] { 0xE0, 0x10, 0x25, 0x02, 0x00, 0x03, 0x00 }, Head(w[0], 7));
        Assert.Equal(new byte[] { 0xE0, 0x30, 0x10, 0x30, 0x20 }, Head(w[1], 5));
    }

    [Fact]
    public void Breathing_fills_every_led_with_its_one_color_on_all_twelve_ports()
    {
        var ls = With("breathing", speed: 2);
        ls.ModeColors["breathing"] = new List<string> { "#aa5500" };
        var w = Commit(ls);

        Assert.Equal(25, w.Count);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x02, 0x00, 0x00, 0x00, 0x00 }, Head(w[0], 7));
        Assert.Equal(new byte[] { 0xE0, 0x30, 0xAA, 0x00, 0x55 }, Head(w[1], 5));
        var last = w[1].Skip(2).Take(27 * 3).ToArray();
        Assert.Equal(27, last.Where((_, i) => i % 3 == 0).Count(b => b == 0xAA));
        Assert.Equal(0x00, w[1][2 + 27 * 3]);
        Assert.Equal(new byte[] { 0xE0, 0x1B }, Head(w[22], 2));
        Assert.Equal(new byte[] { 0xE0, 0x2C, 0x0F, 0xFF }, Head(w[24], 4));
    }

    [Fact]
    public void Mixing_sends_two_colors_and_the_wave_direction_byte_carries_through()
    {
        var mixing = Commit(With("mixing", direction: 1));
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x1A, 0x00, 0x00, 0x00, 0x00 }, Head(mixing[0], 7));
        Assert.Equal(new byte[] { 0xE0, 0x30, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00 }, Head(mixing[1], 9));

        var wave = Commit(With("wave", speed: 3, direction: 1));
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x18, 0xFF, 0x00, 0x00, 0x00 }, Head(wave[0], 7));
        Assert.Equal(25, wave.Count);
    }

    [Fact]
    public void Colorless_modes_send_the_default_six_so_the_firmware_has_a_palette()
    {
        var w = Commit(With("rainbow", speed: 2));
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x05, 0x00, 0x01, 0x00, 0x00 }, Head(w[0], 7));
        Assert.Equal(new byte[] { 0xE0, 0x30, 0xFF, 0x00, 0x00, 0xFF, 0x00, 0x69 }, Head(w[1], 8));
        Assert.Equal(0x00, w[1][2 + 18]);
    }

    [Fact]
    public void Channel_wide_mode_without_direction_sends_the_zero_direction_byte()
    {
        var w = Commit(With("ripple", speed: 2));
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x22, 0x00, 0x00, 0x00, 0x00 }, Head(w[0], 7));
        Assert.Equal(new byte[] { 0xE0, 0x2C, 0x00, 0x41 }, Head(w[4], 4));
    }

    [Fact]
    public void Off_sends_the_effect_to_every_port_without_colors()
    {
        var w = Commit(With("off"));
        Assert.Equal(13, w.Count);
        Assert.All(w.Take(12), r => Assert.Equal(0x10, r[1] & 0xF0));
    }

    [Fact]
    public void Static_scales_its_color_by_the_brightness_level_and_sends_no_speed()
    {
        var ls = With("static", brightness: 2);
        ls.ModeColors["static"] = new List<string> { "#ff8040" };
        var w = Commit(ls);

        Assert.Equal(25, w.Count);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x01, 0x00, 0x00, 0x02, 0x00 }, Head(w[0], 7));
        Assert.Equal(new byte[] { 0xE0, 0x30, 0x7F, 0x20, 0x40 }, Head(w[1], 5));
    }

    [Fact]
    public void Effect_speed_starts_at_the_effects_default_until_the_user_sets_one()
    {
        var mixing = Commit(With("mixing"));
        var drizzling = Commit(With("drizzling"));
        Assert.Equal(0x00, mixing[0][3]);
        Assert.Equal(0xFE, drizzling[0][3]);
        Assert.Equal(0x01, Commit(With("drizzling", speed: 1))[0][3]);
    }

    [Fact]
    public void A_cable_and_a_port_can_play_their_own_effect()
    {
        var ls = With("rainbow", speed: 2);
        ls.Targets["ch1"] = new StrimerTargetLook { Mode = "ripple", Brightness = 4 };
        var w = Commit(ls);
        Assert.Equal(6 * 2 + 2 + 1, w.Count);
        Assert.Equal(new byte[] { 0xE0, 0x10, 0x05 }, Head(w[0], 3));
        Assert.Equal(new byte[] { 0xE0, 0x16, 0x22 }, Head(w[12], 3));
        Assert.Equal(new byte[] { 0xE0, 0x2C, 0x00, 0x7F }, Head(w[14], 4));

        ls.Targets["p2"] = new StrimerTargetLook { Mode = "wave", Brightness = 4 };
        var withPort = Commit(ls);
        Assert.Equal(new byte[] { 0xE0, 0x12, 0x18 }, Head(withPort[4], 3));
        Assert.Equal(new byte[] { 0xE0, 0x11, 0x05 }, Head(withPort[2], 3));
    }

    [Fact]
    public void A_channel_wide_effect_on_the_shared_look_ignores_port_overrides_on_that_cable()
    {
        var ls = With("drizzling");
        ls.Targets["p1"] = new StrimerTargetLook { Mode = "wave", Brightness = 4 };
        var w = Commit(ls);
        Assert.Equal(5, w.Count);
        Assert.Equal(new byte[] { 0xE0, 0x2C, 0x00, 0x41 }, Head(w[4], 4));
    }

    [Fact]
    public void A_port_cannot_play_a_channel_wide_effect()
    {
        var ls = With("rainbow", speed: 2);
        ls.Targets["p3"] = new StrimerTargetLook { Mode = "ripple", Brightness = 4 };
        var w = Commit(ls);
        Assert.Equal(new byte[] { 0xE0, 0x13, 0x05 }, Head(w[6], 3));
    }
}
