using Nexus.Service.Models.Peripherals.Keeb;
using Nexus.Service.Peripherals.Keeb;
using Xunit;

namespace Nexus.Service.Tests.Keeb;

/// <summary>
/// GetSettings must round-trip every field the panel renders - the settings
/// tab and the rotary editor both restore from this response on page load,
/// so a field missing here silently resets in the UI after every reload.
/// </summary>
public class KeebProviderSettingsTests
{
    [Fact]
    public void StubProvider_GetSettings_RoundTripsRotaryAndGameMode()
    {
        var store = new InMemoryConfigStore();
        var provider = new StubKeebProvider(store);

        provider.SetRotary(new SetRotaryWheelsBody { Left = "ScrollY", Right = "Scale" });
        provider.SetGameMode(new SetGameModeBody { AltF4 = true, WindowsKey = true });

        var s = provider.GetSettings();

        Assert.Equal("ScrollY", s.RotaryLeft);
        Assert.Equal("Scale", s.RotaryRight);
        Assert.True(s.AltF4Disabled);
        Assert.True(s.WindowsKeyDisabled);
        Assert.False(s.AltTabDisabled);
    }

    [Fact]
    public void StubProvider_GetSettings_DefaultsBeforeAnyWrite()
    {
        var provider = new StubKeebProvider(new InMemoryConfigStore());

        var s = provider.GetSettings();

        // Install defaults seed the persisted state; the response must carry
        // them rather than empty strings.
        Assert.Equal(Defaults.InstallDefaults.Keeb.RotaryLeft, s.RotaryLeft);
        Assert.Equal(Defaults.InstallDefaults.Keeb.RotaryRight, s.RotaryRight);
    }

    [Fact]
    public void StubProvider_RotaryFunctions_MatchTheRealCodecList()
    {
        var provider = new StubKeebProvider(new InMemoryConfigStore());
        Assert.Equal(
            Nexus.Service.Peripherals.Hyte.Keeb.KeebSettingsCodec.RotaryFunctions,
            provider.GetRotaryFunctions());
    }

    [Fact]
    public void StubProvider_SetLayerKey_RefusesHonestly()
    {
        var provider = new StubKeebProvider(new InMemoryConfigStore());
        var r = provider.SetLayerKey(0, new SetLayerKeyBody { X = 2, Y = 0, Func = "A", Mode = "StandardKey" });
        Assert.True(r.Error);
        Assert.False(r.WroteDevice);
    }

    [Fact]
    public void StubProvider_SetMacro_PersistsButReportsNoDeviceWrite()
    {
        var provider = new StubKeebProvider(new InMemoryConfigStore());
        var r = provider.SetMacro(3, new SetMacroBody
        {
            Keys = new()
            {
                new MacroKey { Key = "KeyA", Type = "Make", Duration = 10 },
                new MacroKey { Key = "Bogus", Type = "Make", Duration = 10 },
            },
        });
        Assert.False(r.Error);
        Assert.False(r.WroteDevice);
        Assert.Equal(new[] { "Bogus" }, r.DroppedKeys);
        Assert.Equal(2, provider.GetMacro(3).Keys.Count);
    }

    private sealed class NoDevices : Nexus.Service.Peripherals.Hid.IHidEnumerator
    {
        public IReadOnlyList<Nexus.Service.Peripherals.Hid.HidDeviceInfo> Find(int vendorId, int productId) => System.Array.Empty<Nexus.Service.Peripherals.Hid.HidDeviceInfo>();
        public IReadOnlyList<Nexus.Service.Peripherals.Hid.HidDeviceInfo> FindAll() => System.Array.Empty<Nexus.Service.Peripherals.Hid.HidDeviceInfo>();
        public Nexus.Service.Peripherals.Hid.IHidDevice? Open(string path, bool forInput = false) => null;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Firmware_brightness_slider_never_moves_the_global_master(bool effectRunning)
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Lighting.GlobalBrightness = 0.8f);
        var hub = new Nexus.Service.Peripherals.Hyte.Keeb.KeebHub(new NoDevices());
        var applier = new Nexus.Service.Peripherals.Hyte.Keeb.KeebSettingsApplier(hub, store, new Nexus.Service.Sockets.MultiplexHub());
        using var engine = new Nexus.Service.Lighting.Engine.LightingEngine();
        if (effectRunning) engine.SetEffect(new Nexus.Service.Lighting.Engine.Effects.BlackEffect());
        var provider = new RealKeebProvider(store, hub, applier, engine);

        provider.SetFirmwareLighting(new SetFirmwareLightingBody
        {
            AnimationMode = "Static", Speed = "Standard", Direction = "LeftToRight", Brightness = 30,
        });

        var s = store.Load();
        Assert.Equal(30, s.Keeb.FirmwareLighting.Brightness);
        Assert.Equal(0.8f, s.Lighting.GlobalBrightness);
    }

    [Theory]
    [InlineData(100, 1.0)]
    [InlineData(30, 0.3)]
    [InlineData(0, 0.0)]
    [InlineData(250, 1.0)]
    [InlineData(-5, 0.0)]
    public void Keeb_stream_is_scaled_by_its_own_firmware_level(int level, double expected)
    {
        var store = new InMemoryConfigStore();
        store.Update(s => s.Keeb.FirmwareLighting.Brightness = level);
        Assert.Equal(expected, Nexus.Service.Lighting.KeebLightingFrameWriter.KeebStreamLevel(store.Load()), 3);
    }

    private static Nexus.Service.Persistence.NexusSettings Parse(string json) =>
        System.Text.Json.JsonSerializer.Deserialize(json, Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings)!;

    [Fact]
    public void Fresh_install_firmware_brightness_defaults_to_full()
    {
        Assert.Equal(100, Parse("{}").Keeb.FirmwareLighting.Brightness);
        Assert.Equal(100, new StubKeebProvider(new InMemoryConfigStore()).GetSettings().Brightness);
    }

    [Fact]
    public void A_stored_firmware_brightness_is_not_rewritten_by_the_new_default()
    {
        var s = Parse("{\"keeb\":{\"firmwareLighting\":{\"brightness\":80}}}");
        Assert.Equal(80, s.Keeb.FirmwareLighting.Brightness);
    }

    [Fact]
    public void An_untouched_default_firmware_brightness_is_written_to_settings_json()
    {
        // WhenWritingNull only: an int is always persisted, so an install that ever saved
        // settings holds its old default as a stored value and the new default never reaches it.
        var json = System.Text.Json.JsonSerializer.Serialize(
            new Nexus.Service.Persistence.NexusSettings(), Nexus.Service.Serialization.PersistenceJsonContext.Default.NexusSettings);
        var block = json[json.IndexOf("\"firmwareLighting\"", StringComparison.Ordinal)..];
        Assert.Contains("\"brightness\": 100", block[..block.IndexOf('}')]);
    }
}
