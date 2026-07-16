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
    public void StubProvider_GetSettings_RoundTripsRotaryAndPassiveLighting()
    {
        var store = new InMemoryConfigStore();
        var provider = new StubKeebProvider(store);

        provider.SetRotary(new SetRotaryWheelsBody { Left = "ScrollY", Right = "Scale" });
        provider.SetPassiveLighting(new SetPassiveLightingBody
        {
            KeyReactive = true,
            KeyReactiveMask = true,
            KeyReactiveMode = "Ripple",
            KeyReactiveColor = new() { R = 1, G = 2, B = 3, A = 4 },
        });
        provider.SetGameMode(new SetGameModeBody { AltF4 = true, WindowsKey = true });

        var s = provider.GetSettings();

        Assert.Equal("ScrollY", s.RotaryLeft);
        Assert.Equal("Scale", s.RotaryRight);
        Assert.True(s.KeyReactive);
        Assert.True(s.KeyReactiveMask);
        Assert.Equal("Ripple", s.KeyReactiveMode);
        Assert.Equal(3, s.KeyReactiveColor.B);
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
}
