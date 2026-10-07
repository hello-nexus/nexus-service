using System.Linq;
using Nexus.Service.Peripherals.BulkPanels;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class HydroShift2RingEffectsTests
{
    // FNV-1a over every frame at brightness 0-4 x both directions, recovered from L-Connect's
    // HydroShift II source by lian-li-linux (h2/native_tests.rs).
    [Theory]
    [InlineData(HydroShift2RingEffects.Rainbow, 0x2ceb1fd33c6e9275UL)]
    [InlineData(HydroShift2RingEffects.RainbowMorph, 0xb48f5bfdbf847985UL)]
    [InlineData(HydroShift2RingEffects.Static, 0x079beb6046e212c5UL)]
    [InlineData(HydroShift2RingEffects.Breathing, 0xf3ab73657e090b45UL)]
    [InlineData(HydroShift2RingEffects.Runway, 0x61f83d4f47e16b15UL)]
    [InlineData(HydroShift2RingEffects.Meteor, 0xbdb8873410f7c379UL)]
    [InlineData(HydroShift2RingEffects.TaiChi, 0xcbc2367f5c002295UL)]
    [InlineData(HydroShift2RingEffects.Twinkle, 0x95f2a939301888f5UL)]
    [InlineData(HydroShift2RingEffects.Voice, 0x064c1b3883dfd025UL)]
    [InlineData(HydroShift2RingEffects.Pump, 0xf320cbb87d04712dUL)]
    [InlineData(HydroShift2RingEffects.Bounce, 0xaf1ca8470a567d51UL)]
    public void Frames_match_the_vendor_effect(string mode, ulong expected)
    {
        (byte, byte, byte)[] colors = { (255, 1, 127), (5, 200, 17), (90, 33, 240), (18, 77, 150) };
        ulong hash = 0xcbf29ce484222325;
        for (int brightness = 0; brightness <= 4; brightness++)
        {
            foreach (var reverse in new[] { false, true })
            {
                var (frames, _) = HydroShift2RingEffects.Render(mode, colors, brightness, 0, reverse);
                foreach (var b in System.BitConverter.GetBytes(frames.Count))
                {
                    hash = (hash ^ b) * 0x100000001b3;
                }
                foreach (var b in frames.SelectMany(f => f))
                {
                    hash = (hash ^ b) * 0x100000001b3;
                }
            }
        }
        Assert.Equal(expected, hash);
    }

    [Theory]
    [InlineData(HydroShift2RingEffects.Rainbow, 0, 140)]
    [InlineData(HydroShift2RingEffects.Rainbow, 4, 60)]
    [InlineData(HydroShift2RingEffects.RainbowMorph, 0, 77)]
    public void Speed_sets_the_vendor_frame_interval(string mode, int speed, int ticks)
    {
        Assert.Equal(ticks, HydroShift2RingEffects.Render(mode, [], 4, speed, false).IntervalTicks);
    }

    [Fact]
    public void Unset_palette_falls_back_to_vendor_colours_and_white_is_current_limited()
    {
        var (unset, _) = HydroShift2RingEffects.Render(HydroShift2RingEffects.Static, [], 4, 0, false);
        Assert.Equal(new byte[] { 254, 0, 0 }, unset[0][..3]);
        var (white, _) = HydroShift2RingEffects.Render(HydroShift2RingEffects.Static, [(255, 255, 255)], 4, 0, false);
        Assert.Equal(new byte[] { 184, 184, 184 }, white[0][..3]);
    }

    [Fact]
    public void Off_is_one_dark_frame()
    {
        var (frames, _) = HydroShift2RingEffects.Render(HydroShift2RingEffects.Off, [], 4, 2, false);
        Assert.All(Assert.Single(frames), b => Assert.Equal(0, b));
    }
}
