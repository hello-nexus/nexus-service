using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Peripherals.Hyte.Keeb;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Keeb;

/// <summary>
/// A brightness-knob detent steps the GLOBAL master while a software effect streams and never
/// touches the keeb's own firmware level (the stream is global x level). Following the knob uses
/// the input worker's own HID handle: reading the settings page shares KeebHub's IO lock with the
/// LED stream and stalled frames. Stream-level behavior is in <see cref="KeebStreamBrightnessTests"/>.
/// </summary>
public class KeebKnobBrightnessTests
{
    private sealed class NoDevices : IHidEnumerator
    {
        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) => System.Array.Empty<HidDeviceInfo>();
        public IReadOnlyList<HidDeviceInfo> FindAll() => System.Array.Empty<HidDeviceInfo>();
        public IHidDevice? Open(string path, bool forInput = false) => null;
    }

    private static KeebSettingsApplier Applier(IConfigStore store, bool streaming)
    {
        var applier = new KeebSettingsApplier(new KeebHub(new NoDevices()), store, new Nexus.Service.Sockets.MultiplexHub());
        if (streaming) applier.SuppressFirmwareAnimation();
        return applier;
    }

    private static IConfigStore StoreWith(string left, string right, int level, float global)
    {
        var store = new Nexus.Service.Tests.InMemoryConfigStore();
        store.Update(s =>
        {
            s.Keeb.RotaryLeft = left;
            s.Keeb.RotaryRight = right;
            s.Keeb.FirmwareLighting.Brightness = level;
            s.Lighting.GlobalBrightness = global;
        });
        return store;
    }

    [Fact]
    public void A_detent_while_streaming_steps_global_and_not_the_keeb_level()
    {
        var store = StoreWith("VolumeAdjustment", "BrightnessAdjustment", 30, 0.5f);
        Assert.True(Applier(store, streaming: true).HandleEncoderScroll(KeebProtocol.KeebEncoder.Right, up: true));
        var s = store.Load();
        Assert.Equal(0.55f, s.Lighting.GlobalBrightness, 3);
        Assert.Equal(30, s.Keeb.FirmwareLighting.Brightness);
    }

    [Fact]
    public void Scrolling_down_lowers_global()
    {
        var store = StoreWith("BrightnessAdjustment", "VolumeAdjustment", 100, 0.5f);
        Applier(store, streaming: true).HandleEncoderScroll(KeebProtocol.KeebEncoder.Left, up: false);
        Assert.Equal(0.45f, store.Load().Lighting.GlobalBrightness, 3);
    }

    [Fact]
    public void A_detent_while_not_streaming_is_left_to_the_firmware()
    {
        var store = StoreWith("VolumeAdjustment", "BrightnessAdjustment", 30, 0.5f);
        Assert.True(Applier(store, streaming: false).HandleEncoderScroll(KeebProtocol.KeebEncoder.Right, up: true));
        var s = store.Load();
        Assert.Equal(0.5f, s.Lighting.GlobalBrightness, 3);
        Assert.Equal(30, s.Keeb.FirmwareLighting.Brightness);
    }

    [Fact]
    public void An_encoder_assigned_to_volume_is_left_alone()
    {
        var store = StoreWith("VolumeAdjustment", "BrightnessAdjustment", 50, 0.5f);
        var handled = Applier(store, streaming: true).HandleEncoderScroll(KeebProtocol.KeebEncoder.Left, up: true);
        Assert.False(handled);
        Assert.Equal(0.5f, store.Load().Lighting.GlobalBrightness, 3);
    }

    [Theory]
    [InlineData(0.98f, true, 1.0f)]
    [InlineData(0.02f, false, 0.0f)]
    public void It_clamps_global_at_the_ends(float start, bool up, float expected)
    {
        var store = StoreWith("VolumeAdjustment", "BrightnessAdjustment", 50, start);
        Applier(store, streaming: true).HandleEncoderScroll(KeebProtocol.KeebEncoder.Right, up);
        Assert.Equal(expected, store.Load().Lighting.GlobalBrightness, 3);
    }
}
