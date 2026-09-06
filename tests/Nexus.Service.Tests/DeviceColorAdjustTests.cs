using Nexus.Service.Lighting;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Covers the per-device colour-tuning trim every frame writer applies.
/// Identity has to stay byte-exact (an untuned device must not drift), and the
/// default-constructed struct has to read as identity too - the writers pass
/// `default` on paths with no preference, and three zeroed gains there would
/// drive the strip black.
/// </summary>
public class DeviceColorAdjustTests
{
    [Fact]
    public void DefaultStruct_IsIdentity_AndPassesColourThrough()
    {
        DeviceColorAdjust adjust = default;
        Assert.True(adjust.IsIdentity);
        adjust.Apply(10, 120, 250, 1.0, out var r, out var g, out var b);
        Assert.Equal(10, r);
        Assert.Equal(120, g);
        Assert.Equal(250, b);
    }

    [Fact]
    public void NeutralValues_ResolveToIdentity()
    {
        Assert.True(DeviceColorAdjust.Create(1f, 1f, 1f, 0f, 1f).IsIdentity);
    }

    [Fact]
    public void ChannelGain_ScalesOnlyItsOwnChannel()
    {
        var adjust = DeviceColorAdjust.Create(0.5f, 1f, 1f, 0f, 1f);
        Assert.False(adjust.IsIdentity);
        adjust.Apply(200, 200, 200, 1.0, out var r, out var g, out var b);
        Assert.Equal(100, r);
        Assert.Equal(200, g);
        Assert.Equal(200, b);
    }

    [Fact]
    public void WarmShift_LiftsRedAndCutsBlue_CoolShiftIsTheMirror()
    {
        DeviceColorAdjust.Create(1f, 1f, 1f, 1f, 1f).Apply(100, 100, 100, 1.0, out var wr, out var wg, out var wb);
        Assert.True(wr > 100);
        Assert.Equal(100, wg);
        Assert.True(wb < 100);

        DeviceColorAdjust.Create(1f, 1f, 1f, -1f, 1f).Apply(100, 100, 100, 1.0, out var cr, out var cg, out var cb);
        Assert.Equal(wb, cr);
        Assert.Equal(100, cg);
        Assert.Equal(wr, cb);
    }

    [Fact]
    public void ZeroSaturation_CollapsesToLuma()
    {
        var adjust = DeviceColorAdjust.Create(1f, 1f, 1f, 0f, 0f);
        adjust.Apply(255, 0, 0, 1.0, out var r, out var g, out var b);
        // Rec.709 luma of pure red: 0.2126 * 255 ~ 54.
        Assert.Equal(54, r);
        Assert.Equal(r, g);
        Assert.Equal(r, b);
    }

    [Fact]
    public void BrightnessMultiplier_AppliesOnTopOfTheTrim()
    {
        var adjust = DeviceColorAdjust.Create(0.5f, 1f, 1f, 0f, 1f);
        adjust.Apply(200, 200, 200, 0.5, out var r, out var g, out var b);
        Assert.Equal(50, r);
        Assert.Equal(100, g);
        Assert.Equal(100, b);
    }

    [Fact]
    public void OutOfRangeValues_AreClampedRatherThanWrapping()
    {
        var adjust = DeviceColorAdjust.Create(9f, 1f, 1f, 5f, 9f);
        // Red is clamped to 1.7 and then lifted by the (clamped) warm shift, so
        // a bright channel saturates at 255 instead of overflowing the byte.
        adjust.Apply(250, 250, 250, 1.0, out var r, out _, out _);
        Assert.Equal(255, r);
    }

    [Fact]
    public void For_ReadsThePreference_AndFallsBackToIdentity()
    {
        Assert.False(DeviceColorAdjust.For(new LightingDevicePreference { AdjustRed = 1.2f }).IsIdentity);
        Assert.True(DeviceColorAdjust.For(null).IsIdentity);
    }

    // The whole point of taking the preference object: a device nobody tuned
    // resolves to identity off the stored defaults, so the frame writers keep
    // their original per-LED loop.
    [Fact]
    public void For_UntouchedPreference_IsIdentity()
    {
        Assert.True(DeviceColorAdjust.For(new LightingDevicePreference()).IsIdentity);
        Assert.True(DeviceColorAdjust.For(new LightingDevicePreference { Brightness = 40 }).IsIdentity);
    }
}
